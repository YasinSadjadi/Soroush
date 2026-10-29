using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Analyzers;

namespace Soroush.Mediator.Analyzers.Tests;

public class AnalyzerTests
{
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

}
