namespace FileSentry.Api.Infrastructure.Options;

public sealed class ServiceAuthenticationOptions
{
    public const string SectionName = "ServiceAuthentication";
    public const int MinimumApiKeyBytes = 32;
    public const int MaximumApiKeyLength = 512;
    public const int MaximumServiceNameLength = 64;

    public bool Enabled { get; set; }

    public Guid ServiceId { get; set; }

    public string ServiceName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}
