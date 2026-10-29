namespace Soroush.Persistence.Abstractions;

/// <summary>Observes and prepares persistence saves in registration order.</summary>
/// <remarks>Before hooks may change tracked entities but must not call save recursively. Saved hooks
/// run after the provider has saved; a saved-hook failure does not undo that save and does not invoke
/// failed hooks. Failure hooks receive an uncanceled cleanup token. They cannot suppress the original failure.</remarks>
public interface ISaveChangesInterceptor
{
    /// <summary>Runs before changes are saved, for example to apply audit timestamps.</summary>
    Task SavingChangesAsync(SaveChangesContext context, CancellationToken cancellationToken);

    /// <summary>Runs after saving, with the affected entry count. An active transaction is not yet committed.</summary>
    Task SavedChangesAsync(SaveChangesContext context, int affectedEntries, CancellationToken cancellationToken);

    /// <summary>Observes a before-hook or provider failure, including cancellation.</summary>
    Task SaveChangesFailedAsync(SaveChangesContext context, Exception exception, CancellationToken cancellationToken);
}
