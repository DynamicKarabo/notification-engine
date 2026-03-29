namespace NotificationEngine.Application.Abstractions.Presence;

public interface IPresenceTracker
{
    Task UserConnectedAsync(string userId, string connectionId);
    Task UserDisconnectedAsync(string userId, string connectionId);
    Task<bool> IsUserOnlineAsync(string userId);
    Task<IEnumerable<string>> GetOnlineUsersAsync(IEnumerable<string> userIds);
    Task<IEnumerable<string>> GetOnlineUsersInTenantAsync(string tenantId);
}
