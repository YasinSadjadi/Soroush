namespace Soroush.Domain.Abstractions;

/// <summary>Exposes creation and modification timestamps maintained by the application or adapter.</summary>
public interface IAuditable
{
    /// <summary>Gets the time at which the entity was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the last modification time, or null if never modified.</summary>
    DateTimeOffset? UpdatedAt { get; }
}
