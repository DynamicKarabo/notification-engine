using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Presence;
using StackExchange.Redis;

namespace NotificationEngine.Infrastructure.Presence;

public class RedisPresenceTracker : IPresenceTracker
{
    private const string PresenceKey = "presence:users";
    private const string ConnectionsKeyPrefix = "presence:connections:";
    private const string TenantUsersKeyPrefix = "presence:tenant:";
    private const int PresenceTimeoutSeconds = 30;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisPresenceTracker> _logger;

    public RedisPresenceTracker(
        IConnectionMultiplexer redis,
        ILogger<RedisPresenceTracker> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task UserConnectedAsync(string userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var tx = db.CreateTransaction();
        
        _ = tx.SortedSetAddAsync(PresenceKey, userId, now);
        _ = tx.SetAddAsync($"{ConnectionsKeyPrefix}{userId}", connectionId);
        
        await tx.ExecuteAsync();

        _logger.LogDebug("User {UserId} connected with connection {ConnectionId}", userId, connectionId);
    }

    public async Task UserDisconnectedAsync(string userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        
        await db.SetRemoveAsync($"{ConnectionsKeyPrefix}{userId}", connectionId);
        
        var connections = await db.SetMembersAsync($"{ConnectionsKeyPrefix}{userId}");
        if (connections.Length == 0)
        {
            await db.SortedSetRemoveAsync(PresenceKey, userId);
            _logger.LogDebug("User {UserId} is now offline", userId);
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SortedSetAddAsync(PresenceKey, userId, now);
        }
    }

    public async Task<bool> IsUserOnlineAsync(string userId)
    {
        var db = _redis.GetDatabase();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-PresenceTimeoutSeconds).ToUnixTimeSeconds();
        
        var score = await db.SortedSetScoreAsync(PresenceKey, userId);
        return score.HasValue && score.Value >= cutoff;
    }

    public async Task<IEnumerable<string>> GetOnlineUsersAsync(IEnumerable<string> userIds)
    {
        var db = _redis.GetDatabase();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-PresenceTimeoutSeconds).ToUnixTimeSeconds();
        
        var userIdList = userIds.ToList();
        var tasks = userIdList.Select(async userId =>
        {
            var score = await db.SortedSetScoreAsync(PresenceKey, userId);
            return (userId, isOnline: score.HasValue && score.Value >= cutoff);
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r.isOnline).Select(r => r.userId);
    }

    public async Task<IEnumerable<string>> GetOnlineUsersInTenantAsync(string tenantId)
    {
        var db = _redis.GetDatabase();
        var tenantKey = $"{TenantUsersKeyPrefix}{tenantId}";
        
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-PresenceTimeoutSeconds).ToUnixTimeSeconds();
        
        var tenantUsers = await db.SetMembersAsync(tenantKey);
        var userIds = tenantUsers.Select(v => v.ToString()).ToList();
        
        var onlineTasks = userIds.Select(async userId =>
        {
            var score = await db.SortedSetScoreAsync(PresenceKey, userId);
            return (userId, isOnline: score.HasValue && score.Value >= cutoff);
        });

        var results = await Task.WhenAll(onlineTasks);
        return results.Where(r => r.isOnline).Select(r => r.userId);
    }
}
