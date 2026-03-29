namespace NotificationEngine.Application.Abstractions.Presence;

public interface IGroupMembership
{
    Task<bool> CanAccessAsync(string userId, string resourceType, string resourceId);
    Task<IEnumerable<string>> GetUserGroupsAsync(string userId, string? tenantId);
    Task JoinGroupAsync(string userId, string groupName);
    Task LeaveGroupAsync(string userId, string groupName);
}
