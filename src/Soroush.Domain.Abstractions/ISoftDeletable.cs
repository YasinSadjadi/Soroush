namespace Soroush.Domain.Abstractions;

/// <summary>Exposes logical deletion state; filtering and deletion policy belong to the adapter.</summary>
public interface ISoftDeletable
{
    /// <summary>Gets whether the entity has been logically deleted.</summary>
    bool IsDeleted { get; }

    /// <summary>Gets the logical deletion time, or null when not deleted.</summary>
    DateTimeOffset? DeletedAt { get; }
}
