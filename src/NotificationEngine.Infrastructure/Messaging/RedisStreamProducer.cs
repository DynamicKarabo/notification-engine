using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Domain;
using StackExchange.Redis;

namespace NotificationEngine.Infrastructure.Messaging;

public class RedisStreamProducer : IEventProducer
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisStreamProducer> _logger;
    private const long MaxStreamLength = 1_000_000;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RedisStreamProducer(
        IConnectionMultiplexer redis,
        ILogger<RedisStreamProducer> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<string> ProduceAsync<TEvent>(
        string streamKey,
        TEvent @event,
        CancellationToken ct = default)
        where TEvent : IDomainEvent
    {
        var db = _redis.GetDatabase();

        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;

        var entries = new NameValueEntry[]
        {
            new("type", @event.GetType().Name),
            new("payload", JsonSerializer.Serialize(@event, @event.GetType(), JsonSerializerOptions)),
            new("version", @event.Version.ToString()),
            new("trace_id", traceId),
            new("occurred_on", @event.OccurredOn.ToString("O"))
        };

        var messageId = await db.StreamAddAsync(
            streamKey,
            entries,
            maxLength: (int)MaxStreamLength,
            useApproximateMaxLength: true);

        _logger.LogDebug(
            "Produced event {EventType} to stream {StreamKey} with ID {MessageId}",
            @event.GetType().Name,
            streamKey,
            messageId);

        return messageId.ToString();
    }
}
