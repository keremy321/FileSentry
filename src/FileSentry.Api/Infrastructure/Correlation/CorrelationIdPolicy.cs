namespace FileSentry.Api.Infrastructure.Correlation;

public static class CorrelationIdPolicy
{
    public const string HeaderName = "X-Correlation-ID";
    public const int MinimumLength = 8;
    public const int MaximumLength = 64;

    public static bool IsValid(string? value)
    {
        return value is { Length: >= MinimumLength and <= MaximumLength }
            && value.All(character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_' or '.');
    }

    public static string Create() => Guid.NewGuid().ToString("N");
}
