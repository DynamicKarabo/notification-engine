namespace NotificationEngine.Api.Contracts.SignalR;

public record SystemAlertPayload(
    string Id,
    string Severity,
    string Title,
    string Message,
    DateTime CreatedAt,
    DateTime? ExpiresAt);
