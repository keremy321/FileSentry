using FileSentry.Api.Application.Files;

namespace FileSentry.UnitTests;

public sealed class FileNameSanitizerTests
{
    [Fact]
    public void Sanitize_PathTraversal_ReturnsOnlySafeLeafMetadata()
    {
        string result = FileNameSanitizer.Sanitize("../../folder\\report.pdf");

        Assert.Equal("report.pdf", result);
        Assert.DoesNotContain("..", result, StringComparison.Ordinal);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void Sanitize_ControlAndUnsafeCharacters_ReplacesThem()
    {
        string result = FileNameSanitizer.Sanitize(" bad\0<name>?.png. ");

        Assert.Equal("bad__name__.png", result);
    }

    [Fact]
    public void Sanitize_LongName_IsBoundedAndPreservesNormalExtension()
    {
        string result = FileNameSanitizer.Sanitize($"{new string('a', 300)}.docx");

        Assert.Equal(FileNameSanitizer.MaximumLength, result.Length);
        Assert.EndsWith(".docx", result, StringComparison.Ordinal);
    }
}
