namespace NotificationEngine.Application.Abstractions.Jobs;

public interface ISmsNotificationJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}
