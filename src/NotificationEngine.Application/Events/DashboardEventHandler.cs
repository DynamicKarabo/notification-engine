using MediatR;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Domain.Events;

namespace NotificationEngine.Application.Events;

public class DashboardEventHandler :
    INotificationHandler<DashboardUpdatedEvent>,
    INotificationHandler<SystemAlertEvent>
{
    private readonly ISignalRNotificationService _signalRService;
    private readonly ILogger<DashboardEventHandler> _logger;

    public DashboardEventHandler(
        ISignalRNotificationService signalRService,
        ILogger<DashboardEventHandler> logger)
    {
        _signalRService = signalRService;
        _logger = logger;
    }

    public async Task Handle(DashboardUpdatedEvent dashboardEvent, CancellationToken cancellationToken)
    {
        var payload = new DashboardUpdatePayload(
            dashboardEvent.EntityType,
            dashboardEvent.EntityId,
            dashboardEvent.Action,
            dashboardEvent.Data,
            dashboardEvent.OccurredOn,
            null);

        var groupName = $"dashboard:{dashboardEvent.DashboardId}";

        await _signalRService.SendDashboardUpdateToGroupAsync(
            groupName,
            payload,
            cancellationToken);

        _logger.LogInformation(
            "Dashboard update for {EntityType}:{EntityId} sent to dashboard {DashboardId}",
            dashboardEvent.EntityType,
            dashboardEvent.EntityId,
            dashboardEvent.DashboardId);
    }

    public async Task Handle(SystemAlertEvent alertEvent, CancellationToken cancellationToken)
    {
        var payload = new SystemAlertPayload(
            alertEvent.AlertId.ToString(),
            alertEvent.Severity,
            alertEvent.Title,
            alertEvent.Message,
            alertEvent.OccurredOn,
            alertEvent.ExpiresAt);

        if (!string.IsNullOrEmpty(alertEvent.TenantId))
        {
            await _signalRService.SendSystemAlertToGroupAsync(
                $"tenant:{alertEvent.TenantId}",
                payload,
                cancellationToken);
        }
        else
        {
            await _signalRService.SendSystemAlertToAllAsync(
                payload,
                cancellationToken);
        }

        _logger.LogInformation(
            "System alert {AlertId} ({Severity}) sent",
            alertEvent.AlertId,
            alertEvent.Severity);
    }
}
