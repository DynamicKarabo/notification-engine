namespace NotificationEngine.Domain;

public interface IAggregateRoot
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    
    void ClearDomainEvents();
}

public interface IDomainEvent
{
    Guid Id { get; }
    DateTime OccurredOn { get; }
    int Version { get; }
}

public abstract record DomainEventBase : IDomainEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public int Version { get; init; } = 1;
}
