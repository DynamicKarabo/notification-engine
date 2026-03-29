using Azure.Messaging.ServiceBus;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationEngine.Application.Abstractions.Jobs;
using NotificationEngine.Application.Abstractions.Messaging;
using NotificationEngine.Application.Abstractions.Presence;
using NotificationEngine.Infrastructure.Jobs;
using NotificationEngine.Infrastructure.Messaging;
using NotificationEngine.Infrastructure.Persistence;
using NotificationEngine.Infrastructure.Presence;
using StackExchange.Redis;

namespace NotificationEngine.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("Sql");
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(30);
            });
        });

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var connectionString = configuration.GetConnectionString("Redis")
                ?? configuration["Redis:ConnectionString"]
                ?? "localhost:6379";
            return ConnectionMultiplexer.Connect(connectionString);
        });

        services.AddScoped<IPresenceTracker, RedisPresenceTracker>();
        services.AddScoped<IGroupMembership, RedisGroupMembership>();

        services.AddSingleton<IEventProducer, RedisStreamProducer>();
        services.AddSingleton<ISignalRNotificationService, SignalRNotificationService>();

        services.AddHostedService<RedisStreamConsumer>();

        var serviceBusConnectionString = configuration.GetConnectionString("ServiceBus")
            ?? configuration["ServiceBus:ConnectionString"];
        if (!string.IsNullOrEmpty(serviceBusConnectionString))
        {
            services.AddSingleton(sp => new ServiceBusClient(serviceBusConnectionString));
            services.AddHostedService<ServiceBusSubscriber>();
        }

        var sqlConnectionString = configuration.GetConnectionString("Sql")
            ?? throw new InvalidOperationException("SQL connection string is required for Hangfire");
        
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(sqlConnectionString, new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.FromSeconds(15),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));

        services.AddHangfireServer(options =>
        {
            options.Queues = new[] { "critical", "default", "low" };
            options.WorkerCount = Environment.ProcessorCount * 5;
        });

        services.AddScoped<IOutboxPublisherJob, OutboxPublisherJob>();
        services.AddScoped<IEmailNotificationJob, EmailNotificationJob>();
        services.AddScoped<ISmsNotificationJob, SmsNotificationJob>();

        return services;
    }
}
