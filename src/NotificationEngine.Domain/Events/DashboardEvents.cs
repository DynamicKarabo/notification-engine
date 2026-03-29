using MediatR;

namespace NotificationEngine.Domain.Events;

public record DashboardUpdatedEvent(
    Guid DashboardId,
    string? TenantId,
    string EntityType,
    string EntityId,
    string Action,
    Dictionary<string, object> Data) : DomainEventBase, INotification;

public record SystemAlertEvent(
    Guid AlertId,
    string Severity,
    string Title,
    string Message,
    string? TenantId,
    DateTime? ExpiresAt) : DomainEventBase, INotification;
