namespace FileSentry.Api.Infrastructure.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "../../data";
}
