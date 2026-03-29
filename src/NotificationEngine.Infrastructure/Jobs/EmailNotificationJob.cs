using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions.Jobs;

namespace NotificationEngine.Infrastructure.Jobs;

public class EmailNotificationJob : IEmailNotificationJob
{
    private readonly ILogger<EmailNotificationJob> _logger;

    public EmailNotificationJob(ILogger<EmailNotificationJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Processing email notification job");
        
        await Task.CompletedTask;
    }
}
