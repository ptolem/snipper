namespace Snipper.Models;

using Microsoft.CodeAnalysis;

public sealed record SnipperFinding(
    string RuleId,
    string Title,
    string Message,
    CertaintyTier Certainty,
    FindingCategory Category,
    string FilePath,
    int LineNumber,
    int CharacterOffset,
    ISymbol? Symbol = null);
