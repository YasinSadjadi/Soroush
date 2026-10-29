using System.Data.Common;

namespace Soroush.Persistence.Abstractions;

/// <summary>Creates ADO.NET connections for SQL or micro-ORM adapters.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Creates and opens a new connection. The caller owns and must dispose the returned connection.
    /// The factory must dispose a partially created connection on failure or cancellation.</summary>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
