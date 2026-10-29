using System.Data;

namespace Soroush.Persistence.Abstractions;

/// <summary>The provider adapter used by the unit of work to access a shared persistence session.</summary>
/// <remarks>The application or DI scope owns this session and disposes it after the unit of work.
/// Implementations provide raw persistence operations, without recursively calling the unit of work.
/// Repositories must use the same underlying session. Unsupported transactions or isolation levels must
/// throw explicitly. A failed begin operation must clean up resources before throwing.</remarks>
public interface IPersistenceSession
{
    /// <summary>Flushes pending changes and returns the number of affected entries.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins a transaction owned by the unit of work. Does not save pending changes.</summary>
    Task<ITransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default);
}
