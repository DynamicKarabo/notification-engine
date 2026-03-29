using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Presence;
using StackExchange.Redis;

namespace NotificationEngine.Infrastructure.Presence;

public class RedisGroupMembership : IGroupMembership
{
    private const string UserGroupsKeyPrefix = "user:groups:";
    private const string GroupMembersKeyPrefix = "group:members:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisGroupMembership> _logger;

    public RedisGroupMembership(
        IConnectionMultiplexer redis,
        ILogger<RedisGroupMembership> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> CanAccessAsync(string userId, string resourceType, string resourceId)
    {
        var db = _redis.GetDatabase();
        
        var resourceKey = $"{resourceType}:{resourceId}";
        
        var isMember = await db.SetContainsAsync($"{resourceKey}:allowed_users", userId);
        if (isMember) return true;
        
        var userRoles = await db.SetMembersAsync($"user:{userId}:roles");
        foreach (var role in userRoles)
        {
            var roleAllowed = await db.SetContainsAsync($"{resourceKey}:allowed_roles", role.ToString());
            if (roleAllowed) return true;
        }

        return false;
    }

    public async Task<IEnumerable<string>> GetUserGroupsAsync(string userId, string? tenantId)
    {
        var db = _redis.GetDatabase();
        var groups = new List<string>();

        groups.Add($"user:{userId}");
        
        if (tenantId is not null)
        {
            groups.Add($"tenant:{tenantId}");
            
            var userRoles = await db.SetMembersAsync($"user:{userId}:roles");
            foreach (var role in userRoles)
            {
                groups.Add($"role:{role}");
            }
        }

        return groups;
    }

    public async Task JoinGroupAsync(string userId, string groupName)
    {
        var db = _redis.GetDatabase();
        
        await db.SetAddAsync($"{UserGroupsKeyPrefix}{userId}", groupName);
        await db.SetAddAsync($"{GroupMembersKeyPrefix}{groupName}", userId);
        
        _logger.LogDebug("User {UserId} joined group {GroupName}", userId, groupName);
    }

    public async Task LeaveGroupAsync(string userId, string groupName)
    {
        var db = _redis.GetDatabase();
        
        await db.SetRemoveAsync($"{UserGroupsKeyPrefix}{userId}", groupName);
        await db.SetRemoveAsync($"{GroupMembersKeyPrefix}{groupName}", userId);
        
        _logger.LogDebug("User {UserId} left group {GroupName}", userId, groupName);
    }
}
