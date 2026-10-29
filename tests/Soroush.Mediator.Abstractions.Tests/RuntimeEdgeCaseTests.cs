using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Soroush.Mediator;

namespace Soroush.Mediator.Abstractions.Tests;

public class RuntimeEdgeCaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoidRequestsUseRuntimeTypeAndForwardCancellation(bool boxed)
    {
        var request = new VoidRequest();
        using var source = new CancellationTokenSource();
        var handler = new VoidHandler((actual, token) =>
        {
            Assert.Same(request, actual);
            Assert.Equal(source.Token, token);
            return Task.CompletedTask;
        });
        using var provider = new ServiceCollection().AddSingleton<IRequestHandler<VoidRequest>>(handler)
            .BuildServiceProvider();
        ISender sender = new Mediator(new LocalRequestDispatcher(provider));

        if (boxed) Assert.Null(await sender.Send((object)request, source.Token));
        else await sender.Send<IRequest>(request, source.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseRequestsPreserveNullResults(bool boxed)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((_, _) => Task.FromResult<string?>(null)))
            .BuildServiceProvider();
        ISender sender = new Mediator(new LocalRequestDispatcher(provider));
        Assert.Null(boxed ? await sender.Send((object)new Query()) : await sender.Send(new Query()));
    }

    [Fact]
    public async Task CovariantResponseResolvesHandlerForDeclaredResponse()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((_, _) => Task.FromResult<string?>("value")))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        Assert.Equal("value", await dispatcher.Send<object?>(new Query()));
    }

    [Fact]
    public async Task PipelineWrapsHandlerInRegistrationOrderAndForwardsRequestAndToken()
    {
        var calls = new List<string>();
        var request = new Query();
        using var source = new CancellationTokenSource();
        var services = new ServiceCollection()
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((actual, token) =>
            {
                Assert.Same(request, actual);
                Assert.Equal(source.Token, token);
                calls.Add("handler");
                return Task.FromResult<string?>("result");
            }));
        foreach (var name in new[] { "first", "second" })
            services.AddSingleton<IPipelineBehavior<Query, string?>>(new Behavior(async (actual, next, token) =>
            {
                Assert.Same(request, actual);
                Assert.Equal(source.Token, token);
                calls.Add(name + ":before");
                var result = await next(token);
                calls.Add(name + ":after");
                return result;
            }));
        using var provider = services.BuildServiceProvider();

        Assert.Equal("result", await new LocalRequestDispatcher(provider).Send(request, source.Token));
        Assert.Equal(new[] { "first:before", "second:before", "handler", "second:after", "first:after" }, calls);
    }

    [Fact]
    public async Task PipelineCanShortCircuitWithoutResolvingHandler()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IPipelineBehavior<Query, string?>>(new Behavior((_, _, _) => Task.FromResult<string?>("cached")))
            .BuildServiceProvider();
        Assert.Equal("cached", await new LocalRequestDispatcher(provider).Send(new Query()));
    }

    [Fact]
    public async Task PipelineCanReplaceCancellationToken()
    {
        using var original = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        using var provider = new ServiceCollection()
            .AddSingleton<IPipelineBehavior<Query, string?>>(new Behavior((_, next, token) =>
            {
                Assert.Equal(original.Token, token);
                return next(replacement.Token);
            }))
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((_, token) =>
            {
                Assert.Equal(replacement.Token, token);
                return Task.FromResult<string?>("handled");
            })).BuildServiceProvider();
        Assert.Equal("handled", await new LocalRequestDispatcher(provider).Send(new Query(), original.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerExceptionsAreNotWrappedByReflection(bool boxed)
    {
        var failure = new InvalidOperationException("handler failure");
        using var provider = new ServiceCollection()
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((_, _) => throw failure))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => boxed
            ? dispatcher.Send((object)new Query()) : dispatcher.Send(new Query()));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task VoidHandlerExceptionsAreNotWrappedByReflection()
    {
        var failure = new InvalidOperationException("void failure");
        using var provider = new ServiceCollection()
            .AddSingleton<IRequestHandler<VoidRequest>>(new VoidHandler((_, _) => throw failure))
            .BuildServiceProvider();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalRequestDispatcher(provider).Send(new VoidRequest())));
    }

    [Fact]
    public async Task CanceledHandlerTaskRetainsCancellationToken()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var provider = new ServiceCollection()
            .AddSingleton<IRequestHandler<Query, string?>>(new QueryHandler((_, token) => Task.FromCanceled<string?>(token)))
            .BuildServiceProvider();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalRequestDispatcher(provider).Send(new Query(), source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamsAreLazyAndBoxValueItemsForObjectOverload(bool boxed)
    {
        var called = false;
        var request = new NumberStream();
        using var source = new CancellationTokenSource();
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<NumberStream, int>>(new NumberStreamHandler((actual, token) =>
            {
                Assert.Same(request, actual);
                Assert.Equal(source.Token, token);
                called = true;
                return Numbers(token);
            })).BuildServiceProvider();
        ISender sender = new Mediator(new LocalRequestDispatcher(provider));
        if (boxed)
        {
            var stream = sender.CreateStream((object)request, source.Token);
            Assert.False(called);
            Assert.Equal(new object?[] { 1, 2 }, await Collect(stream));
        }
        else
        {
            var stream = sender.CreateStream(request, source.Token);
            Assert.False(called);
            Assert.Equal(new[] { 1, 2 }, await Collect(stream));
        }
        Assert.True(called);
    }

    [Fact]
    public async Task StreamSupportsCovariantItemsIncludingNull()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<TextStream, string?>>(new TextStreamHandler())
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        Assert.Equal(new object?[] { "text", null }, await Collect(dispatcher.CreateStream<object?>(new TextStream())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamCombinesDispatchAndEnumerationCancellation(bool boxed)
    {
        using var dispatch = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<NumberStream, int>>(new NumberStreamHandler((_, token) => Numbers(token)))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        // Different tokens must both be able to cancel the underlying async iterator.
        if (boxed)
        {
            await using var iterator = dispatcher.CreateStream((object)new NumberStream(), dispatch.Token)
                .GetAsyncEnumerator(enumeration.Token);
            Assert.True(await iterator.MoveNextAsync());
            dispatch.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        }
        else
        {
            await using var iterator = dispatcher.CreateStream(new NumberStream(), dispatch.Token)
                .GetAsyncEnumerator(enumeration.Token);
            Assert.True(await iterator.MoveNextAsync());
            enumeration.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BreakingOutOfStreamDisposesUnderlyingIterator(bool boxed)
    {
        var disposed = false;
        async IAsyncEnumerable<int> Items()
        {
            try { yield return 1; await Task.Yield(); yield return 2; }
            finally { disposed = true; }
        }
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<NumberStream, int>>(new NumberStreamHandler((_, _) => Items()))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        if (boxed) { await foreach (var _ in dispatcher.CreateStream((object)new NumberStream())) break; }
        else { await foreach (var _ in dispatcher.CreateStream(new NumberStream())) break; }
        Assert.True(disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStreamHandlerFailsWhenEnumerated(bool boxed)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (boxed) await Collect(dispatcher.CreateStream((object)new NumberStream()));
            else await Collect(dispatcher.CreateStream(new NumberStream()));
        });
        Assert.Contains("No handler is registered", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamHandlerSynchronousFailureIsNotWrappedByReflection(bool boxed)
    {
        var failure = new InvalidOperationException("stream handler failure");
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<NumberStream, int>>(new NumberStreamHandler((_, _) => throw failure))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (boxed) await Collect(dispatcher.CreateStream((object)new NumberStream()));
            else await Collect(dispatcher.CreateStream(new NumberStream()));
        });
        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterFirstStreamItemPropagatesAndDisposesIterator(bool boxed)
    {
        var failure = new InvalidOperationException("iteration failure");
        var disposed = false;
        async IAsyncEnumerable<int> Items()
        {
            try { yield return 1; await Task.Yield(); throw failure; }
            finally { disposed = true; }
        }
        using var provider = new ServiceCollection()
            .AddSingleton<IStreamRequestHandler<NumberStream, int>>(new NumberStreamHandler((_, _) => Items()))
            .BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        var received = new List<object?>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (boxed) { await foreach (var item in dispatcher.CreateStream((object)new NumberStream())) received.Add(item); }
            else { await foreach (var item in dispatcher.CreateStream(new NumberStream())) received.Add(item); }
        });
        Assert.Same(failure, error);
        Assert.Equal(new object?[] { 1 }, received);
        Assert.True(disposed);
    }

    [Fact]
    public async Task MissingVoidHandlerHasUsefulError()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalRequestDispatcher(provider).Send(new VoidRequest()));
        Assert.Contains("No handler is registered", error.Message);
    }

    [Fact]
    public async Task NullRequestsAreRejectedByEveryOverload()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.Send<string>(null!))).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.Send<VoidRequest>((VoidRequest)null!))).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.Send((object)null!))).ParamName);
        Assert.Equal("request", Assert.Throws<ArgumentNullException>(() => dispatcher.CreateStream<int>(null!)).ParamName);
        Assert.Equal("request", Assert.Throws<ArgumentNullException>(() => dispatcher.CreateStream((object)null!)).ParamName);
    }

    [Fact]
    public async Task ObjectSendRejectsUnsupportedAndAmbiguousRequests()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        foreach (var request in new object[] { new object(), new NumberStream(), new AmbiguousResponse(), new VoidAndResponse() })
            Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.Send(request))).ParamName);
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.Send<string>(new AmbiguousResponse()));
    }

    [Fact]
    public void StreamRejectsUnsupportedAndAmbiguousRequests()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new LocalRequestDispatcher(provider);
        foreach (var request in new object[] { new object(), new Query(), new AmbiguousStream() })
            Assert.Equal("request", Assert.Throws<ArgumentException>(() => dispatcher.CreateStream(request)).ParamName);
        Assert.Throws<ArgumentException>(() => dispatcher.CreateStream<string>(new AmbiguousStream()));
    }

    [Fact]
    public void ConstructorsRejectNullDependencies()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => new LocalRequestDispatcher(null!)).ParamName);
        Assert.Equal("dispatcher", Assert.Throws<ArgumentNullException>(() => new Mediator(null!)).ParamName);
    }

    private static async IAsyncEnumerable<int> Numbers([EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return 1;
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        yield return 2;
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var values = new List<T>();
        await foreach (var value in stream) values.Add(value);
        return values;
    }

    private sealed class VoidRequest : Command { }
    private sealed class Query : IQuery<string?> { }
    private sealed class NumberStream : IStreamableQuery<int> { }
    private sealed class TextStream : IStreamableQuery<string?> { }
    private sealed class AmbiguousResponse : IRequest<string>, IRequest<object> { }
    private sealed class VoidAndResponse : IRequest, IRequest<string> { }
    private sealed class AmbiguousStream : IStreamRequest<string>, IStreamRequest<object> { }
    private sealed class VoidHandler(Func<VoidRequest, CancellationToken, Task> handle) : CommandHandler<VoidRequest>
    {
        public override Task Handle(VoidRequest request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private sealed class QueryHandler(Func<Query, CancellationToken, Task<string?>> handle) : IQueryHandler<Query, string?>
    {
        public Task<string?> Handle(Query request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private sealed class Behavior(Func<Query, RequestHandlerDelegate<string?>, CancellationToken, Task<string?>> handle) : IPipelineBehavior<Query, string?>
    {
        public Task<string?> Handle(Query request, RequestHandlerDelegate<string?> next, CancellationToken cancellationToken) => handle(request, next, cancellationToken);
    }
    private sealed class NumberStreamHandler(Func<NumberStream, CancellationToken, IAsyncEnumerable<int>> handle) : IStreamableQueryHandler<NumberStream, int>
    {
        public IAsyncEnumerable<int> Handle(NumberStream request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private sealed class TextStreamHandler : IStreamableQueryHandler<TextStream, string?>
    {
        public async IAsyncEnumerable<string?> Handle(TextStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return "text";
            await Task.Yield();
            yield return null;
        }
    }
}
