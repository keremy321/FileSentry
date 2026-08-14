using FileSentry.Client;

namespace FileSentry.ConsoleExample;

internal static class ConsoleExampleConfiguration
{
    public const string BaseUrlVariable = "FILESENTRY_BASE_URL";
    public const string ApiKeyVariable = "FILESENTRY_SERVICE_API_KEY";

    public static FileSentryClientOptions FromEnvironment(
        Func<string, string?> readVariable) => Create(
            readVariable(BaseUrlVariable),
            readVariable(ApiKeyVariable));

    internal static FileSentryClientOptions Create(string? baseUrl, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"Set {BaseUrlVariable} to the FileSentry API base URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Set {ApiKeyVariable} to the FileSentry service API key.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseAddress))
        {
            throw new InvalidOperationException(
                $"{BaseUrlVariable} must be an absolute HTTP or HTTPS URL.");
        }

        var options = new FileSentryClientOptions
        {
            BaseAddress = baseAddress,
            ApiKey = apiKey
        };

        try
        {
            options.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "FileSentry client configuration is invalid. Check the base URL and service API key.",
                exception);
        }

        return options;
    }
}
