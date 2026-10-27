using System.Text.Json;

namespace Workout.AuditLogs;

public enum AuditActionType { ADD, RENEW, UPDATE, SUSPEND, DELETE }

public record UserAuditLogData
{
    public required string UserId { get; init; }
    public required AuditActionType ActionType { get; init; }
    public DateOnly? OldExpiry { get; init; }
    public DateOnly? NewExpiry { get; init; }
    public JsonElement? OldData { get; init; }
    public JsonElement? NewData { get; init; }
    public string? PerformedBy { get; init; }
}

public sealed record UserAuditLog : UserAuditLogData
{
    public required long Id { get; init; }
    // MySQL DATETIME has no timezone; this value has DateTimeKind.Unspecified.
    public required DateTime CreatedAt { get; init; }
}
