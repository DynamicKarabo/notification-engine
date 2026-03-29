using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Jobs;

namespace NotificationEngine.Infrastructure.Jobs;

public class SmsNotificationJob : ISmsNotificationJob
{
    private readonly ILogger<SmsNotificationJob> _logger;

    public SmsNotificationJob(ILogger<SmsNotificationJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Processing SMS notification job");
        
        await Task.CompletedTask;
    }
}
