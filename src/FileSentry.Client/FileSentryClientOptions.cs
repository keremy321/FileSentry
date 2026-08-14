using System.Text;

namespace FileSentry.Client;

/// <summary>Configures FileSentry API access and scan-status polling.</summary>
public sealed class FileSentryClientOptions
{
    /// <summary>
    /// Gets the absolute HTTP or HTTPS API base address without credentials, query,
    /// or fragment.
    /// </summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>Gets the service API key sent in the <c>X-Api-Key</c> header.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Gets the delay between scan-status requests.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum duration of a single scan-status polling operation.</summary>
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Validates all options without creating an HTTP client.</summary>
    /// <exception cref="ArgumentException">An option is missing or invalid.</exception>
    public void Validate() => _ = ValidateAndNormalize();

    internal ValidatedFileSentryClientOptions ValidateAndNormalize()
    {
        ArgumentNullException.ThrowIfNull(BaseAddress);

        if (!BaseAddress.IsAbsoluteUri
            || (BaseAddress.Scheme != Uri.UriSchemeHttp
                && BaseAddress.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(BaseAddress.UserInfo)
            || !string.IsNullOrEmpty(BaseAddress.Query)
            || !string.IsNullOrEmpty(BaseAddress.Fragment))
        {
            throw new ArgumentException(
                "The FileSentry base address must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.",
                nameof(BaseAddress));
        }

        if (string.IsNullOrWhiteSpace(ApiKey)
            || Encoding.UTF8.GetByteCount(ApiKey) < 32
            || ApiKey.Length > 512
            || ApiKey.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException(
                "The FileSentry API key must contain 32 to 512 printable ASCII characters.",
                nameof(ApiKey));
        }

        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollingInterval),
                "The polling interval must be greater than zero.");
        }

        if (ScanTimeout <= TimeSpan.Zero || ScanTimeout < PollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ScanTimeout),
                "The scan timeout must be greater than or equal to the polling interval.");
        }

        string normalizedBaseAddress = BaseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? BaseAddress.AbsoluteUri
            : $"{BaseAddress.AbsoluteUri}/";
        return new ValidatedFileSentryClientOptions(
            new Uri(normalizedBaseAddress, UriKind.Absolute),
            ApiKey,
            PollingInterval,
            ScanTimeout);
    }
}

internal sealed record ValidatedFileSentryClientOptions(
    Uri BaseAddress,
    string ApiKey,
    TimeSpan PollingInterval,
    TimeSpan ScanTimeout);
