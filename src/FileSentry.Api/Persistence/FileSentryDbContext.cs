using FileSentry.Api.Domain.Auditing;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Persistence.Configurations;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FileSentry.Api.Persistence;

public sealed class FileSentryDbContext(DbContextOptions<FileSentryDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();

    public DbSet<ScanAttempt> ScanAttempts => Set<ScanAttempt>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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

        builder.ApplyConfiguration(new AuditEventConfiguration());
        builder.ApplyConfiguration(new FileRecordConfiguration());
        builder.ApplyConfiguration(new ScanAttemptConfiguration());
    }
}
