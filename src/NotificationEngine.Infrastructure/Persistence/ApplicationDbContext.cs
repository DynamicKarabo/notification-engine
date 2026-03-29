using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationEngine.Domain;
using NotificationEngine.Infrastructure.Persistence.Entities;

namespace NotificationEngine.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public override async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var domainEntities = ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .ToList();

        var domainEvents = domainEntities
            .SelectMany(e => e.Entity.DomainEvents)
            .ToList();

        foreach (var entity in domainEntities)
        {
            entity.Entity.ClearDomainEvents();
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            var outboxMessage = new OutboxMessage
            {
                Id = domainEvent.Id,
                OccurredOn = domainEvent.OccurredOn,
                Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonSerializerOptions),
                IdempotencyKey = $"{(domainEvent.GetType().Name)}_{domainEvent.Id}"
            };

            OutboxMessages.Add(outboxMessage);
        }

        if (domainEvents.Count > 0)
        {
            await base.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasIndex(e => e.ProcessedAt);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
