namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Over-approximates the set of assemblies a project actually consumes symbols from.
/// Over-approximation is intentional: erring towards "used" prevents false-positive
/// "unreferenced package/project" findings.
///
/// ALL documents are scanned — including generated and external ones. This is usage
/// evidence, not finding location: generated code (source generators, protobuf
/// clients) and linked/external sources can legitimately consume a referenced
/// assembly, and missing that evidence produces false SNP0003/SNP0004 reports.
/// </summary>
internal static class SymbolUsageCollector
{
    public static async Task<FrozenSet<IAssemblySymbol>> CollectUsedAssembliesAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var usedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);

        // Sequential binding: workspace compilations are built with
        // ConcurrentBuild=false; concurrent GetSymbolInfo is unsupported.
        foreach (var document in project.Documents)
        {
            if (!document.SupportsSyntaxTree)
            {
                continue;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null || root is null)
            {
                continue;
            }

            foreach (var node in root.DescendantNodes())
            {
                if (node is not (ExpressionSyntax or AttributeSyntax))
                {
                    continue;
                }

                var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
                AddSymbolAssembly(symbolInfo.Symbol, usedAssemblies);

                if (symbolInfo.Symbol is null)
                {
                    foreach (var candidate in symbolInfo.CandidateSymbols)
                    {
                        AddSymbolAssembly(candidate, usedAssemblies);
                    }

                    if (node is ExpressionSyntax expression)
                    {
                        AddSymbolAssembly(semanticModel.GetTypeInfo(expression, cancellationToken).Type, usedAssemblies);
                    }
                }
            }
        }

        // Frozen: built once per project, then queried for every declared package/project reference.
        return usedAssemblies.ToFrozenSet((IEqualityComparer<IAssemblySymbol>)SymbolEqualityComparer.Default);
    }

    private static void AddSymbolAssembly(ISymbol? symbol, HashSet<IAssemblySymbol> usedAssemblies)
    {
        if (symbol is null)
        {
            return;
        }

        var assembly = symbol switch
        {
            IAssemblySymbol assemblySymbol => assemblySymbol,
            IArrayTypeSymbol arrayType => arrayType.ElementType.ContainingAssembly,
            _ => symbol.OriginalDefinition.ContainingAssembly,
        };

        if (assembly is not null)
        {
            usedAssemblies.Add(assembly);
        }
    }
}
