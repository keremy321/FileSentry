namespace FileSentry.Api.Application.Files;

public sealed record FileDownload(
    Stream Content,
    string MediaType,
    string DownloadName);
