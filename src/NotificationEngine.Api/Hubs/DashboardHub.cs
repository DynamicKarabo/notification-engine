using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NotificationEngine.Api.Contracts.SignalR;
using NotificationEngine.Application.Abstractions.Presence;

namespace NotificationEngine.Api.Hubs;

[Authorize]
public class DashboardHub : Hub<IDashboardClient>
{
    private readonly IPresenceTracker _presenceTracker;
    private readonly IGroupMembership _groupMembership;
    private readonly IMediator _mediator;
    private readonly ILogger<DashboardHub> _logger;

    public DashboardHub(
        IPresenceTracker presenceTracker,
        IGroupMembership groupMembership,
        IMediator mediator,
        ILogger<DashboardHub> logger)
    {
        _presenceTracker = presenceTracker;
        _groupMembership = groupMembership;
        _mediator = mediator;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? "anonymous";
        var connectionId = Context.ConnectionId;

        await _presenceTracker.UserConnectedAsync(userId, connectionId);

        await Groups.AddToGroupAsync(connectionId, $"user:{userId}");

        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (tenantId is not null)
        {
            await Groups.AddToGroupAsync(connectionId, $"tenant:{tenantId}");
        }

        _logger.LogInformation(
            "Client connected. UserId={UserId} ConnectionId={ConnectionId} TenantId={TenantId}",
            userId, connectionId, tenantId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier ?? "anonymous";
        await _presenceTracker.UserDisconnectedAsync(userId, Context.ConnectionId);

        _logger.LogInformation(
            "Client disconnected. UserId={UserId} ConnectionId={ConnectionId}",
            userId, Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    public Task AcknowledgeNotification(string notificationId)
    {
        var userId = Context.UserIdentifier!;
        
        _logger.LogDebug(
            "Notification acknowledged. NotificationId={NotificationId} UserId={UserId}",
            notificationId, userId);

        return Task.CompletedTask;
    }

    public async Task SubscribeToDashboard(string dashboardId)
    {
        var userId = Context.UserIdentifier!;
        
        if (await _groupMembership.CanAccessAsync(userId, "dashboard", dashboardId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"dashboard:{dashboardId}");
            _logger.LogDebug("User {UserId} subscribed to dashboard {DashboardId}", userId, dashboardId);
        }
        else
        {
            _logger.LogWarning(
                "User {UserId} attempted to subscribe to dashboard {DashboardId} without permission",
                userId, dashboardId);
            throw new HubException("Access denied to this dashboard");
        }
    }

    public async Task UnsubscribeFromDashboard(string dashboardId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"dashboard:{dashboardId}");
        _logger.LogDebug("User {UserId} unsubscribed from dashboard {DashboardId}", Context.UserIdentifier, dashboardId);
    }

    public async Task SubscribeToTenant(string tenantId)
    {
        var userTenantId = Context.User?.FindFirst("tenant_id")?.Value;
        
        if (userTenantId == tenantId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
            _logger.LogDebug("User {UserId} subscribed to tenant {TenantId}", Context.UserIdentifier, tenantId);
        }
        else
        {
            throw new HubException("Cannot subscribe to a different tenant");
        }
    }

    public async Task<PresencePayload[]> GetOnlineUsers(string[] userIds)
    {
        var onlineUserIds = await _presenceTracker.GetOnlineUsersAsync(userIds);
        
        return onlineUserIds
            .Select(id => new PresencePayload(id, "online", DateTime.UtcNow))
            .ToArray();
    }
}
