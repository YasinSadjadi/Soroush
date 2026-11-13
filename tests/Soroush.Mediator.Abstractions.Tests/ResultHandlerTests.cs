using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Abstractions.Tests;

public class ResultHandlerTests
{
    [Theory]
    [InlineData(HandlerResultCode.HasAccess, "access")]
    [InlineData(HandlerResultCode.Validation, "access,validate")]
    [InlineData(HandlerResultCode.Execution, "access,validate,execute")]
    [InlineData(HandlerResultCode.Success, "access,validate,execute")]
    public async Task CommandStagesStopAtFailure(HandlerResultCode failure, string expected)
    {
        var handler = new CommandHandler(failure);
        var result = await handler.Handle(new Command(), CancellationToken.None);
        Assert.Equal(failure, result.Code);
        Assert.Equal(expected, string.Join(",", handler.Calls));
    }

    [Fact]
    public async Task QueryRunsAccessThenExecutionThroughMediator()
    {
        using var provider = new ServiceCollection().AddSoroushMediator(typeof(ResultHandlerTests).Assembly).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Query());
        Assert.Equal(42, result.Value);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamCleansUpOnCompletionOrEarlyDisposal(bool early)
    {
        var handler = new StreamHandler();
        await using (var iterator = handler.Handle(new StreamQuery(), CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            if (!early) Assert.False(await iterator.MoveNextAsync());
        }
        Assert.Equal(new[] { "access", "pre", "execute", "post" }, handler.Calls);
    }

    [Fact]
    public async Task StreamCleansUpOnCancellationAndExecutionException()
    {
        foreach (var cancel in new[] { false, true })
        {
            var handler = new StreamHandler { Throw = !cancel };
            using var cts = new CancellationTokenSource();
            await using var iterator = handler.Handle(new StreamQuery(), cts.Token).GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            if (cancel) cts.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(async () => await iterator.MoveNextAsync());
            Assert.Equal("post", handler.Calls.Last());
            Assert.False(handler.CleanupToken.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task FailedPreProcessRunsCleanupAndReportsStage()
    {
        var handler = new StreamHandler { PreFails = true };
        await using var iterator = handler.Handle(new StreamQuery(), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(HandlerResultCode.PreProcess, iterator.Current.Code);
        Assert.False(await iterator.MoveNextAsync());
        Assert.Equal(new[] { "access", "pre", "post" }, handler.Calls);
    }

    public sealed class Command : ICommand<HandlerResult<int>> { }
    private sealed class CommandHandler(HandlerResultCode failure) : ResultCommandHandler<Command, int>
    {
        public List<string> Calls { get; } = new();
        private HandlerResult Stage(HandlerResultCode code) => failure == code ? HandlerResult.Failure(code) : HandlerResult.Success();
        protected override Task<HandlerResult> HasAccess(Command request, CancellationToken token) { Calls.Add("access"); return Task.FromResult(Stage(HandlerResultCode.HasAccess)); }
        protected override Task<HandlerResult> Validate(Command request, CancellationToken token) { Calls.Add("validate"); return Task.FromResult(Stage(HandlerResultCode.Validation)); }
        protected override Task<HandlerResult<int>> Execute(Command request, CancellationToken token) { Calls.Add("execute"); return Task.FromResult(failure == HandlerResultCode.Execution ? HandlerResult<int>.Failure(failure) : HandlerResult<int>.Success(42)); }
    }
    public sealed class Query : IQuery<HandlerResult<int>> { }
    public sealed class QueryHandler : ResultQueryHandler<Query, int>
    {
        protected override Task<HandlerResult<int>> Execute(Query request, CancellationToken token) => Task.FromResult(HandlerResult<int>.Success(42));
    }
    public sealed class StreamQuery : IStreamableQuery<HandlerResult<int>> { }
    private sealed class StreamHandler : ResultStreamableQueryHandler<StreamQuery, int>
    {
        public List<string> Calls { get; } = new();
        public bool Throw { get; init; }
        public bool PreFails { get; init; }
        public CancellationToken CleanupToken { get; private set; }
        protected override Task<HandlerResult> HasAccess(StreamQuery request, CancellationToken token) { Calls.Add("access"); return Task.FromResult(HandlerResult.Success()); }
        protected override Task<HandlerResult> PreProcess(StreamQuery request, CancellationToken token) { Calls.Add("pre"); return Task.FromResult(PreFails ? HandlerResult.Failure(HandlerResultCode.Execution) : HandlerResult.Success()); }
        protected override async IAsyncEnumerable<HandlerResult<int>> Execute(StreamQuery request, [EnumeratorCancellation] CancellationToken token)
        {
            Calls.Add("execute"); yield return HandlerResult<int>.Success(1);
            await Task.Yield(); token.ThrowIfCancellationRequested();
            if (Throw) throw new InvalidOperationException();
        }
        protected override Task<HandlerResult> PostProcess(StreamQuery request, CancellationToken token) { Calls.Add("post"); CleanupToken = token; return Task.FromResult(HandlerResult.Success()); }
    }
}
