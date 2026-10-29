using System.Data;

namespace Soroush.Persistence.Abstractions;

/// <summary>Coordinates repositories sharing one persistence session.</summary>
/// <remarks>Dispose the coordinator before disposing its persistence session. Saving does not dispatch domain events.
/// Without an explicit transaction, the adapter defines the atomicity of a save. A unit of work is scoped to
/// one logical operation; concurrent or reentrant persistence operations are not supported.</remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>Gets the active transaction, or null when no transaction is active.</summary>
    ITransaction? CurrentTransaction { get; }

    /// <summary>Persists pending changes and returns the number of affected entries.
    /// Within an explicit transaction, durability requires committing that transaction.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins a caller-owned transaction. Unsupported isolation levels or nested
    /// transactions must be rejected explicitly by the adapter.</summary>
    Task<ITransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default);
}
