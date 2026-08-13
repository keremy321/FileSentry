using FileSentry.Api.Domain.Auditing;
using FileSentry.Api.Domain.Files;
using FileSentry.Api.Domain.Scanning;
using FileSentry.Api.Infrastructure.Correlation;
using FileSentry.Api.Persistence;

namespace FileSentry.Api.Application.Auditing;

public sealed class AuditEventWriter(
    FileSentryDbContext dbContext,
    TimeProvider timeProvider)
{
    public AuditEvent Add(
        AuditEventType eventType,
        string correlationId,
        Guid? actorUserId = null,
        Guid? fileRecordId = null,
        FileRecordStatus? previousStatus = null,
        FileRecordStatus? newStatus = null,
        ScanFailureCode? failureCode = null,
        long? durationMilliseconds = null)
    {
        if (!CorrelationIdPolicy.IsValid(correlationId))
        {
            throw new InvalidOperationException("An audit event requires a valid correlation ID.");
        }

        if (durationMilliseconds < 0)
        {
            throw new InvalidOperationException("Audit duration must not be negative.");
        }

        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            FileRecordId = fileRecordId,
            EventType = eventType,
            OccurredAtUtc = timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            FailureCode = failureCode,
            DurationMilliseconds = durationMilliseconds
        };
        dbContext.AuditEvents.Add(auditEvent);
        return auditEvent;
    }
}
