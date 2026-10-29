namespace Soroush.Domain.Abstractions;

/// <summary>A fact that occurred in the domain, independent of its delivery mechanism.</summary>
public interface IDomainEvent
{
    /// <summary>Gets the time at which the event occurred.</summary>
    DateTimeOffset OccurredAt { get; }
}
