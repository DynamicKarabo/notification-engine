using MediatR;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Domain.Events;

namespace NotificationEngine.Application.Events;

public class NotificationEventHandler :
    INotificationHandler<NotificationCreatedEvent>
{
    private readonly ISignalRNotificationService _signalRService;
    private readonly ILogger<NotificationEventHandler> _logger;

    public NotificationEventHandler(
        ISignalRNotificationService signalRService,
        ILogger<NotificationEventHandler> logger)
    {
        _signalRService = signalRService;
        _logger = logger;
    }

    public async Task Handle(NotificationCreatedEvent notification, CancellationToken cancellationToken)
    {
        var payload = new NotificationPayload(
            notification.NotificationId.ToString(),
            notification.Type,
            notification.Title,
            notification.Message,
            notification.ImageUrl,
            notification.OccurredOn,
            notification.Metadata);

        if (!string.IsNullOrEmpty(notification.TenantId))
        {
            await _signalRService.SendNotificationToGroupAsync(
                $"tenant:{notification.TenantId}",
                payload,
                cancellationToken);
        }

        await _signalRService.SendNotificationToUserAsync(
            notification.UserId,
            payload,
            cancellationToken);

        _logger.LogInformation(
            "Notification {NotificationId} sent to user {UserId}",
            notification.NotificationId,
            notification.UserId);
    }
}
