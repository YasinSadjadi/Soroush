using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>A save interceptor with no-op defaults for optional lifecycle hooks.</summary>
public abstract class SaveChangesInterceptor : ISaveChangesInterceptor
{
    /// <inheritdoc />
    public virtual Task SavingChangesAsync(SaveChangesContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task SavedChangesAsync(SaveChangesContext context, int affectedEntries, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task SaveChangesFailedAsync(SaveChangesContext context, Exception exception, CancellationToken cancellationToken) => Task.CompletedTask;
}
