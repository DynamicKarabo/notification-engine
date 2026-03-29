using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Domain;
using NotificationEngine.Domain.Events;

namespace NotificationEngine.Infrastructure.Messaging;

public class ServiceBusSubscriber : BackgroundService
{
    private const int MaxConcurrentCalls = 16;
    private const int MaxAutoLockRenewalMinutes = 5;

    private readonly ServiceBusClient _serviceBusClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventProducer _eventProducer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceBusSubscriber> _logger;

    private readonly List<ServiceBusProcessor> _processors = new();

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ServiceBusSubscriber(
        ServiceBusClient serviceBusClient,
        IServiceScopeFactory scopeFactory,
        IEventProducer eventProducer,
        IConfiguration configuration,
        ILogger<ServiceBusSubscriber> logger)
    {
        _serviceBusClient = serviceBusClient;
        _scopeFactory = scopeFactory;
        _eventProducer = eventProducer;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topics = GetTopicSubscriptions();

        foreach (var (topicName, subscriptionName) in topics)
        {
            var processor = _serviceBusClient.CreateProcessor(
                topicName,
                subscriptionName,
                new ServiceBusProcessorOptions
                {
                    MaxConcurrentCalls = MaxConcurrentCalls,
                    AutoCompleteMessages = false,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(MaxAutoLockRenewalMinutes)
                });

            processor.ProcessMessageAsync += HandleMessageAsync;
            processor.ProcessErrorAsync += HandleErrorAsync;

            await processor.StartProcessingAsync(stoppingToken);
            _processors.Add(processor);

            _logger.LogInformation(
                "Started processing topic {TopicName}, subscription {SubscriptionName}",
                topicName,
                subscriptionName);
        }

        _logger.LogInformation(
            "Azure Service Bus Subscriber started. Processing {Count} topics",
            topics.Count);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        finally
        {
            foreach (var processor in _processors)
            {
                await processor.StopProcessingAsync();
                await processor.DisposeAsync();
            }
        }
    }

    private List<(string TopicName, string SubscriptionName)> GetTopicSubscriptions()
    {
        var subscriptions = new List<(string, string)>();

        var notificationsTopic = _configuration["ServiceBus:Topics:Notifications"];
        if (!string.IsNullOrEmpty(notificationsTopic))
        {
            subscriptions.Add((notificationsTopic, "notification-engine"));
        }
        else
        {
            subscriptions.Add(("notifications", "notification-engine"));
        }

        var dashboardTopic = _configuration["ServiceBus:Topics:Dashboard"];
        if (!string.IsNullOrEmpty(dashboardTopic))
        {
            subscriptions.Add((dashboardTopic, "notification-engine"));
        }
        else
        {
            subscriptions.Add(("dashboard-updates", "notification-engine"));
        }

        var alertsTopic = _configuration["ServiceBus:Topics:SystemAlerts"];
        if (!string.IsNullOrEmpty(alertsTopic))
        {
            subscriptions.Add((alertsTopic, "notification-engine"));
        }
        else
        {
            subscriptions.Add(("system-alerts", "notification-engine"));
        }

        return subscriptions;
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var body = args.Message.Body.ToString();
            var eventType = args.Message.ApplicationProperties.TryGetValue("eventType", out var et)
                ? et?.ToString()
                : null;

            if (string.IsNullOrEmpty(eventType))
            {
                _logger.LogWarning("Message {MessageId} has no eventType property", args.Message.MessageId);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var streamKey = DetermineStreamKey(args.Message);
            var domainEvent = DeserializeMessage(eventType, body);

            if (domainEvent != null)
            {
                await _eventProducer.ProduceAsync(streamKey, domainEvent, args.CancellationToken);
            }

            await args.CompleteMessageAsync(args.Message);

            _logger.LogDebug(
                "Processed Service Bus message {MessageId} -> stream {StreamKey}",
                args.Message.MessageId,
                streamKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed processing message {MessageId}", args.Message.MessageId);
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus error. Entity: {Entity}, ErrorSource: {ErrorSource}",
            args.EntityPath,
            args.ErrorSource);
        
        return Task.CompletedTask;
    }

    private string DetermineStreamKey(ServiceBusReceivedMessage message)
    {
        if (message.ApplicationProperties.TryGetValue("tenantId", out var tenantId) && tenantId is string tid)
        {
            return $"stream:notifications:{tid}";
        }

        if (message.ApplicationProperties.TryGetValue("dashboardId", out var dashboardId) && dashboardId is string did)
        {
            return $"stream:dashboard:{did}";
        }

        return "stream:system";
    }

    private IDomainEvent? DeserializeMessage(string eventType, string body)
    {
        Type? targetType = eventType switch
        {
            "NotificationCreated" => typeof(NotificationCreatedEvent),
            "NotificationAcknowledged" => typeof(NotificationAcknowledgedEvent),
            "DashboardUpdated" => typeof(DashboardUpdatedEvent),
            "SystemAlert" => typeof(SystemAlertEvent),
            _ => null
        };

        if (targetType == null)
        {
            _logger.LogWarning("Unknown event type: {EventType}", eventType);
            return null;
        }

        return JsonSerializer.Deserialize(body, targetType, JsonSerializerOptions) as IDomainEvent;
    }
}
