using System.IO.Compression;
using System.Text;
using FileSentry.Api.Application.Files;

namespace FileSentry.UnitTests;

public sealed class FileFormatValidatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"filesentry-format-tests-{Guid.NewGuid():N}");
    private readonly FileFormatValidator _validator = new();

    public FileFormatValidatorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("pdf", SupportedFileFormat.Pdf)]
    [InlineData("png", SupportedFileFormat.Png)]
    [InlineData("jpeg", SupportedFileFormat.Jpeg)]
    public async Task ValidateAsync_RecognizesSupportedSignatures(
        string sample,
        SupportedFileFormat expectedFormat)
    {
        byte[] content = sample switch
        {
            "pdf" => "%PDF-1.7\n%%EOF\n"u8.ToArray(),
            "png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            "jpeg" => [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9],
            _ => throw new ArgumentOutOfRangeException(nameof(sample))
        };
        string path = await WriteAsync(content);

        FileFormatValidationResult result = await _validator.ValidateAsync(path, default);

        Assert.True(result.IsStructurallyValid);
        Assert.Equal(expectedFormat, result.DetectedFormat);
    }

    [Fact]
    public async Task ValidateAsync_ValidWordOpenXmlPackage_RecognizesDocx()
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        await using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
                </Relationships>
                """);
            AddEntry(archive, "word/document.xml", """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body /></w:document>
                """);
        }

        FileFormatValidationResult result = await _validator.ValidateAsync(path, default);

        Assert.True(result.IsStructurallyValid);
        Assert.True(result.IsZipContainer);
        Assert.Equal(SupportedFileFormat.Docx, result.DetectedFormat);
    }

    [Fact]
    public async Task ValidateAsync_GenericZip_IsNotAcceptedAsDocx()
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        await using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddEntry(archive, "payload.txt", "not a Word package");
        }

        FileFormatValidationResult result = await _validator.ValidateAsync(path, default);

        Assert.False(result.IsStructurallyValid);
        Assert.True(result.IsZipContainer);
        Assert.Null(result.DetectedFormat);
    }

    [Fact]
    public async Task ValidateAsync_MalformedSignature_IsRejected()
    {
        string path = await WriteAsync("not a supported file"u8.ToArray());

        FileFormatValidationResult result = await _validator.ValidateAsync(path, default);

        Assert.False(result.IsStructurallyValid);
        Assert.Null(result.DetectedFormat);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteAsync(byte[] content)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
