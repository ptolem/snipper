namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;

/// <summary>
/// Shared interface-implementation detection for rules whose reasoning breaks
/// under polymorphic dispatch (SNP0018 zero-reference proof, SNP0023
/// never-overridden proof, SNP0024 can-be-static). Covers explicit
/// implementations and implicit ones matched through
/// <see cref="INamedTypeSymbol.FindImplementationForInterfaceMember"/>.
/// </summary>
internal static class InterfaceImplementationQuery
{
    public static bool IsInterfaceImplementation(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
            || symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
            || symbol is IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return true;
        }

        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || symbol.ContainingType is not { } containingType)
        {
            return false;
        }

        foreach (var contract in containingType.AllInterfaces)
        {
            foreach (var member in contract.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(member);
                if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
