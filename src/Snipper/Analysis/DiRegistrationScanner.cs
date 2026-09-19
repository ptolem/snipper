namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Discovers types registered with the dependency injection container
/// (AddScoped/AddTransient/AddSingleton/AddHostedService/Configure/AddOptions).
/// Members of registered types are demoted to Advisory certainty since the
/// container may resolve them reflectively at runtime.
/// </summary>
internal static class DiRegistrationScanner
{
    private static readonly FrozenSet<string> RegistrationMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "AddScoped",
        "AddTransient",
        "AddSingleton",
        "AddHostedService",
        "Configure",
        "AddOptions",
        "TryAddScoped",
        "TryAddTransient",
        "TryAddSingleton",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static async Task<FrozenSet<INamedTypeSymbol>> ScanAsync(Solution solution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Pass 1 (syntax only, parallel): collect registration-shaped invocations.
        // Registration calls cluster in a handful of startup/extension files —
        // documents without one never pay for a semantic model (syntax-first;
        // previously every document in the solution bound a model here).
        var candidateInvocations = new ConcurrentBag<(Document Document, InvocationExpressionSyntax Invocation)>();
        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);

        // ALL documents are scanned: registrations are evidence (extra evidence only
        // ever demotes certainty), and source-generated registration code is common.
        Parallel.ForEach(
            documents,
            AnalysisParallelism.CreateOptions(cancellationToken),
            document =>
            {
                var root = document.GetSyntaxRootAsync(cancellationToken).GetAwaiter().GetResult();
                if (root is null)
                {
                    return;
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (IsRegistrationInvocation(invocation))
                    {
                        candidateInvocations.Add((document, invocation));
                    }
                }
            });

        // Pass 2: resolve the type syntaxes — one semantic model per
        // candidate-bearing document, sequential binding per the workspace's
        // ConcurrentBuild=false contract (parallelising per-document binding is
        // AnalysisParallelism-gated elsewhere; this pass is a handful of files).
        var registeredTypes = new HashSet<INamedTypeSymbol>((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
        foreach (var group in candidateInvocations.GroupBy(static candidate => candidate.Document))
        {
            var semanticModel = await group.Key.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }

            foreach (var (_, invocation) in group)
            {
                foreach (var typeSyntax in invocation.DescendantNodes().OfType<TypeSyntax>())
                {
                    if (semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type is INamedTypeSymbol namedType)
                    {
                        registeredTypes.Add(namedType.OriginalDefinition);
                    }
                }
            }
        }

        // Frozen: built once per run, queried for every candidate member thereafter.
        return registeredTypes.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
    }

    private static bool IsRegistrationInvocation(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            _ => null,
        };

        var identifier = name switch
        {
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax identifierName => identifierName.Identifier.Text,
            _ => null,
        };

        return identifier is not null && RegistrationMethodNames.Contains(identifier);
    }
}
