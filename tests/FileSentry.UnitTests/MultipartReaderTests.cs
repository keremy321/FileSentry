using FileSentry.Api.Application.Files;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FileSentry.UnitTests;

public sealed class MultipartReaderTests
{
    [Fact]
    public async Task Reader_CanStreamMultipartFormDataContentToItsBoundary()
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent("content"u8.ToArray()), "file", "sample.pdf");
        await using var requestBody = new MemoryStream();
        await multipart.CopyToAsync(requestBody);
        requestBody.Position = 0;
        Assert.True(Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
            multipart.Headers.ContentType!.ToString(),
            out Microsoft.Net.Http.Headers.MediaTypeHeaderValue? contentType));
        string boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value!;
        var reader = new MultipartReader(boundary, requestBody)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = FileIngestionService.MaximumFileSizeBytes + (64 * 1024)
        };

        MultipartSection? section = await reader.ReadNextSectionAsync();
        Assert.NotNull(section);
        using var content = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int bytesRead = await section.Body.ReadAsync(buffer, CancellationToken.None);
            if (bytesRead == 0)
            {
                break;
            }

            await content.WriteAsync(buffer.AsMemory(0, bytesRead));
        }

        Assert.Equal("content"u8.ToArray(), content.ToArray());
        Assert.Null(await reader.ReadNextSectionAsync());
    }
}
