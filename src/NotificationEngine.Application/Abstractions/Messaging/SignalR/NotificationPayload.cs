namespace NotificationEngine.Application.Abstractions.Messaging;

public record NotificationPayload(
    string Id,
    string Type,
    string Title,
    string Message,
    string? ImageUrl,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata);

public record DashboardUpdatePayload(
    string EntityType,
    string EntityId,
    string Action,
    Dictionary<string, object> Data,
    DateTime UpdatedAt,
    string? TraceId);

public record SystemAlertPayload(
    string Id,
    string Severity,
    string Title,
    string Message,
    DateTime CreatedAt,
    DateTime? ExpiresAt);
