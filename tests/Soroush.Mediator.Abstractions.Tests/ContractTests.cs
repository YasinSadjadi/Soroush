using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Abstractions.Tests;

public class ContractTests
{
    [Fact]
    public async Task CommandAndQueryHandlersAreRequestHandlers()
    {
        IRequestHandler<Command, string> command = new CommandHandler();
        IRequestHandler<Query, string> query = new QueryHandler();
        Assert.Equal("command", await command.Handle(new Command(), CancellationToken.None));
        Assert.Equal("query", await query.Handle(new Query(), CancellationToken.None));
    }

    [Fact]
    public async Task StreamQueryHandlerIsStreamRequestHandler()
    {
        IStreamRequestHandler<StreamQuery, int> handler = new StreamHandler();
        var items = new List<int>();
        await foreach (var item in handler.Handle(new StreamQuery(), CancellationToken.None)) items.Add(item);
        Assert.Equal(new[] { 1, 2 }, items);
    }

    private sealed class Command : ICommand<string> { }
    private sealed class Query : IQuery<string> { }
    private sealed class StreamQuery : IStreamableQuery<int> { }
    private sealed class CommandHandler : ICommandHandler<Command, string>
    {
        public Task<string> Handle(Command request, CancellationToken cancellationToken) => Task.FromResult("command");
    }
    private sealed class QueryHandler : IQueryHandler<Query, string>
    {
        public Task<string> Handle(Query request, CancellationToken cancellationToken) => Task.FromResult("query");
    }
    private sealed class StreamHandler : IStreamableQueryHandler<StreamQuery, int>
    {
        public async IAsyncEnumerable<int> Handle(StreamQuery request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }
    }
}

