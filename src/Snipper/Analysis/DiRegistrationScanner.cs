namespace Snipper.Analysis;

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

        var registeredTypes = new HashSet<INamedTypeSymbol>((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);

        // Sequential binding: workspace compilations are built with
        // ConcurrentBuild=false; concurrent GetTypeInfo is unsupported.
        // ALL documents are scanned: registrations are evidence (extra evidence only
        // ever demotes certainty), and source-generated registration code is common.
        foreach (var project in solution.Projects)
        {
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

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!IsRegistrationInvocation(invocation))
                    {
                        continue;
                    }

                    foreach (var typeSyntax in invocation.DescendantNodes().OfType<TypeSyntax>())
                    {
                        if (semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type is INamedTypeSymbol namedType)
                        {
                            registeredTypes.Add(namedType.OriginalDefinition);
                        }
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
