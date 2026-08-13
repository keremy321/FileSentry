using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileSentry.Api.Application.Files;

public sealed class FileFormatValidator
{
    private const long MaximumRequiredDocxPartSize = 2 * 1024 * 1024;
    private const int MaximumDocxEntryCount = 2048;

    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] PdfEndMarker = "%%EOF"u8.ToArray();
    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    ];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] JpegEndMarker = [0xFF, 0xD9];
    private static readonly byte[] ZipHeader = [0x50, 0x4B, 0x03, 0x04];

    public async Task<FileFormatValidationResult> ValidateAsync(
        string trustedFilePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            trustedFilePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        byte[] header = new byte[8];
        int headerLength = await stream.ReadAsync(header, cancellationToken);
        ReadOnlySpan<byte> availableHeader = header.AsSpan(0, headerLength);

        if (availableHeader.StartsWith(PdfHeader))
        {
            bool hasEndMarker = await HasPdfEndMarkerAsync(stream, cancellationToken);
            return new FileFormatValidationResult(SupportedFileFormat.Pdf, hasEndMarker);
        }

        if (availableHeader.StartsWith(PngSignature))
        {
            return new FileFormatValidationResult(SupportedFileFormat.Png, true);
        }

        if (availableHeader.StartsWith(JpegHeader))
        {
            bool hasEndMarker = await HasExactEndMarkerAsync(
                stream,
                JpegEndMarker,
                cancellationToken);
            return new FileFormatValidationResult(SupportedFileFormat.Jpeg, hasEndMarker);
        }

        if (availableHeader.StartsWith(ZipHeader))
        {
            await stream.DisposeAsync();
            bool isValidDocx = ValidateDocxPackage(trustedFilePath);
            return new FileFormatValidationResult(
                isValidDocx ? SupportedFileFormat.Docx : null,
                isValidDocx,
                IsZipContainer: true);
        }

        return new FileFormatValidationResult(null, false);
    }

    private static async Task<bool> HasPdfEndMarkerAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        const int maximumTailLength = 1024;
        int tailLength = (int)Math.Min(stream.Length, maximumTailLength);
        byte[] tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        await stream.ReadExactlyAsync(tail, cancellationToken);

        int markerIndex = tail.AsSpan().LastIndexOf(PdfEndMarker);
        if (markerIndex < 0)
        {
            return false;
        }

        foreach (byte trailingByte in tail.AsSpan(markerIndex + PdfEndMarker.Length))
        {
            if (trailingByte is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HasExactEndMarkerAsync(
        FileStream stream,
        byte[] endMarker,
        CancellationToken cancellationToken)
    {
        if (stream.Length < endMarker.Length)
        {
            return false;
        }

        byte[] actualEnd = new byte[endMarker.Length];
        stream.Seek(-endMarker.Length, SeekOrigin.End);
        await stream.ReadExactlyAsync(actualEnd, cancellationToken);
        return actualEnd.AsSpan().SequenceEqual(endMarker);
    }

    private static bool ValidateDocxPackage(string trustedFilePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(trustedFilePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count is 0 or > MaximumDocxEntryCount
                || archive.Entries.Any(entry => !HasSafePartName(entry.FullName))
                || archive.Entries
                    .GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
            {
                return false;
            }

            ZipArchiveEntry? contentTypes = archive.GetEntry("[Content_Types].xml");
            ZipArchiveEntry? packageRelationships = archive.GetEntry("_rels/.rels");
            ZipArchiveEntry? document = archive.GetEntry("word/document.xml");
            if (contentTypes is null || packageRelationships is null || document is null)
            {
                return false;
            }

            XDocument? contentTypesXml = LoadBoundedXml(contentTypes);
            XDocument? relationshipsXml = LoadBoundedXml(packageRelationships);
            XDocument? documentXml = LoadBoundedXml(document);
            return contentTypesXml is not null
                && relationshipsXml is not null
                && documentXml is not null
                && HasWordDocumentContentType(contentTypesXml)
                && HasOfficeDocumentRelationship(relationshipsXml)
                && HasWordDocumentRoot(documentXml);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException)
        {
            return false;
        }
    }

    private static XDocument? LoadBoundedXml(ZipArchiveEntry entry)
    {
        if (entry.Length is <= 0 or > MaximumRequiredDocxPartSize)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumRequiredDocxPartSize,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool HasWordDocumentContentType(XDocument contentTypes)
    {
        XNamespace contentTypesNamespace =
            "http://schemas.openxmlformats.org/package/2006/content-types";
        const string wordDocumentContentType =
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

        return contentTypes.Root?.Name == contentTypesNamespace + "Types"
            && contentTypes.Root
                .Elements(contentTypesNamespace + "Override")
                .Any(element =>
                    string.Equals(
                        element.Attribute("PartName")?.Value,
                        "/word/document.xml",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        element.Attribute("ContentType")?.Value,
                        wordDocumentContentType,
                        StringComparison.Ordinal));
    }

    private static bool HasOfficeDocumentRelationship(XDocument relationships)
    {
        XNamespace relationshipsNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        string[] officeDocumentRelationshipTypes =
        [
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
            "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument"
        ];

        return relationships.Root?.Name == relationshipsNamespace + "Relationships"
            && relationships.Root
                .Elements(relationshipsNamespace + "Relationship")
                .Any(element =>
                    officeDocumentRelationshipTypes.Contains(
                        element.Attribute("Type")?.Value,
                        StringComparer.Ordinal)
                    && !string.Equals(
                        element.Attribute("TargetMode")?.Value,
                        "External",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        element.Attribute("Target")?.Value.TrimStart('/'),
                        "word/document.xml",
                        StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasWordDocumentRoot(XDocument document)
    {
        string[] wordProcessingNamespaces =
        [
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
            "http://purl.oclc.org/ooxml/wordprocessingml/main"
        ];

        return document.Root?.Name.LocalName == "document"
            && wordProcessingNamespaces.Contains(
                document.Root.Name.NamespaceName,
                StringComparer.Ordinal);
    }

    private static bool HasSafePartName(string partName)
    {
        if (string.IsNullOrWhiteSpace(partName)
            || partName.StartsWith('/')
            || partName.StartsWith('\\'))
        {
            return false;
        }

        return !partName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
    }
}
