using NotificationEngine.Domain;

namespace NotificationEngine.Application.Abstractions.Messaging;

public interface IEventProducer
{
    Task<string> ProduceAsync<TEvent>(
        string streamKey,
        TEvent @event,
        CancellationToken ct = default)
        where TEvent : IDomainEvent;
}
