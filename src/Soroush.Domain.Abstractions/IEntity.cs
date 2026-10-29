namespace Soroush.Domain.Abstractions;

/// <summary>An entity identified by a key.</summary>
public interface IEntity<out TKey> where TKey : notnull
{
    /// <summary>Gets the identity of the entity.</summary>
    TKey Id { get; }
}
