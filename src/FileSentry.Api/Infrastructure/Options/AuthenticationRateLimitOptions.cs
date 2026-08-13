namespace FileSentry.Api.Infrastructure.Options;

public sealed class AuthenticationRateLimitOptions
{
    public const string SectionName = "AuthenticationRateLimit";
    public const string PolicyName = "authentication";

    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 60;
}
