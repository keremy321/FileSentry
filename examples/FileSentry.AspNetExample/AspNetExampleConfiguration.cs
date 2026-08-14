using FileSentry.Client;

namespace FileSentry.AspNetExample;

internal static class AspNetExampleConfiguration
{
    public const string BaseUrlVariable = "FILESENTRY_BASE_URL";
    public const string ApiKeyVariable = "FILESENTRY_SERVICE_API_KEY";

    public static FileSentryClientOptions FromConfiguration(IConfiguration configuration) =>
        Create(
            configuration[BaseUrlVariable] ?? configuration["FileSentry:BaseUrl"],
            configuration[ApiKeyVariable] ?? configuration["FileSentry:ApiKey"],
            configuration["FileSentry:PollingIntervalSeconds"],
            configuration["FileSentry:ScanTimeoutSeconds"]);

    internal static FileSentryClientOptions Create(
        string? baseUrl,
        string? apiKey,
        string? pollingIntervalSeconds,
        string? scanTimeoutSeconds)
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

        int pollingSeconds = ParsePositiveSeconds(
            pollingIntervalSeconds,
            defaultValue: 1,
            "FileSentry:PollingIntervalSeconds");
        int timeoutSeconds = ParsePositiveSeconds(
            scanTimeoutSeconds,
            defaultValue: 300,
            "FileSentry:ScanTimeoutSeconds");
        var options = new FileSentryClientOptions
        {
            BaseAddress = baseAddress,
            ApiKey = apiKey,
            PollingInterval = TimeSpan.FromSeconds(pollingSeconds),
            ScanTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        try
        {
            options.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "FileSentry client configuration is invalid. Check the base URL, service API key, polling interval, and timeout.",
                exception);
        }

        return options;
    }

    private static int ParsePositiveSeconds(
        string? value,
        int defaultValue,
        string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int seconds) || seconds <= 0)
        {
            throw new InvalidOperationException($"{settingName} must be a positive integer.");
        }

        return seconds;
    }
}
