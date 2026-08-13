namespace FileSentry.Api.Infrastructure.Options;

public sealed class UploadRateLimitOptions
{
    public const string SectionName = "UploadRateLimit";
    public const string PolicyName = "upload";

    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 60;
}
