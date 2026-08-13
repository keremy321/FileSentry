using System.Text;

namespace FileSentry.Api.Application.Files;

public static class FileNameSanitizer
{
    public const int MaximumLength = 255;

    private static readonly HashSet<char> UnsafeCharacters =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    ];

    public static string Sanitize(string untrustedFileName)
    {
        string validUnicode = string.Concat(
            untrustedFileName.EnumerateRunes().Select(rune => rune.ToString()));
        string normalizedSeparators = validUnicode
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/');
        string leafName = normalizedSeparators[(normalizedSeparators.LastIndexOf('/') + 1)..];

        var builder = new StringBuilder(leafName.Length);
        foreach (char character in leafName)
        {
            builder.Append(char.IsControl(character) || UnsafeCharacters.Contains(character)
                ? '_'
                : character);
        }

        string sanitized = builder.ToString().Trim().Trim('.');
        if (sanitized.Length <= MaximumLength)
        {
            return sanitized;
        }

        string extension = Path.GetExtension(sanitized);
        if (extension.Length >= MaximumLength)
        {
            return sanitized[..MaximumLength];
        }

        string baseName = Path.GetFileNameWithoutExtension(sanitized);
        int baseNameLength = MaximumLength - extension.Length;
        return string.Concat(baseName.AsSpan(0, Math.Min(baseName.Length, baseNameLength)), extension);
    }
}
