namespace NotificationEngine.Application.Abstractions.Jobs;

public interface IOutboxPublisherJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}
