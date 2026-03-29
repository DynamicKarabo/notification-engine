namespace NotificationEngine.Api.Contracts.SignalR;

public record DashboardUpdatePayload(
    string EntityType,
    string EntityId,
    string Action,
    Dictionary<string, object> Data,
    DateTime UpdatedAt,
    string? TraceId);
