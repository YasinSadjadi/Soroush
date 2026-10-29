using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Soroush.Mediator.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatorContractAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor MultipleContracts = new(
        "SOR001", "Ambiguous mediator request", "Request '{0}' implements multiple command or query contracts", "Soroush.Mediator", DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);
    private static readonly DiagnosticDescriptor ResponseMismatch = new(
        "SOR002", "Handler response does not match request", "Handler '{0}' specifies response '{1}', but request '{2}' declares '{3}'", "Soroush.Mediator", DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);
    private static readonly DiagnosticDescriptor DuplicateHandler = new(
        "SOR003", "Multiple handlers for request", "Request '{0}' has multiple handlers in this compilation and its referenced assemblies", "Soroush.Mediator", DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);
    private static readonly DiagnosticDescriptor MissingHandler = new(
        "SOR004", "No handler found in this compilation", "No handler for request '{0}' was found in this compilation; it may be defined in another assembly", "Soroush.Mediator", DiagnosticSeverity.Warning, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MultipleContracts, ResponseMismatch, DuplicateHandler, MissingHandler);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var command = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.ICommand");
            var commandResult = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.ICommand`1");
            var query = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.IQuery`1");
            var stream = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.IStreamableQuery`1");
            var commandHandler = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.ICommandHandler`1");
            var commandResultHandler = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.ICommandHandler`2");
            var queryHandler = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.IQueryHandler`2");
            var streamHandler = start.Compilation.GetTypeByMetadataName("Soroush.Mediator.Abstractions.IStreamableQueryHandler`2");
            if (command is null || commandResult is null || query is null || stream is null || commandHandler is null || commandResultHandler is null || queryHandler is null || streamHandler is null) return;
            var requests = new ConcurrentBag<INamedTypeSymbol>();
            var handlers = new ConcurrentBag<INamedTypeSymbol>();
            bool Is(INamedTypeSymbol type, INamedTypeSymbol definition) => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, definition);
            bool RequestContract(INamedTypeSymbol type) => Is(type, command) || Is(type, commandResult) || Is(type, query) || Is(type, stream);
            bool HandlerContract(INamedTypeSymbol type) => Is(type, commandHandler) || Is(type, commandResultHandler) || Is(type, queryHandler) || Is(type, streamHandler);
            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract || type.IsGenericType || type.ContainingType?.IsGenericType == true) return;
                if (type.AllInterfaces.Any(RequestContract)) requests.Add(type);
                if (type.AllInterfaces.Any(HandlerContract)) handlers.Add(type);
            }, SymbolKind.NamedType);
            start.RegisterCompilationEndAction(end =>
            {
                // Roslyn analyzes one compilation at a time. Include types from referenced
                // assemblies so contracts and handlers can be split across projects.
                var referencedTypes = new List<INamedTypeSymbol>();
                void VisitType(INamedTypeSymbol type)
                {
                    if (type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract && !type.IsGenericType && type.ContainingType?.IsGenericType != true)
                        referencedTypes.Add(type);
                    foreach (var nested in type.GetTypeMembers()) VisitType(nested);
                }
                void VisitNamespace(INamespaceSymbol ns)
                {
                    foreach (var type in ns.GetTypeMembers()) VisitType(type);
                    foreach (var child in ns.GetNamespaceMembers()) VisitNamespace(child);
                }
                var abstractionAssembly = command.ContainingAssembly;
                bool ReferencesAbstractions(IAssemblySymbol assembly, HashSet<IAssemblySymbol> visited)
                {
                    if (SymbolEqualityComparer.Default.Equals(assembly, abstractionAssembly)) return true;
                    if (!visited.Add(assembly)) return false;
                    return assembly.Modules.Any(m => m.ReferencedAssemblySymbols.Any(a => ReferencesAbstractions(a, visited)));
                }
                foreach (var assembly in end.Compilation.SourceModule.ReferencedAssemblySymbols)
                {
                    end.CancellationToken.ThrowIfCancellationRequested();
                    if (SymbolEqualityComparer.Default.Equals(assembly, abstractionAssembly) ||
                        !ReferencesAbstractions(assembly, new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default))) continue;
                    VisitNamespace(assembly.GlobalNamespace);
                }
                var sourceRequests = requests.ToArray();
                var sourceHandlers = handlers.ToArray();
                var handlerList = sourceHandlers.Concat(referencedTypes.Where(t => t.AllInterfaces.Any(HandlerContract))).ToArray();
                var allRequests = sourceRequests.Concat(referencedTypes.Where(t => t.AllInterfaces.Any(RequestContract)))
                    .Concat(sourceHandlers.SelectMany(h => h.AllInterfaces.Where(HandlerContract))
                        .Select(i => i.TypeArguments[0]).OfType<INamedTypeSymbol>())
                    .Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>();
                foreach (var request in allRequests)
                {
                    var contracts = request.AllInterfaces.Where(RequestContract).ToArray();
                    var location = request.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location != null && contracts.Length > 1) end.ReportDiagnostic(Diagnostic.Create(MultipleContracts, location, request.Name));
                    var matching = handlerList.Where(h => h.AllInterfaces.Any(i => HandlerContract(i) && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], request))).ToArray();
                    if (matching.Length == 0 && location != null) end.ReportDiagnostic(Diagnostic.Create(MissingHandler, location, request.Name));
                    else if (matching.Length > 1)
                    {
                        // Metadata locations cannot be reported against the current project.
                        var diagnosticLocation = location ?? matching.SelectMany(h => h.Locations).FirstOrDefault(l => l.IsInSource);
                        if (diagnosticLocation != null) end.ReportDiagnostic(Diagnostic.Create(DuplicateHandler, diagnosticLocation, request.Name));
                    }
                }
                foreach (var handler in sourceHandlers)
                foreach (var contract in handler.AllInterfaces.Where(HandlerContract))
                {
                    if (contract.TypeArguments.Length != 2 || contract.TypeArguments[0] is not INamedTypeSymbol request) continue;
                    var expectedDefinition = Is(contract, commandResultHandler) ? commandResult : Is(contract, queryHandler) ? query : stream;
                    var declared = request.AllInterfaces.Where(i => Is(i, expectedDefinition)).ToArray();
                    if (declared.Length == 1 && !SymbolEqualityComparer.Default.Equals(declared[0].TypeArguments[0], contract.TypeArguments[1]))
                        end.ReportDiagnostic(Diagnostic.Create(ResponseMismatch, handler.Locations.FirstOrDefault(l => l.IsInSource), handler.Name, contract.TypeArguments[1].ToDisplayString(), request.Name, declared[0].TypeArguments[0].ToDisplayString()));
                }
            });
        });
    }
}

