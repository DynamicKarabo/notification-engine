namespace NotificationEngine.Application.Abstractions.Messaging;

public interface ISignalRNotificationService
{
    Task SendNotificationToUserAsync(string userId, NotificationPayload notification, CancellationToken ct = default);
    Task SendNotificationToGroupAsync(string groupName, NotificationPayload notification, CancellationToken ct = default);
    Task SendDashboardUpdateToGroupAsync(string groupName, DashboardUpdatePayload update, CancellationToken ct = default);
    Task SendSystemAlertToAllAsync(SystemAlertPayload alert, CancellationToken ct = default);
    Task SendSystemAlertToGroupAsync(string groupName, SystemAlertPayload alert, CancellationToken ct = default);
}
