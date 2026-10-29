namespace Soroush.Mediator.Abstractions;

/// <summary>Invokes the next handler in a request pipeline.</summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);

