using FileSentry.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace FileSentry.Api.Persistence;

public sealed class DatabaseMigrationRunner(
    FileSentryDbContext dbContext,
    ServiceIdentityProvisioner serviceIdentityProvisioner,
    ILogger<DatabaseMigrationRunner> logger)
{
    public const string CommandArgument = "--migrate";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying FileSentry database migrations.");
        await dbContext.Database.MigrateAsync(cancellationToken);
        await serviceIdentityProvisioner.EnsureProvisionedAsync(cancellationToken);
        logger.LogInformation("FileSentry database migrations are current.");
    }
}
