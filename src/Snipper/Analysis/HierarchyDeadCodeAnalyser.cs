namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snipper.Models;

/// <summary>
/// SNP0023 — Hierarchy dead code. One rule, two checks sharing
/// <see cref="InheritanceGraph"/>: (1) a virtual member that is never overridden
/// is a speculative extension point, and (2) a class that declares virtual
/// members yet is never inherited carries the abstraction for nothing. The
/// class-level finding suppresses member-level findings on the same class
/// (root-cause dedup, the SNP0002/SNP0009 philosophy); zero-reference
/// types/members stay with SNP0001/0005/0006 (usage gates everywhere).
/// Only virtual chain roots are candidates — overrides fulfil an existing
/// contract, abstract members demand a subclass by definition, and interface
/// implementations dispatch through the contract. Certainty: Moderate, demoted
/// to Advisory on exported public surface or with friend assemblies (external
/// code can inherit); a type whose name is spelled in a string literal or
/// solution JSON is plugin-loading evidence and suppresses the finding
/// entirely (batched via <see cref="AssemblyNameEvidenceScanner"/>).
///
/// SNP0027 — Unused member hierarchy: an override family (root + every
/// override reaching it) whose confirmed references all land inside the
/// family's own declaration spans keeps itself alive with no external caller
/// (ReSharper's UnusedMemberHierarchy). One finding per family, at the root;
/// never-overridden virtuals stay with SNP0023, zero-reference families with
/// SNP0005/0006, abstract links and unconfirmed locations suppress.
/// </summary>
public sealed class HierarchyDeadCodeAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0023", "SNP0027"];

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var usageIndex = SolutionUsageIndex.Get(solution);
        var graph = InheritanceGraph.Get(solution);

        // Phase 1: gather candidate classes (syntax pre-filtered, symbol-confirmed).
        var candidates = new List<CandidateClass>();
        var familyCandidates = new List<ISymbol>();
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"HierarchyDeadCodeAnalyser: scanning {project.Name}");

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var hasFriendAssemblies = compilation.Assembly.GetAttributes()
                .Any(static a => a.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute");

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null || root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // SNP0027 gate: any member declared virtual or override —
                    // a different gate than SNP0023's (which needs the class-level
                    // modifiers below). Symbol resolved once for both.
                    var declaresFamilyMember = DeclaresVirtualOrOverrideMember(classDeclaration);
                    if (declaresFamilyMember
                        && semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is { } familyClassSymbol)
                    {
                        CollectFamilyCandidates(familyClassSymbol, familyCandidates);
                    }

                    // Syntax gate: no static/abstract/sealed modifiers, at least one
                    // direct member declared with the virtual modifier.
                    if (classDeclaration.Modifiers.Any(static m => m.IsKind(SyntaxKind.StaticKeyword)
                        || m.IsKind(SyntaxKind.AbstractKeyword)
                        || m.IsKind(SyntaxKind.SealedKeyword))
                        || !DeclaresVirtualMember(classDeclaration))
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not { } classSymbol
                        || classSymbol.TypeKind is not TypeKind.Class
                        || classSymbol.IsAbstract
                        || classSymbol.IsSealed
                        || classSymbol.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(classSymbol) || ExclusionEngine.IsNamespaceExcluded(classSymbol, _exclusions))
                    {
                        continue;
                    }

                    var virtualMembers = new List<ISymbol>();
                    foreach (var member in classSymbol.GetMembers())
                    {
                        var isCandidate = member switch
                        {
                            IMethodSymbol method => method is { IsVirtual: true, MethodKind: MethodKind.Ordinary },
                            IPropertySymbol property => property is { IsVirtual: true, IsIndexer: false },
                            _ => false,
                        };

                        if (!isCandidate
                            || member.IsAbstract
                            || InterfaceImplementationQuery.IsInterfaceImplementation(member)
                            || ExclusionEngine.ShouldExclude(member))
                        {
                            continue;
                        }

                        virtualMembers.Add(member);
                    }

                    if (virtualMembers.Count > 0)
                    {
                        candidates.Add(new CandidateClass(classSymbol, virtualMembers, project, hasFriendAssemblies));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            return findings;
        }

        // Phase 2: one batched evidence sweep for every candidate type name —
        // plugin-style loading by name suppresses the whole type (members too).
        var candidateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidateNames.Add(candidate.Symbol.Name);
        }

        foreach (var member in familyCandidates)
        {
            candidateNames.Add(member.Name);
            if (member.ContainingType is not null)
            {
                candidateNames.Add(member.ContainingType.Name);
            }
        }

        var nameEvidence = AssemblyNameEvidenceScanner.FindSpelledNames(solution, candidateNames);
        if (solution.FilePath is { Length: > 0 } solutionPath
            && Path.GetDirectoryName(solutionPath) is { Length: > 0 } rootDirectory)
        {
            nameEvidence.UnionWith(AssemblyNameEvidenceScanner.FindSpelledNamesInJsonFiles(rootDirectory, candidateNames));
        }

        // Phase 3: evaluate. Sequential binding per the ConcurrentBuild=false contract.
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var classSymbol = candidate.Symbol;
            if (nameEvidence.Contains(classSymbol.Name))
            {
                continue;
            }

            if (!graph.HasAnyDerivedClass(classSymbol))
            {
                // Check 2: class-level finding requires the class itself to be used —
                // an unreferenced class is plain dead code (SNP0005/0006), and its
                // members fail the usage gate with it, so no member checks run here.
                var hasReference = await HasAnyReferenceAsync(classSymbol, candidate, solution, usageIndex, cancellationToken).ConfigureAwait(false);
                if (hasReference)
                {
                    var lineSpan = classSymbol.Locations[0].GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0023",
                        Title: "Never-Inherited Virtual Class",
                        Message: $"Class '{classSymbol.Name}' declares virtual members but is never inherited.",
                        Certainty: TierFor(classSymbol, candidate.HasFriendAssemblies),
                        Category: FindingCategory.HierarchyDeadCode,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: classSymbol));
                }

                continue;
            }

            // Check 1: member-level — the class IS inherited, so individual virtual
            // chain roots nothing overrides are the speculative extension points.
            IReadOnlyList<INamedTypeSymbol>? derivedClosure = null;
            foreach (var member in candidate.VirtualMembers)
            {
                if (!await HasAnyReferenceAsync(member, candidate, solution, usageIndex, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                derivedClosure ??= graph.GetTransitivelyDerivedClasses(classSymbol);
                if (IsOverriddenInClosure(member, derivedClosure))
                {
                    continue;
                }

                var memberLineSpan = member.Locations[0].GetLineSpan();
                findings.Add(new SnipperFinding(
                    RuleId: "SNP0023",
                    Title: "Never-Overridden Virtual Member",
                    Message: $"Virtual member '{member.Name}' is never overridden.",
                    Certainty: TierFor(member, candidate.HasFriendAssemblies),
                    Category: FindingCategory.HierarchyDeadCode,
                    FilePath: memberLineSpan.Path ?? string.Empty,
                    LineNumber: memberLineSpan.StartLinePosition.Line + 1,
                    CharacterOffset: memberLineSpan.StartLinePosition.Character + 1,
                    Symbol: member));
            }
        }

        // Phase 4 (SNP0027): override families with no external caller.
        var processedRoots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var member in familyCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var familyRoot = FamilyRootOf(member);
            if (familyRoot is null
                || familyRoot.IsAbstract
                || familyRoot.ContainingType is null
                || !processedRoots.Add(familyRoot))
            {
                continue;
            }

            // Family = root + every override in the root's derived closure whose
            // override chain reaches it. An abstract link anywhere in the chain
            // excludes the family (v1: abstract dispatch has no safe root finding).
            var family = new List<ISymbol> { familyRoot };
            var familyHasAbstractLink = false;
            foreach (var derivedClass in graph.GetTransitivelyDerivedClasses(familyRoot.ContainingType))
            {
                foreach (var candidate in derivedClass.GetMembers(familyRoot.Name))
                {
                    for (var link = candidate; link is not null; link = OverriddenLink(link))
                    {
                        if (link.IsAbstract)
                        {
                            familyHasAbstractLink = true;
                        }

                        if (SymbolEqualityComparer.Default.Equals(link.OriginalDefinition, familyRoot.OriginalDefinition))
                        {
                            family.Add(candidate);
                            break;
                        }
                    }
                }
            }

            // Never-overridden virtuals stay with SNP0023; entirely unreferenced
            // families stay with SNP0005/0006.
            if (familyHasAbstractLink || family.Count < 2)
            {
                continue;
            }

            if (nameEvidence.Contains(familyRoot.Name) || nameEvidence.Contains(familyRoot.ContainingType.Name))
            {
                continue;
            }

            var candidateDocuments = usageIndex.GetDocumentsUsingName(familyRoot.Name);
            if (candidateDocuments.Count == 0)
            {
                continue;
            }

            // External-caller proof: every confirmed reference to every family
            // member must land inside a family member's own declaration span
            // (base. and sibling-chain calls). Unconfirmed locations suppress —
            // unknown evidence is never a finding.
            var familySpans = GetFamilySpans(family, cancellationToken);
            var sawReference = false;
            var sawExternalOrUnknown = false;

            foreach (var familyMember in family)
            {
                var references = await SymbolFinder.FindReferencesAsync(familyMember, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
                foreach (var referencedSymbol in references)
                {
                    foreach (var location in referencedSymbol.Locations)
                    {
                        if (location.IsCandidateLocation)
                        {
                            sawExternalOrUnknown = true;
                            break;
                        }

                        sawReference = true;
                        if (!IsInsideAnySpan(location.Location, familySpans))
                        {
                            sawExternalOrUnknown = true;
                            break;
                        }
                    }

                    if (sawExternalOrUnknown)
                    {
                        break;
                    }
                }

                if (sawExternalOrUnknown)
                {
                    break;
                }
            }

            if (!sawReference || sawExternalOrUnknown)
            {
                continue;
            }

            var hasFriendAssemblies = familyRoot.ContainingAssembly.GetAttributes()
                .Any(static a => a.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute");
            var rootLineSpan = familyRoot.Locations[0].GetLineSpan();
            findings.Add(new SnipperFinding(
                RuleId: "SNP0027",
                Title: "Unused Member Hierarchy",
                Message: $"Virtual member '{familyRoot.Name}' and its {family.Count - 1} override(s) reference only each other — the chain has no external callers.",
                Certainty: TierFor(familyRoot, hasFriendAssemblies),
                Category: FindingCategory.HierarchyDeadCode,
                FilePath: rootLineSpan.Path ?? string.Empty,
                LineNumber: rootLineSpan.StartLinePosition.Line + 1,
                CharacterOffset: rootLineSpan.StartLinePosition.Character + 1,
                Symbol: familyRoot));
        }

        return findings;
    }

    private void CollectFamilyCandidates(INamedTypeSymbol classSymbol, List<ISymbol> familyCandidates)
    {
        foreach (var member in classSymbol.GetMembers())
        {
            var isCandidate = member switch
            {
                IMethodSymbol method => method is { MethodKind: MethodKind.Ordinary, IsAbstract: false, IsImplicitlyDeclared: false }
                    && (method.IsVirtual || method.IsOverride),
                IPropertySymbol property => property is { IsIndexer: false, IsAbstract: false, IsImplicitlyDeclared: false }
                    && (property.IsVirtual || property.IsOverride),
                _ => false,
            };

            if (!isCandidate
                || ExclusionEngine.ShouldExclude(member)
                || ExclusionEngine.IsNamespaceExcluded(member, _exclusions)
                || InterfaceImplementationQuery.IsInterfaceImplementation(member))
            {
                continue;
            }

            familyCandidates.Add(member);
        }
    }

    private static ISymbol? FamilyRootOf(ISymbol member)
    {
        // The topmost SOURCE link: override chains can end in metadata (an SDK
        // base class), and findings never land outside source code.
        var root = member;
        for (var link = OverriddenLink(root); link is not null; link = OverriddenLink(root))
        {
            if (link.DeclaringSyntaxReferences.Length == 0)
            {
                break;
            }

            root = link;
        }

        return root;
    }

    private static List<(SyntaxTree Tree, TextSpan Span)> GetFamilySpans(
        IReadOnlyList<ISymbol> family,
        CancellationToken cancellationToken)
    {
        var spans = new List<(SyntaxTree, TextSpan)>();
        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                spans.Add((reference.SyntaxTree, reference.GetSyntax(cancellationToken).Span));
            }
        }

        return spans;
    }

    private static bool IsInsideAnySpan(Location location, List<(SyntaxTree Tree, TextSpan Span)> spans)
    {
        foreach (var (tree, span) in spans)
        {
            if (location.SourceTree == tree && span.Contains(location.SourceSpan))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DeclaresVirtualOrOverrideMember(ClassDeclarationSyntax classDeclaration)
    {
        foreach (var member in classDeclaration.Members)
        {
            if (member is MethodDeclarationSyntax or PropertyDeclarationSyntax
                && member.Modifiers.Any(static m => m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DeclaresVirtualMember(ClassDeclarationSyntax classDeclaration)
    {
        foreach (var member in classDeclaration.Members)
        {
            if (member is MethodDeclarationSyntax or PropertyDeclarationSyntax
                && member.Modifiers.Any(static m => m.IsKind(SyntaxKind.VirtualKeyword)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasAnyReferenceAsync(
        ISymbol symbol,
        CandidateClass candidate,
        Solution solution,
        SolutionUsageIndex usageIndex,
        CancellationToken cancellationToken)
    {
        // Internal types without friend assemblies can only be referenced within
        // their own project; everything else searches solution-wide (SNP0005 scoping).
        var candidateDocuments = candidate.Symbol.DeclaredAccessibility == Accessibility.Internal && !candidate.HasFriendAssemblies
            ? usageIndex.GetDocumentsUsingName(candidate.Project, symbol.Name)
            : usageIndex.GetDocumentsUsingName(symbol.Name);

        return candidateDocuments.Count > 0
            && await SymbolReferenceQuery.HasAnyReferenceAsync(symbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsOverriddenInClosure(ISymbol member, IReadOnlyList<INamedTypeSymbol> derivedClosure)
    {
        foreach (var derivedClass in derivedClosure)
        {
            foreach (var candidate in derivedClass.GetMembers(member.Name))
            {
                for (var link = candidate; link is not null; link = OverriddenLink(link))
                {
                    if (SymbolEqualityComparer.Default.Equals(link.OriginalDefinition, member.OriginalDefinition))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static ISymbol? OverriddenLink(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            _ => null,
        };
    }

    private static CertaintyTier TierFor(ISymbol symbol, bool hasFriendAssemblies)
    {
        return hasFriendAssemblies || IsOnExportedType(symbol) ? CertaintyTier.Advisory : CertaintyTier.Moderate;
    }

    private static bool IsOnExportedType(ISymbol symbol)
    {
        // For type symbols the walk starts at the type itself: a top-level public
        // type has no containing type but is still exported API surface.
        var current = symbol is INamedTypeSymbol typeSymbol ? typeSymbol : symbol.ContainingType;
        if (current is null)
        {
            return false;
        }

        for (var type = current; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is not Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CandidateClass(
        INamedTypeSymbol Symbol,
        IReadOnlyList<ISymbol> VirtualMembers,
        Project Project,
        bool HasFriendAssemblies);
}
