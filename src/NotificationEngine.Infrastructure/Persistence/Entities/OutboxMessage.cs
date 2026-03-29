using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NotificationEngine.Infrastructure.Persistence.Entities;

[Table("OutboxMessages")]
public class OutboxMessage
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public DateTime OccurredOn { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(256)]
    public string Type { get; set; } = string.Empty;

    [Required]
    public string Payload { get; set; } = string.Empty;

    public DateTime? ProcessedAt { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    [Required]
    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;
}
