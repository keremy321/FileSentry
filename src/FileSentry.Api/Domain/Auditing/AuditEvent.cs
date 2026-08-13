using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;

namespace FileSentry.Api.Domain.Auditing;

public sealed class AuditEvent
{
    public Guid Id { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? FileRecordId { get; set; }

    public AuditEventType EventType { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public FileRecordStatus? PreviousStatus { get; set; }

    public FileRecordStatus? NewStatus { get; set; }

    public ScanFailureCode? FailureCode { get; set; }

    public long? DurationMilliseconds { get; set; }
}
