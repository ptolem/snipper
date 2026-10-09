namespace Snipper.Models;

using Microsoft.CodeAnalysis;

/// <summary>
/// Another location for the same finding. Used where a single finding covers several
/// places at once — SNP0031's clone sets, where one skeleton copied 30 times is one
/// finding anchored at one member rather than 30 findings.
/// </summary>
public sealed record RelatedLocation(string FilePath, int LineNumber);

public sealed record SnipperFinding(
    string RuleId,
    string Title,
    string Message,
    CertaintyTier Certainty,
    FindingCategory Category,
    string FilePath,
    int LineNumber,
    int CharacterOffset,
    ISymbol? Symbol = null,
    IReadOnlyList<RelatedLocation>? RelatedLocations = null);
