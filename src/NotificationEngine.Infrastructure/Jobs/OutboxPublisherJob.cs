using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Jobs;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Domain;
using NotificationEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace NotificationEngine.Infrastructure.Jobs;

[DisableConcurrentExecution(60)]
public class OutboxPublisherJob : IOutboxPublisherJob
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IEventProducer _eventProducer;
    private readonly ILogger<OutboxPublisherJob> _logger;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OutboxPublisherJob(
        ApplicationDbContext dbContext,
        IEventProducer eventProducer,
        ILogger<OutboxPublisherJob> logger)
    {
        _dbContext = dbContext;
        _eventProducer = eventProducer;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var messages = await _dbContext.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredOn)
            .Take(100)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            _logger.LogDebug("No unpublished outbox messages found");
            return;
        }

        _logger.LogInformation("Processing {Count} outbox messages", messages.Count);

        foreach (var message in messages)
        {
            try
            {
                var streamKey = DetermineStreamKey(message.Type);
                var domainEvent = DeserializeEvent(message.Type, message.Payload);

                if (domainEvent != null)
                {
                    await _eventProducer.ProduceAsync(streamKey, domainEvent, ct);
                    
                    message.ProcessedAt = DateTime.UtcNow;
                    
                    _logger.LogDebug(
                        "Published outbox message {MessageId} to stream {StreamKey}",
                        message.Id,
                        streamKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
                message.Error = ex.Message;
            }
        }

        await _dbContext.SaveChangesAsync(ct);
        
        _logger.LogInformation(
            "Completed processing {Count} outbox messages",
            messages.Count);
    }

    private string DetermineStreamKey(string eventType)
    {
        return eventType switch
        {
            "NotificationCreatedEvent" => "stream:notifications",
            "NotificationAcknowledgedEvent" => "stream:notifications",
            "DashboardUpdatedEvent" => "stream:dashboard",
            "SystemAlertEvent" => "stream:system",
            _ => "stream:unknown"
        };
    }

    private IDomainEvent? DeserializeEvent(string eventType, string payload)
    {
        var targetType = eventType switch
        {
            "NotificationCreatedEvent" => Type.GetType("NotificationEngine.Domain.Events.NotificationCreatedEvent, NotificationEngine.Domain"),
            "NotificationAcknowledgedEvent" => Type.GetType("NotificationEngine.Domain.Events.NotificationAcknowledgedEvent, NotificationEngine.Domain"),
            "DashboardUpdatedEvent" => Type.GetType("NotificationEngine.Domain.Events.DashboardUpdatedEvent, NotificationEngine.Domain"),
            "SystemAlertEvent" => Type.GetType("NotificationEngine.Domain.Events.SystemAlertEvent, NotificationEngine.Domain"),
            _ => null
        };

        if (targetType == null)
        {
            _logger.LogWarning("Unknown event type: {EventType}", eventType);
            return null;
        }

        return JsonSerializer.Deserialize(payload, targetType, JsonSerializerOptions) as IDomainEvent;
    }
}
