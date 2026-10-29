namespace Soroush.Persistence.Abstractions;

/// <summary>Identifies the unit of work whose save operation is being intercepted.</summary>
public sealed class SaveChangesContext
{
    /// <summary>Creates a save context.</summary>
    public SaveChangesContext(IUnitOfWork unitOfWork)
        => UnitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    /// <summary>Gets the coordinating unit of work. Interceptors must not reenter it.</summary>
    public IUnitOfWork UnitOfWork { get; }
}
