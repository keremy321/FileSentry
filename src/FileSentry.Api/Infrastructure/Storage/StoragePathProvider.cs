using FileSentry.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Infrastructure.Storage;

public sealed class StoragePathProvider
{
    public StoragePathProvider(
        IOptions<StorageOptions> options,
        IHostEnvironment hostEnvironment)
    {
        string configuredRoot = options.Value.RootPath;
        string candidateRoot = Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(hostEnvironment.ContentRootPath, configuredRoot);
        RootPath = Path.GetFullPath(candidateRoot);

        string webRootPath = Path.GetFullPath(
            hostEnvironment is IWebHostEnvironment webHostEnvironment
                && !string.IsNullOrWhiteSpace(webHostEnvironment.WebRootPath)
                    ? webHostEnvironment.WebRootPath
                    : Path.Combine(hostEnvironment.ContentRootPath, "wwwroot"));

        if (IsPathWithin(RootPath, webRootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath must not be located inside the web root.");
        }

        TempRootPath = Path.Combine(RootPath, "temp");
        QuarantineRootPath = Path.Combine(RootPath, "quarantine");
        Directory.CreateDirectory(TempRootPath);
        Directory.CreateDirectory(QuarantineRootPath);
    }

    public string RootPath { get; }

    public string TempRootPath { get; }

    public string QuarantineRootPath { get; }

    public string CreateTempPath() => Path.Combine(
        TempRootPath,
        $"{Guid.NewGuid():N}.tmp");

    public string CreateStorageName(Guid fileId) => $"{fileId:N}.quarantine";

    public string GetQuarantinePath(string storageName)
    {
        string candidatePath = Path.GetFullPath(
            Path.Combine(QuarantineRootPath, storageName));
        if (!IsPathWithin(candidatePath, QuarantineRootPath))
        {
            throw new InvalidOperationException("Generated quarantine path escaped its trusted root.");
        }

        return candidatePath;
    }

    private static bool IsPathWithin(string candidatePath, string parentPath)
    {
        string relativePath = Path.GetRelativePath(parentPath, candidatePath);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
