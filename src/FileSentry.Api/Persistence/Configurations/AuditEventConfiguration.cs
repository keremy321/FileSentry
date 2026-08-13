using FileSentry.Api.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileSentry.Api.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents", table => table.HasCheckConstraint(
            "CK_AuditEvents_DurationMilliseconds_NonNegative",
            "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" >= 0"));
        builder.HasKey(auditEvent => auditEvent.Id);

        builder.Property(auditEvent => auditEvent.EventType)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(auditEvent => auditEvent.CorrelationId)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(auditEvent => auditEvent.PreviousStatus)
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(auditEvent => auditEvent.NewStatus)
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(auditEvent => auditEvent.FailureCode)
            .HasConversion<string>()
            .HasMaxLength(64);

        builder.HasIndex(auditEvent => auditEvent.OccurredAtUtc);
        builder.HasIndex(auditEvent => new { auditEvent.FileRecordId, auditEvent.OccurredAtUtc });
        builder.HasIndex(auditEvent => new { auditEvent.ActorUserId, auditEvent.OccurredAtUtc });
        builder.HasIndex(auditEvent => auditEvent.CorrelationId);
    }
}
