using System.ComponentModel.DataAnnotations.Schema;

namespace Soroush.Domain.Abstractions;

/// <summary>A base entity with the identity and timestamps used by Sheng.Ai.</summary>
public abstract class Entity : IEntity<Guid>
{
    /// <summary>Gets or sets the identity of the entity.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the last modification time, or null if never modified.</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Gets whether Touch must preserve the current modification time.</summary>
    [NotMapped]
    public bool IsModifiedAtUpdateSuppressed { get; private set; }

    /// <summary>Suppresses modification time updates, or enables them again when false is supplied.</summary>
    /// <remarks>The flag applies to this instance until changed. Adapters should call Touch to honor it.</remarks>
    public virtual void SuppressModifiedAtUpdate(bool suppress = true)
        => IsModifiedAtUpdateSuppressed = suppress;

    /// <summary>Sets the modification time to UTC now unless updates are suppressed.</summary>
    public virtual void Touch()
    {
        if (!IsModifiedAtUpdateSuppressed)
            ModifiedAt = DateTime.UtcNow;
    }
}
