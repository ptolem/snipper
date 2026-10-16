namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;

/// <summary>
/// Shared rescue for rules whose zero-reference proof fails on extension-method
/// holders. An extension invocation (<c>value.Method()</c>) binds to the method
/// symbol, not the class: a heavily used extension holder can have zero
/// references to its own name, which may never appear in source at all. The type
/// is alive whenever any of its extension methods is.
///
/// Extracted from <see cref="UnusedNonPrivateMemberAnalyser"/> (SNP0006) so
/// SNP0018 applies the identical test instead of a second, subtly different one.
/// </summary>
internal static class ExtensionMethodUsageQuery
{
    public static async Task<bool> HasAnyUsedExtensionMethodAsync(
        INamedTypeSymbol staticType,
        ReferenceSearch referenceSearch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staticType);
        ArgumentNullException.ThrowIfNull(referenceSearch);

        foreach (var member in staticType.GetMembers())
        {
            if (member is not IMethodSymbol { IsExtensionMethod: true } extensionMethod)
            {
                continue;
            }

            if (await referenceSearch.HasAnyAsync(extensionMethod, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}