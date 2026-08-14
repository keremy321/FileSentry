using FileSentry.Client;

namespace FileSentry.ConsoleExample;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project examples/FileSentry.ConsoleExample -- <file-path>");
            return 2;
        }

        FileSentryClientOptions options;
        try
        {
            options = ConsoleExampleConfiguration.FromEnvironment(
                Environment.GetEnvironmentVariable);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        string filePath = Path.GetFullPath(args[0]);
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine("The supplied file does not exist.");
            return 2;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using var client = new FileSentryClient(options);
            await using var input = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            FileUpload upload = await client.UploadAsync(
                input,
                Path.GetFileName(filePath),
                cancellationSource.Token);
            Console.WriteLine($"File ID: {upload.FileId}");
            Console.WriteLine($"Initial status: {upload.Status}");

            FileMetadata result = await client.WaitForScanAsync(
                upload.FileId,
                cancellationSource.Token);
            Console.WriteLine($"Final status: {result.Status}");

            return await HandleTerminalResultAsync(
                client,
                result,
                cancellationSource.Token);
        }
        catch (FileSentryPollingTimeoutException)
        {
            Console.Error.WriteLine("The scan did not finish within the configured timeout.");
            return 4;
        }
        catch (FileSentryApiException exception)
        {
            string code = string.IsNullOrWhiteSpace(exception.Code)
                ? "unknown"
                : exception.Code;
            Console.Error.WriteLine(
                $"FileSentry rejected the request (HTTP {(int)exception.StatusCode}, code {code}).");
            return 5;
        }
        catch (FileSentryProtocolException)
        {
            Console.Error.WriteLine("FileSentry returned an unexpected response; processing stopped.");
            return 5;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("FileSentry could not be reached.");
            return 5;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation canceled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> HandleTerminalResultAsync(
        IFileSentryClient client,
        FileMetadata result,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case FileStatus.Clean:
                await using (Stream clean = await client.DownloadAsync(
                    result.FileId,
                    cancellationToken))
                {
                    await clean.CopyToAsync(Stream.Null, cancellationToken);
                }

                Console.WriteLine("Clean content was streamed successfully without being displayed.");
                return 0;

            case FileStatus.Infected:
                Console.Error.WriteLine("The file was rejected because malware was detected.");
                return 3;

            case FileStatus.ScanFailed:
                Console.Error.WriteLine(
                    "The file was not processed because scanning did not complete conclusively.");
                return 4;

            case FileStatus.Deleted:
                Console.Error.WriteLine("The file is no longer available.");
                return 5;

            default:
                Console.Error.WriteLine("The file did not reach a safe terminal state.");
                return 5;
        }
    }
}
