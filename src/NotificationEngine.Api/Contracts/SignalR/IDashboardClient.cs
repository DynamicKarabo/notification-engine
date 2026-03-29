using NotificationEngine.Application.Abstractions.Messaging;

namespace NotificationEngine.Api.Contracts.SignalR;

public interface IDashboardClient
{
    Task ReceiveNotification(NotificationPayload notification);
    Task ReceiveDashboardUpdate(DashboardUpdatePayload update);
    Task ReceivePresenceUpdate(PresencePayload presence);
    Task ReceiveSystemAlert(SystemAlertPayload alert);
}
