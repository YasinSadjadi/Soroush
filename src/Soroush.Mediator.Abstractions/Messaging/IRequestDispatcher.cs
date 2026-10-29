namespace Soroush.Mediator.Abstractions;

/// <summary>
/// Dispatches requests on behalf of a sender. Implementations may execute locally or use an external transport.
/// A response or stream must retain the semantics of the corresponding sender operation.
/// </summary>
public interface IRequestDispatcher : ISender { }
