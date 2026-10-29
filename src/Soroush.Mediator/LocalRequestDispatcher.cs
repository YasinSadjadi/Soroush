using System.Reflection;
using System.Runtime.CompilerServices;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Dispatches requests to handlers resolved from the supplied service provider.</summary>
public sealed class LocalRequestDispatcher : IRequestDispatcher
{
    private readonly IServiceProvider services;

    /// <summary>Creates a local dispatcher. Handler and behavior lifetimes are controlled by the provider.</summary>
    public LocalRequestDispatcher(IServiceProvider services) =>
        this.services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var contract = FindContract(request, typeof(IRequest<>));
        var responseType = contract.GetGenericArguments()[0];
        if (!typeof(TResponse).IsAssignableFrom(responseType))
            throw new ArgumentException("The request response type is incompatible with the requested response type.",
                nameof(request));
        return (TResponse)(await InvokeResponse(request, responseType, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        return request is null
            ? throw new ArgumentNullException(nameof(request))
            : InvokeVoid(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var contracts = GetContracts(request, typeof(IRequest<>));
        if (contracts.Length == 1 && request is not IRequest)
            return await InvokeResponse(request, contracts[0].GetGenericArguments()[0], cancellationToken)
                .ConfigureAwait(false);
        if (contracts.Length == 0 && request is IRequest)
        {
            await InvokeVoid(request, cancellationToken).ConfigureAwait(false);
            return null;
        }

        throw new ArgumentException("The request must implement exactly one non-stream request contract.",
            nameof(request));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var contract = FindContract(request, typeof(IStreamRequest<>));
        var responseType = contract.GetGenericArguments()[0];
        if (!typeof(TResponse).IsAssignableFrom(responseType))
            throw new ArgumentException("The stream item type is incompatible with the requested response type.",
                nameof(request));
        var method = GetType().GetMethod(nameof(StreamTypedCore), BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IAsyncEnumerable<TResponse>)method.MakeGenericMethod(request.GetType(), responseType, typeof(TResponse))
            .Invoke(this, new object[] { request, cancellationToken })!;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var contract = FindContract(request, typeof(IStreamRequest<>));
        return (IAsyncEnumerable<object?>)InvokeGeneric(nameof(StreamObjectCore), request,
            contract.GetGenericArguments()[0], cancellationToken);
    }

    private Task<object?> InvokeResponse(object request, Type responseType, CancellationToken token) =>
        (Task<object?>)InvokeGeneric(nameof(ResponseCore), request, responseType, token);

    private Task InvokeVoid(object request, CancellationToken token) =>
        (Task)InvokeGeneric(nameof(VoidCore), request, null, token);

    private object InvokeGeneric(string name, object request, Type? responseType, CancellationToken token)
    {
        var method = GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var types = responseType is null ? new[] { request.GetType() } : new[] { request.GetType(), responseType };
        return method.MakeGenericMethod(types).Invoke(this, new[] { request, (object)token })!;
    }

    private async Task<object?> ResponseCore<TRequest, TResponse>(TRequest request, CancellationToken token)
        where TRequest : IRequest<TResponse>
    {
        RequestHandlerDelegate<TResponse> next = ct =>
            Resolve<IRequestHandler<TRequest, TResponse>>().Handle(request, ct);
        var behaviors =
            services.GetService(typeof(IEnumerable<IPipelineBehavior<TRequest, TResponse>>)) as
                IEnumerable<IPipelineBehavior<TRequest, TResponse>>;
        if (behaviors != null)
            foreach (var behavior in behaviors.Reverse())
            {
                var inner = next;
                next = ct => behavior.Handle(request, inner, ct);
            }

        return await next(token).ConfigureAwait(false);
    }

    private async Task VoidCore<TRequest>(TRequest request, CancellationToken token) where TRequest : IRequest =>
        await Resolve<IRequestHandler<TRequest>>().Handle(request, token).ConfigureAwait(false);

    private IAsyncEnumerable<TResponse> StreamCore<TRequest, TResponse>(TRequest request, CancellationToken token)
        where TRequest : IStreamRequest<TResponse> =>
        Resolve<IStreamRequestHandler<TRequest, TResponse>>().Handle(request, token);

    private async IAsyncEnumerable<TOutput> StreamTypedCore<TRequest, TItem, TOutput>(TRequest request,
        [EnumeratorCancellation] CancellationToken token) where TRequest : IStreamRequest<TItem>
    {
        await foreach (var item in StreamCore<TRequest, TItem>(request, token).WithCancellation(token)
                           .ConfigureAwait(false)) yield return (TOutput)(object?)item!;
    }

    private async IAsyncEnumerable<object?> StreamObjectCore<TRequest, TResponse>(TRequest request,
        [EnumeratorCancellation] CancellationToken token) where TRequest : IStreamRequest<TResponse>
    {
        await foreach (var item in StreamCore<TRequest, TResponse>(request, token).WithCancellation(token)
                           .ConfigureAwait(false)) yield return item;
    }

    private T Resolve<T>() => (T)(services.GetService(typeof(T)) ??
                                  throw new InvalidOperationException($"No handler is registered for {typeof(T)}."));

    private static Type[] GetContracts(object request, Type definition) => request.GetType().GetInterfaces()
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition).ToArray();

    private static Type FindContract(object request, Type definition)
    {
        var contracts = GetContracts(request, definition);
        if (contracts.Length != 1)
            throw new ArgumentException("The request must implement exactly one matching request contract.",
                nameof(request));
        return contracts[0];
    }
}