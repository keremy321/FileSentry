using FileSentry.Api.Domain.Files;
using FileSentry.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileSentry.Api.Persistence.Configurations;

public sealed class FileRecordConfiguration : IEntityTypeConfiguration<FileRecord>
{
    public void Configure(EntityTypeBuilder<FileRecord> builder)
    {
        builder.ToTable("FileRecords");
        builder.HasKey(record => record.Id);

        builder.Property(record => record.OriginalFileName)
            .HasMaxLength(255)
            .IsRequired();
        builder.Property(record => record.StorageName)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(record => record.Sha256)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        builder.Property(record => record.DetectedMediaType)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(record => record.ClientMediaType)
            .HasMaxLength(256);
        builder.Property(record => record.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(record => record.CreatedAtUtc)
            .IsRequired();

        builder.HasIndex(record => record.OwnerId);
        builder.HasIndex(record => record.StorageName)
            .IsUnique();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(record => record.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
