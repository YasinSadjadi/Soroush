using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Analyzers;

namespace Soroush.Mediator.Analyzers.Tests;

public class AnalyzerTests
{
    [Theory]
    [InlineData("ICommand", "IRequestHandler<Q>", "Task", "Task.CompletedTask")]
    [InlineData("IQuery<int>", "IRequestHandler<Q, int>", "Task<int>", "Task.FromResult(1)")]
    [InlineData("IStreamableQuery<int>", "IStreamRequestHandler<Q, int>", "IAsyncEnumerable<int>", "null!")]
    public async Task BaseHandlerContractsSatisfyRequests(string request, string handler, string result, string body)
    {
        Assert.Empty(await Analyze($"class Q : {request} {{ }} class H : {handler} {{ public {result} Handle(Q q, CancellationToken t) => {body}; }}"));
    }

    [Fact]
    public async Task BaseHandlersParticipateInDuplicateAndResponseChecks()
    {
        var diagnostics = await Analyze("""
            class Q : IQuery<string> { }
            class First : IQueryHandler<Q, string> { public Task<string> Handle(Q q, CancellationToken t) => Task.FromResult(""); }
            class Second : IRequestHandler<Q, object> { public Task<object> Handle(Q q, CancellationToken t) => Task.FromResult<object>(""); }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SOR003");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SOR002");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SOR004");
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, params string[] referencedSources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(ICommand).Assembly.Location));
        foreach (var referencedSource in referencedSources)
        {
            var dependency = CSharpCompilation.Create("Dependency" + references.Count,
                [CSharpSyntaxTree.ParseText("using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using Soroush.Mediator.Abstractions; " + referencedSource)],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emit = dependency.Emit(image);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
            references.Add(MetadataReference.CreateFromImage(image.ToArray()));
        }
        var compilation = CSharpCompilation.Create("Sample", [CSharpSyntaxTree.ParseText("using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using Soroush.Mediator.Abstractions; " + source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MediatorContractAnalyzer())).GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task ValidContractsHaveNoDiagnostics()
    {
        var diagnostics = await Analyze("""
            class C : ICommand { } class CH : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; }
            class Q : IQuery<string> { } class QH : IQueryHandler<Q, string> { public Task<string> Handle(Q q, CancellationToken t) => Task.FromResult(""); }
            class S : IStreamableQuery<int> { } class SH : IStreamableQueryHandler<S, int> { public IAsyncEnumerable<int> Handle(S s, CancellationToken t) => null!; }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ProjectedQueriesRequireConcreteHandlersAndRecognizeInheritedHandlerContracts()
    {
        var diagnostics = await Analyze("""
            class Entity { public int Id { get; set; } }
            class Dto { public int Id { get; set; } }
            class Query : Soroush.Mediator.Abstractions.ProjectedQuery<Dto> { }
            abstract class IntermediateHandler : Soroush.Mediator.Abstractions.ProjectedQueryHandler<Query, Entity, Dto>
            {
                protected IntermediateHandler(Soroush.Mediator.Abstractions.IQueryContext context) : base(context) { }
                protected override System.Linq.Expressions.Expression<System.Func<Entity, Dto>> Mapping
                    => entity => new Dto { Id = entity.Id };
            }
            class QueryHandler : IntermediateHandler
            {
                public QueryHandler(Soroush.Mediator.Abstractions.IQueryContext context) : base(context) { }
            }
            class MissingQuery : Soroush.Mediator.Abstractions.ProjectedQuery<Dto> { }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SOR004", diagnostic.Id);
        Assert.Contains("MissingQuery", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ProjectedQueriesStillRejectMultipleRequestContracts()
    {
        var diagnostics = await Analyze("""
            class Entity { public int Id { get; set; } }
            class Dto { public int Id { get; set; } }
            class Query : Soroush.Mediator.Abstractions.ProjectedQuery<Dto>, IQuery<int> { }
            class QueryHandler : Soroush.Mediator.Abstractions.ProjectedQueryHandler<Query, Entity, Dto>
            {
                public QueryHandler(Soroush.Mediator.Abstractions.IQueryContext context) : base(context) { }
                protected override System.Linq.Expressions.Expression<System.Func<Entity, Dto>> Mapping
                    => entity => new Dto { Id = entity.Id };
            }
            """);

        Assert.Equal("SOR001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task DetectsMissingAndMultipleContracts()
    {
        var diagnostics = await Analyze("class Q : ICommand, IQuery<int> { }");
        Assert.Contains(diagnostics, d => d.Id == "SOR001");
        Assert.Contains(diagnostics, d => d.Id == "SOR004");
    }

    [Fact]
    public async Task DetectsDuplicateHandlers()
    {
        var diagnostics = await Analyze("""
            class C : ICommand { }
            class H1 : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; }
            class H2 : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; }
            """);
        Assert.Contains(diagnostics, d => d.Id == "SOR003");
    }

    [Fact]
    public async Task DetectsCovariantResponseMismatch()
    {
        var diagnostics = await Analyze("""
            class Q : IQuery<string> { }
            class H : IQueryHandler<Q, object> { public Task<object> Handle(Q q, CancellationToken t) => Task.FromResult<object>(""); }
            """);
        Assert.Contains(diagnostics, d => d.Id == "SOR002");
    }

    [Fact]
    public async Task IgnoresAbstractAndOpenGenericTypes()
    {
        var diagnostics = await Analyze("abstract class Q : IQuery<int> { } class Generic<T> : IQuery<T> { }");
        Assert.Empty(diagnostics);
    }
    [Fact]
    public async Task FindsHandlerInReferencedAssembly()
    {
        var diagnostics = await Analyze("public class Local : ICommand { }", "public class Other : ICommand { } public class OtherHandler : ICommandHandler<Other> { public Task Handle(Other c, CancellationToken t) => Task.CompletedTask; }");
        Assert.Single(diagnostics, d => d.Id == "SOR004" && d.GetMessage().Contains("Local"));
    }

    [Fact]
    public async Task DetectsDuplicateHandlersForRequestInReferencedProject()
    {
        var diagnostics = await Analyze("public class LocalHandler : ICommandHandler<Request> { public Task Handle(Request c, CancellationToken t) => Task.CompletedTask; }",
            "public class Request : ICommand { } public class FirstHandler : ICommandHandler<Request> { public Task Handle(Request c, CancellationToken t) => Task.CompletedTask; }");
        Assert.Contains(diagnostics, d => d.Id == "SOR003" && d.Location.IsInSource);
    }

    [Fact]
    public async Task DetectsResponseMismatchForReferencedRequest()
    {
        var diagnostics = await Analyze("public class Handler : IQueryHandler<Request, object> { public Task<object> Handle(Request q, CancellationToken t) => Task.FromResult<object>(null!); }",
            "public class Request : IQuery<string> { }");
        Assert.Contains(diagnostics, d => d.Id == "SOR002");
    }

    [Theory]
    [InlineData("ICommand<string>", "ICommandHandler<Q, object>", "Task<object>", "Task.FromResult<object>(null!)")]
    [InlineData("IStreamableQuery<string>", "IStreamableQueryHandler<Q, object>", "IAsyncEnumerable<object>", "null!")]
    public async Task DetectsCovariantMismatchForCommandsAndStreams(string requestContract, string handlerContract, string result, string body)
    {
        var diagnostics = await Analyze($"class Q : {requestContract} {{ }} class H : {handlerContract} {{ public {result} Handle(Q q, CancellationToken t) => {body}; }}");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SOR002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public async Task ValidResponseCommandHasNoDiagnostics()
    {
        Assert.Empty(await Analyze("""
            class C : Command<int> { }
            class H : CommandHandler<C, int> { public override Task<int> Handle(C c, CancellationToken t) => Task.FromResult(42); }
            """));
    }

    [Fact]
    public async Task InheritedAndPartialContractsDoNotCountAsDuplicates()
    {
        Assert.Empty(await Analyze("""
            interface ICustomQuery : IQuery<int> { }
            partial class Q : ICustomQuery { } partial class Q { }
            abstract class BaseHandler : IQueryHandler<Q, int> { public abstract Task<int> Handle(Q q, CancellationToken t); }
            class H : BaseHandler { public override Task<int> Handle(Q q, CancellationToken t) => Task.FromResult(1); }
            """));
    }

    [Fact]
    public async Task OneHandlerMayHandleSeveralRequestsWithoutDuplicateDiagnostics()
    {
        Assert.Empty(await Analyze("""
            class A : ICommand { } class B : ICommand { }
            class H : ICommandHandler<A>, ICommandHandler<B>
            {
                public Task Handle(A a, CancellationToken t) => Task.CompletedTask;
                public Task Handle(B b, CancellationToken t) => Task.CompletedTask;
            }
            """));
    }

    [Fact]
    public async Task MissingHandlerIsWarningLocatedOnRequest()
    {
        var diagnostic = Assert.Single(await Analyze("class Missing : IQuery<int> { }"));
        Assert.Equal("SOR004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Missing", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public async Task GenericAndAbstractHandlersDoNotSatisfyConcreteRequest()
    {
        var diagnostics = await Analyze("""
            class C : ICommand { }
            abstract class AbstractHandler : ICommandHandler<C> { public abstract Task Handle(C c, CancellationToken t); }
            class GenericHandler<T> : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; }
            class Container<T> { public class Nested : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; } }
            """);
        Assert.Equal("SOR004", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task ReferencedMissingRequestsDoNotProduceSourceWarnings()
    {
        Assert.Empty(await Analyze("class Unrelated { }", "public class Remote : IQuery<int> { }"));
    }

    [Fact]
    public async Task HandlerForReferencedRequestAndNestedMetadataTypesAreDiscovered()
    {
        Assert.Empty(await Analyze("""
            class LocalHandler : ICommandHandler<Remote> { public Task Handle(Remote c, CancellationToken t) => Task.CompletedTask; }
            """, """
            public class Remote : ICommand { }
            namespace Nested { public class Container {
                public class Q : IQuery<int> { }
                public class H : IQueryHandler<Q, int> { public Task<int> Handle(Q q, CancellationToken t) => Task.FromResult(1); }
            } }
            """));
    }

    [Fact]
    public async Task DetectsDuplicateWithRequestAndHandlerInSeparateReferencedAssemblies()
    {
        var diagnostics = await Analyze("class Local : ICommandHandler<Request> { public Task Handle(Request c, CancellationToken t) => Task.CompletedTask; }",
            "public class Request : ICommand { }",
            "public class First : ICommandHandler<Request> { public Task Handle(Request c, CancellationToken t) => Task.CompletedTask; }");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SOR003", diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public async Task StructRequestsAndHandlersAreAnalyzed()
    {
        Assert.Empty(await Analyze("""
            struct C : ICommand { }
            struct H : ICommandHandler<C> { public Task Handle(C c, CancellationToken t) => Task.CompletedTask; }
            """));
    }

    [Fact]
    public async Task UnrelatedTypesWithSimilarContractNamesAreIgnored()
    {
        Assert.Empty(await Analyze("""
            namespace Other {
                interface ICommand { }
                interface IQuery<T> { }
                class Q : ICommand, IQuery<int> { }
            }
            """));
    }

}
