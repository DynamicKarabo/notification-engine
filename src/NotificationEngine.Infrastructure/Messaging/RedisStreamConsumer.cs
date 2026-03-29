using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationEngine.Domain;
using NotificationEngine.Domain.Events;
using StackExchange.Redis;

namespace NotificationEngine.Infrastructure.Messaging;

public class RedisStreamConsumer : BackgroundService
{
    private const string StreamKeyPattern = "stream:*";
    private const string GroupName = "notification-processor";
    private const int BatchSize = 50;
    private const int BlockTimeoutMs = 1000;
    private const int MinIdleTimeMs = 30_000;

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RedisStreamConsumer> _logger;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, Type> EventTypeMap = new()
    {
        { "NotificationCreatedEvent", typeof(NotificationCreatedEvent) },
        { "NotificationAcknowledgedEvent", typeof(NotificationAcknowledgedEvent) },
        { "DashboardUpdatedEvent", typeof(DashboardUpdatedEvent) },
        { "SystemAlertEvent", typeof(SystemAlertEvent) }
    };

    public RedisStreamConsumer(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<RedisStreamConsumer> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = _redis.GetServers().FirstOrDefault();
        if (server == null)
        {
            _logger.LogWarning("No Redis servers available");
            return;
        }

        var consumerName = $"{GroupName}-{Environment.MachineName}";
        var db = _redis.GetDatabase();

        await EnsureConsumerGroupsAsync(db, consumerName);

        _logger.LogInformation(
            "Redis Stream Consumer started. Consumer: {ConsumerName}",
            consumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessStreamMessagesAsync(db, consumerName, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing stream messages");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task EnsureConsumerGroupsAsync(IDatabase db, string consumerName)
    {
        var streamKeys = _redis.GetServers()
            .SelectMany(s => s.Keys(pattern: StreamKeyPattern))
            .Distinct()
            .ToArray();

        foreach (var streamKey in streamKeys)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(streamKey, GroupName, "0", createStream: true);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                _logger.LogDebug("Consumer group {GroupName} already exists for stream {StreamKey}", GroupName, streamKey);
            }
        }
    }

    private async Task ProcessStreamMessagesAsync(
        IDatabase db,
        string consumerName,
        CancellationToken stoppingToken)
    {
        var streamKeys = _redis.GetServers()
            .SelectMany(s => s.Keys(pattern: StreamKeyPattern))
            .Distinct()
            .ToArray();

        if (streamKeys.Length == 0)
        {
            await Task.Delay(1000, stoppingToken);
            return;
        }

        foreach (var streamKey in streamKeys)
        {
            try
            {
                var results = await db.StreamReadGroupAsync(
                    streamKey,
                    GroupName,
                    consumerName,
                    position: ">",
                    count: BatchSize,
                    noAck: false);

                if (results.Length > 0)
                {
                    await ProcessBatchAsync(db, streamKey, results, consumerName, stoppingToken);
                }
                else
                {
                    await ProcessPendingAsync(db, streamKey, consumerName, stoppingToken);
                }
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSTREAM"))
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing stream {StreamKey}", streamKey);
            }
        }

        if (streamKeys.Length > 0)
        {
            await Task.Delay(100, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(
        IDatabase db,
        RedisKey streamKey,
        StreamEntry[] entries,
        string consumerName,
        CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        foreach (var entry in entries)
        {
            try
            {
                var domainEvent = DeserializeEvent(entry);
                if (domainEvent != null)
                {
                    await mediator.Publish(domainEvent, stoppingToken);
                    await db.StreamAcknowledgeAsync(streamKey, GroupName, entry.Id);
                    _logger.LogDebug("Processed event {EventType} with ID {EntryId}", 
                        domainEvent.GetType().Name, entry.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing entry {EntryId}", entry.Id);
                await HandleFailedEntryAsync(db, streamKey, entry, ex);
            }
        }
    }

    private async Task ProcessPendingAsync(
        IDatabase db,
        RedisKey streamKey,
        string consumerName,
        CancellationToken stoppingToken)
    {
        try
        {
            var pending = await db.StreamPendingMessagesAsync(
                streamKey,
                GroupName,
                100,
                consumerName);

            if (pending.Length == 0) return;

            var ids = pending.Select(p => p.MessageId).ToArray();
            await db.StreamClaimAsync(streamKey, GroupName, consumerName, 30_000, ids);

            _logger.LogInformation(
                "Claimed {Count} pending messages from stream {StreamKey}",
                ids.Length,
                streamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOSTREAM"))
        {
        }
    }

    private IDomainEvent? DeserializeEvent(StreamEntry entry)
    {
        var typeEntry = entry.Values.FirstOrDefault(v => v.Name == "type");
        if (typeEntry.Value.IsNull)
            return null;

        var eventTypeName = typeEntry.Value.ToString();
        if (string.IsNullOrEmpty(eventTypeName) || !EventTypeMap.TryGetValue(eventTypeName, out var eventType))
        {
            _logger.LogWarning("Unknown event type: {EventType}", eventTypeName);
            return null;
        }

        var payloadEntry = entry.Values.FirstOrDefault(v => v.Name == "payload");
        if (payloadEntry.Value.IsNull)
            return null;

        var payload = payloadEntry.Value.ToString();
        if (string.IsNullOrEmpty(payload))
            return null;

        return JsonSerializer.Deserialize(payload, eventType, JsonSerializerOptions) as IDomainEvent;
    }

    private async Task HandleFailedEntryAsync(
        IDatabase db,
        RedisKey streamKey,
        StreamEntry entry,
        Exception ex)
    {
        var dlqKey = "stream:dlq";
        var payloadEntry = entry.Values.FirstOrDefault(v => v.Name == "payload");
        var typeEntry = entry.Values.FirstOrDefault(v => v.Name == "type");
        
        var dlqEntry = new NameValueEntry[]
        {
            new("original_stream", streamKey.ToString()),
            new("original_id", entry.Id.ToString()),
            new("error", ex.Message),
            new("failed_at", DateTime.UtcNow.ToString("O")),
            new("payload", payloadEntry.Value.IsNull ? string.Empty : payloadEntry.Value.ToString()),
            new("type", typeEntry.Value.IsNull ? string.Empty : typeEntry.Value.ToString())
        };

        await db.StreamAddAsync(dlqKey, dlqEntry);
        await db.StreamAcknowledgeAsync(streamKey, GroupName, entry.Id);

        _logger.LogWarning(
            "Message {EntryId} moved to DLQ. Error: {Error}",
            entry.Id,
            ex.Message);
    }
}
