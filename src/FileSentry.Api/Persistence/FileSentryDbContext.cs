using FileSentry.Api.Domain.Files;
using FileSentry.Api.Persistence.Configurations;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FileSentry.Api.Persistence;

public sealed class FileSentryDbContext(DbContextOptions<FileSentryDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>()
            .Property(user => user.CreatedAtUtc)
            .IsRequired();

        builder.Entity<ApplicationUser>()
            .HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique();

        builder.ApplyConfiguration(new FileRecordConfiguration());
    }
}
