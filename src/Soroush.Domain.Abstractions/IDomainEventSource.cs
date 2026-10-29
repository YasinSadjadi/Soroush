namespace Soroush.Domain.Abstractions;

/// <summary>Exposes pending domain events for application-controlled dispatch.</summary>
public interface IDomainEventSource
{
    /// <summary>Gets pending events in the order they were recorded.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Clears pending events after the application has handled them successfully.</summary>
    void ClearDomainEvents();
}
