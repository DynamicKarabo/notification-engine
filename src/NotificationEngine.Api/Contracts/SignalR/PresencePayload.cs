namespace NotificationEngine.Api.Contracts.SignalR;

public record PresencePayload(
    string UserId,
    string Status,
    DateTime LastSeen);
