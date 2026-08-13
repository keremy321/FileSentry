using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileSentry.Api.Persistence.Configurations;

public sealed class ScanAttemptConfiguration : IEntityTypeConfiguration<ScanAttempt>
{
    public void Configure(EntityTypeBuilder<ScanAttempt> builder)
    {
        builder.ToTable("ScanAttempts");
        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.Result)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(attempt => attempt.FailureCode)
            .HasConversion<string>()
            .HasMaxLength(64);
        builder.Property(attempt => attempt.DetectionName)
            .HasMaxLength(256);
        builder.Property(attempt => attempt.ScannerVersion)
            .HasMaxLength(256);

        builder.HasIndex(attempt => new { attempt.FileRecordId, attempt.AttemptNumber })
            .IsUnique();
        builder.HasIndex(attempt => attempt.CompletedAtUtc);

        builder.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(attempt => attempt.FileRecordId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
