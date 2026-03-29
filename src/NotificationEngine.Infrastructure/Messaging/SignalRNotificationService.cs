using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Messaging;

namespace NotificationEngine.Infrastructure.Messaging;

public class SignalRNotificationService : ISignalRNotificationService
{
    private readonly IHubContext<Hub> _hubContext;
    private readonly ILogger<SignalRNotificationService> _logger;

    public SignalRNotificationService(
        IHubContext<Hub> hubContext,
        ILogger<SignalRNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendNotificationToUserAsync(string userId, NotificationPayload notification, CancellationToken ct = default)
    {
        dynamic clients = _hubContext.Clients.User(userId);
        await clients.ReceiveNotification(notification);
        _logger.LogDebug("Sent notification {NotificationId} to user {UserId}", notification.Id, userId);
    }

    public async Task SendNotificationToGroupAsync(string groupName, NotificationPayload notification, CancellationToken ct = default)
    {
        dynamic clients = _hubContext.Clients.Group(groupName);
        await clients.ReceiveNotification(notification);
        _logger.LogDebug("Sent notification {NotificationId} to group {GroupName}", notification.Id, groupName);
    }

    public async Task SendDashboardUpdateToGroupAsync(string groupName, DashboardUpdatePayload update, CancellationToken ct = default)
    {
        dynamic clients = _hubContext.Clients.Group(groupName);
        await clients.ReceiveDashboardUpdate(update);
        _logger.LogDebug("Sent dashboard update for {EntityType}:{EntityId} to group {GroupName}", 
            update.EntityType, update.EntityId, groupName);
    }

    public async Task SendSystemAlertToAllAsync(SystemAlertPayload alert, CancellationToken ct = default)
    {
        dynamic clients = _hubContext.Clients.All;
        await clients.ReceiveSystemAlert(alert);
        _logger.LogDebug("Sent system alert {AlertId} to all clients", alert.Id);
    }

    public async Task SendSystemAlertToGroupAsync(string groupName, SystemAlertPayload alert, CancellationToken ct = default)
    {
        dynamic clients = _hubContext.Clients.Group(groupName);
        await clients.ReceiveSystemAlert(alert);
        _logger.LogDebug("Sent system alert {AlertId} to group {GroupName}", alert.Id, groupName);
    }
}
