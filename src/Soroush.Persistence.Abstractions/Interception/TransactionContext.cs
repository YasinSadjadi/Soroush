using System.Data;

namespace Soroush.Persistence.Abstractions;

/// <summary>Identifies an intercepted transaction operation.</summary>
public sealed class TransactionContext
{
    /// <summary>Creates a context. The transaction is null while beginning a transaction.</summary>
    public TransactionContext(IUnitOfWork unitOfWork, IsolationLevel isolationLevel, TransactionOperation operation, ITransaction? transaction = null)
    {
        UnitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        IsolationLevel = isolationLevel;
        Operation = operation;
        Transaction = transaction;
    }

    /// <summary>Gets the coordinating unit of work. Interceptors must not reenter it.</summary>
    public IUnitOfWork UnitOfWork { get; }

    /// <summary>Gets the requested isolation level.</summary>
    public IsolationLevel IsolationLevel { get; }

    /// <summary>Gets the operation being intercepted.</summary>
    public TransactionOperation Operation { get; }

    /// <summary>Gets the transaction, or null during the before-begin hook or a provider begin failure.</summary>
    public ITransaction? Transaction { get; }
}

/// <summary>A transaction lifecycle operation.</summary>
public enum TransactionOperation
{
    /// <summary>Begin a transaction.</summary>
    Begin,
    /// <summary>Commit a transaction.</summary>
    Commit,
    /// <summary>Roll back a transaction.</summary>
    Rollback
}
