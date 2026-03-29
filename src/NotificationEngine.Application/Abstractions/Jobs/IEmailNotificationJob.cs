namespace NotificationEngine.Application.Abstractions.Jobs;

public interface IEmailNotificationJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}
