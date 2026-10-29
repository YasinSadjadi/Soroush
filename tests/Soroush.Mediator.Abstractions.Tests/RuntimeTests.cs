using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Abstractions.Tests;

public class RuntimeTests
{
    private sealed class Query : IQuery<string> { }
    private sealed class Handler : IQueryHandler<Query, string>
    {
        public Task<string> Handle(Query request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }
    private sealed class Behavior : IPipelineBehavior<Query, string>
    {
        public async Task<string> Handle(Query request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => "before " + await next(cancellationToken);
    }
    private sealed class Services : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IRequestHandler<Query, string>) ? new Handler() :
            type == typeof(IEnumerable<IPipelineBehavior<Query, string>>) ? new[] { new Behavior() } : null;
    }
    [Fact]
    public async Task LocalDispatchRunsPipelineForTypedAndObjectRequests()
    {
        ISender sender = new Mediator(new LocalRequestDispatcher(new Services()));
        Assert.Equal("before handled", await sender.Send(new Query()));
        Assert.Equal("before handled", await sender.Send((object)new Query()));
    }
    [Fact]
    public async Task MissingHandlerHasUsefulError()
    {
        var dispatcher = new LocalRequestDispatcher(new EmptyServices());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Send(new Query()));
        Assert.Contains("No handler is registered", error.Message);
    }
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
}
