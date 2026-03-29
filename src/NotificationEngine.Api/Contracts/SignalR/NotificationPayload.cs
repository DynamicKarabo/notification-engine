namespace NotificationEngine.Api.Contracts.SignalR;

public record NotificationPayload(
    string Id,
    string Type,
    string Title,
    string Message,
    string? ImageUrl,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata);
