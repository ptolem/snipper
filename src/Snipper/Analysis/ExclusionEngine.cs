namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class ExclusionEngine
{
    // Hoisted out of IsGeneratedDocument, which runs once per document from fifteen analysers.
    // Interpolated inline they were rebuilt on every one of those calls; as literals the
    // framework builds them once.
    private static readonly string ObjSegment =
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

    private static readonly string ObjSegmentAltForm =
        $"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}";

    // Frozen: built once at type initialization, queried for the entire process lifetime.
    private static readonly FrozenSet<string> ExcludedAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Benchmark", "BenchmarkAttribute",
        "LoggerMessage", "LoggerMessageAttribute",
        "GeneratedRegex", "GeneratedRegexAttribute",
        "Obsolete", "ObsoleteAttribute",
        "UsedImplicitly", "UsedImplicitlyAttribute",
        "MeansImplicitUse", "MeansImplicitUseAttribute",
        "PublicAPI", "PublicAPIAttribute",
        // CLR-invoked at module load; zero source references by design.
        "ModuleInitializer", "ModuleInitializerAttribute",
        // xUnit collection-definition markers are resolved by the framework through
        // the collection-name string, never by symbol reference.
        "CollectionDefinition", "CollectionDefinitionAttribute"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ExcludedInterfaceMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "IRequestHandler",
        "INotificationHandler",
        "IConsumer",
        "IJob",
        "IHostedService",
        "IEndpointFilter",
        // xUnit fixture lifecycle — invoked reflectively by the test runner.
        "IAsyncLifetime"
    }.ToFrozenSet(StringComparer.Ordinal);

    // Methods the runtime invokes with no source references. A type containing one is a
    // framework entry point; tracked separately from ExcludedAttributeNames because the
    // type-level scan must not inherit member-level exclusions such as Obsolete.
    private static readonly FrozenSet<string> RuntimeInvokedMethodAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ModuleInitializer", "ModuleInitializerAttribute"
    }.ToFrozenSet(StringComparer.Ordinal);

    // Test-framework method attributes (xUnit/NUnit/MSTest). A type containing any
    // test-attributed method is a test class: it and all its members are excluded.
    private static readonly FrozenSet<string> TestMethodAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute",
        "TestCase", "TestCaseAttribute",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool ShouldExclude(ISymbol symbol)
    {
        return ShouldExclude(symbol, ignoreObsoleteAttribute: false);
    }

    /// <summary>
    /// <see cref="ShouldExclude(ISymbol)"/> variant for rules that deliberately target
    /// <c>[Obsolete]</c> symbols (SNP0018): the Obsolete attribute itself never triggers
    /// exclusion; every other guard (constructors, framework entry points, remaining
    /// excluded attributes such as UsedImplicitly) still applies.
    /// </summary>
    public static bool ShouldExcludeIgnoringObsolete(ISymbol symbol)
    {
        return ShouldExclude(symbol, ignoreObsoleteAttribute: true);
    }

    private static bool ShouldExclude(ISymbol symbol, bool ignoreObsoleteAttribute)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (symbol is IMethodSymbol method && (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor))
        {
            return true;
        }

        if (HasAnyAttribute(symbol, ExcludedAttributeNames, ignoreObsoleteAttribute))
        {
            return true;
        }

        if (symbol is INamedTypeSymbol namedType && IsFrameworkEntryPointType(namedType))
        {
            return true;
        }

        var containingType = symbol.ContainingType;
        if (containingType is not null && IsFrameworkEntryPointType(containingType))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the symbol's containing namespace (or any ancestor namespace) is
    /// excluded. Findings are suppressed; usage evidence from excluded code is
    /// gathered elsewhere and is unaffected.
    /// </summary>
    public static bool IsNamespaceExcluded(ISymbol symbol, AnalysisExclusions exclusions)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(exclusions);

        if (exclusions.Namespaces.Count == 0)
        {
            return false;
        }

        if (symbol.ContainingNamespace is not { IsGlobalNamespace: false })
        {
            // Top-level-statements Program.cs and file-scope declarations bind to the
            // global namespace, which no real namespace name can match. Reachable only
            // through the <global> sentinel - previously this returned false outright,
            // so such files were silently unexcludable.
            return exclusions.IsGlobalNamespaceExcluded;
        }

        return exclusions.Covers(symbol.ContainingNamespace.ToDisplayString());
    }

    /// <summary>
    /// Namespace exclusion for findings that carry no symbol (unreachable
    /// statements, unread locals, using directives): resolved through the enclosing type
    /// declaration, then the enclosing namespace declaration, then — for file-scope code
    /// that declares no namespace at all — the <see cref="AnalysisExclusions.GlobalNamespaceMarker"/>
    /// sentinel.
    /// </summary>
    public static bool IsNamespaceExcluded(
        SyntaxNode node,
        SemanticModel semanticModel,
        AnalysisExclusions exclusions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(exclusions);

        if (exclusions.Namespaces.Count == 0)
        {
            return false;
        }

        var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDeclaration is not null)
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken);
            if (typeSymbol is not null)
            {
                return IsNamespaceExcluded(typeSymbol, exclusions);
            }
        }

        var namespaceName = ResolveFileScopeNamespace(node);
        return namespaceName is null
            ? exclusions.IsGlobalNamespaceExcluded
            : exclusions.Covers(namespaceName);
    }

    /// <summary>
    /// The namespace a file-scope node belongs to, or null when it declares none.
    /// <para>
    /// The enclosing <see cref="BaseNamespaceDeclarationSyntax"/> is tried first, which is
    /// exact for anything inside a block-scoped namespace. It cannot serve file-scope
    /// code, though: a file-scoped <c>namespace N;</c> and both forms of file-level
    /// <c>using</c> are *siblings* of the using directive, not ancestors, so an ancestor
    /// walk returns null for <c>using System.Text;</c> in a file that does declare
    /// <c>namespace N;</c>. Those files therefore fall back to the unit's own namespace
    /// declaration. Classifying them as global instead would make <c>&lt;global&gt;</c>
    /// suppress usings in every namespaced file.
    /// </para>
    /// </summary>
    private static string? ResolveFileScopeNamespace(SyntaxNode node)
    {
        var ancestor = node.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
        if (ancestor is not null)
        {
            return ancestor.Name.ToString();
        }

        var unit = node.AncestorsAndSelf().OfType<CompilationUnitSyntax>().FirstOrDefault();
        foreach (var member in unit?.Members ?? default)
        {
            if (member is BaseNamespaceDeclarationSyntax declaration)
            {
                return declaration.Name.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Per-symbol memo for <see cref="IsFrameworkEntryPointType"/>, keyed on the symbol
    /// instance (Roslyn symbols are immutable, so the answer is a pure function of it) and
    /// held weakly so it cannot outlive the compilation that produced it.
    ///
    /// Without this the check is quadratic in the members of the containing type: it walks
    /// every method and calls GetAttributes() on each, and it runs once per candidate
    /// symbol from six analysers (SNP0019/0022/0023/0024/0027 and the tightening rules).
    /// A 50-method type with 40 candidates paid ~2,000 attribute bindings where 50 suffice.
    /// GetAttributes() is not cached by Roslyn for source symbols - it re-binds the
    /// attribute list and allocates a fresh ImmutableArray each call.
    ///
    /// Concurrent computes of the same type are possible and benign: the value is a pure
    /// function, so every racer stores the same answer. Same idiom as InvocationSpeculation's
    /// ConditionalAccessPresence.
    /// </summary>
    private static readonly ConditionalWeakTable<INamedTypeSymbol, StrongBox<bool>> FrameworkEntryPointTypes = new();

    public static bool IsFrameworkEntryPointType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return FrameworkEntryPointTypes.GetValue(
            type,
            static t => new StrongBox<bool>(ComputeIsFrameworkEntryPointType(t))).Value;
    }

    private static bool ComputeIsFrameworkEntryPointType(INamedTypeSymbol type)
    {
        // ASP.NET Core MVC / Web API Controllers
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name is "ControllerBase" or "Controller")
            {
                return true;
            }
        }

        // Check [ApiController]
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "ApiController" or "ApiControllerAttribute")
            {
                return true;
            }
        }

        // Check messaging & background contracts
        foreach (var iface in type.AllInterfaces)
        {
            if (ExcludedInterfaceMarkers.Contains(iface.Name))
            {
                return true;
            }
        }

        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
            {
                continue;
            }

            // Process entry point: explicit Main or the compiler-synthesized top-level-statements
            // method. The CLR roots all usage here, so the containing type is never dead code.
            if (method.Name is "Main" or "<Main>$")
            {
                return true;
            }

            // Test classes: any method carrying a test-framework attribute marks the
            // whole type (and therefore all of its members) as an entry point.
            if (HasAnyAttribute(method, TestMethodAttributeNames))
            {
                return true;
            }

            // Runtime-invoked methods (module initializers) mark their containing type.
            if (HasAnyAttribute(method, RuntimeInvokedMethodAttributeNames))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsGeneratedDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        // MSBuild intermediate output (protobuf/gRPC codegen, compiled codegen, etc.).
        //
        // Tested as spans against both separator spellings rather than by normalising the path
        // first. The old code copied the whole path - a 120-character allocation - on every call
        // just to turn one separator into another, then built a fresh "\obj\" literal to search
        // for. On Unix the two separators are the same character and the second test is skipped;
        // on Windows there are genuinely two spellings, and checking for both as spans answers it
        // without the copy. Semantics are unchanged: OrdinalIgnoreCase, same pattern.
        var path = filePath.AsSpan();
        if (path.Contains(ObjSegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar
            && path.Contains(ObjSegmentAltForm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(filePath);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the file does not live under any analysis root — e.g. sources
    /// injected from NuGet packages (Microsoft.NET.Test.Sdk.Program.cs and similar
    /// build-transitive content). Snipper only judges code the repo owns.
    /// </summary>
    public static bool IsExternalDocument(string? filePath, IReadOnlyList<string> analysisRootDirectories)
    {
        if (string.IsNullOrEmpty(filePath) || analysisRootDirectories.Count == 0)
        {
            return false;
        }

        foreach (var directory in analysisRootDirectories)
        {
            if (filePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Detects the standard "&lt;auto-generated&gt;" file header (protobuf, T4,
    /// legacy designers) regardless of file name.
    /// </summary>
    public static bool HasAutoGeneratedHeader(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var trivia in root.GetFirstToken().LeadingTrivia)
        {
            if ((trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                && trivia.ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Solution directory when a solution file was opened; otherwise every project
    /// directory (single-csproj runs). Documents outside all roots are external.
    /// </summary>
    public static IReadOnlyList<string> GetAnalysisRootDirectories(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        if (solution.FilePath is { Length: > 0 } solutionPath
            && Path.GetDirectoryName(solutionPath) is { Length: > 0 } solutionDirectory)
        {
            return [solutionDirectory + Path.DirectorySeparatorChar];
        }

        var directories = new List<string>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is { Length: > 0 } projectPath
                && Path.GetDirectoryName(projectPath) is { Length: > 0 } projectDirectory)
            {
                directories.Add(projectDirectory + Path.DirectorySeparatorChar);
            }
        }

        return directories;
    }

    public static bool ShouldSkipDocument(string? filePath, SyntaxNode root, IReadOnlyList<string> analysisRootDirectories)
    {
        ArgumentNullException.ThrowIfNull(root);

        return IsGeneratedDocument(filePath)
            || IsExternalDocument(filePath, analysisRootDirectories)
            || HasAutoGeneratedHeader(root);
    }

    private static bool HasAnyAttribute(ISymbol symbol, FrozenSet<string> attributeNames, bool ignoreObsoleteAttribute = false)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is not null && attributeNames.Contains(attr.AttributeClass.Name))
            {
                if (ignoreObsoleteAttribute && attr.AttributeClass.Name is "Obsolete" or "ObsoleteAttribute")
                {
                    continue;
                }

                return true;
            }
        }
        return false;
    }
}
