using MediatR;

namespace NotificationEngine.Domain.Events;

public record NotificationCreatedEvent(
    Guid NotificationId,
    string UserId,
    string? TenantId,
    string Type,
    string Title,
    string Message,
    string? ImageUrl,
    Dictionary<string, string> Metadata) : DomainEventBase, INotification;

public record NotificationAcknowledgedEvent(
    Guid NotificationId,
    string UserId,
    DateTime AcknowledgedAt) : DomainEventBase, INotification;
