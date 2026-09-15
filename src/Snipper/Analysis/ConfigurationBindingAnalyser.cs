namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0007 / SNP0008 — Cross-references appsettings*.json keys against
/// options-bound POCOs (IOptions&lt;T&gt;, Configure&lt;T&gt;, Bind&lt;T&gt;, AddOptions&lt;T&gt;).
/// Flags JSON keys bound to nothing (SNP0007) and options properties never
/// populated by any discovered settings file (SNP0008).
/// Tier 4 (Advisory): configuration can flow through conventions, environment
/// variables, and IConfiguration string indexing that static analysis cannot see.
/// </summary>
public sealed class ConfigurationBindingAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    private static readonly FrozenSet<string> OptionsGenericTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "IOptions",
        "IOptionsSnapshot",
        "IOptionsMonitor",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> OptionsBindingMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Configure",
        "Bind",
        "AddOptions",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Framework-owned top-level configuration sections. Their keys bind to the
    /// framework's own options types, never to application options POCOs — flagging
    /// them is always a false positive.
    /// </summary>
    private static readonly FrozenSet<string> FrameworkSectionRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AllowedHosts",
        "ApplicationInsights",
        "ConnectionStrings",
        "Kestrel",
        "Logging",
        "NLog",
        "OpenTelemetry",
        "Serilog",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();

        progress?.Invoke("ConfigurationBindingAnalyser: discovering appsettings files");
        var settingsFiles = DiscoverSettingsFiles(solution);

        progress?.Invoke("ConfigurationBindingAnalyser: discovering options-bound types");
        var optionsTypes = await DiscoverBoundOptionsTypesAsync(solution, cancellationToken).ConfigureAwait(false);

        if (settingsFiles.Count == 0 || optionsTypes.Count == 0)
        {
            return findings;
        }

        var boundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leafProperties = new List<(string Path, IPropertySymbol Symbol)>();
        foreach (var optionsType in optionsTypes)
        {
            FlattenOptionsProperties(optionsType, prefix: string.Empty, boundPaths, leafProperties, visited: new(SymbolEqualityComparer.Default), depth: 0);
        }

        var jsonLeafKeys = new List<(string Key, string LastSegment, string FilePath)>();
        foreach (var file in settingsFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlattenSettingsFile(file, jsonLeafKeys);
        }

        // Index bound paths by their terminal segment so each JSON key only
        // suffix-compares against plausible candidates instead of every bound path
        // (O(keys × paths) → O(keys) average via frozen dictionary lookup).
        var boundPathsByLastSegment = boundPaths
            .GroupBy(static p => p[(p.LastIndexOf(':') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(static g => g.Key, static g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        // Frozen: queried once per options leaf property for SNP0008.
        var configuredKeySegments = jsonLeafKeys
            .Select(static k => k.LastSegment)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // SNP0007 — JSON keys that map to no bound options path.
        foreach (var leaf in jsonLeafKeys)
        {
            // Framework-owned sections (Logging:*, ConnectionStrings:*, ...) bind to
            // framework options types that static discovery never sees — skip them.
            var separatorIndex = leaf.Key.IndexOf(':', StringComparison.Ordinal);
            var rootSegment = separatorIndex < 0 ? leaf.Key : leaf.Key[..separatorIndex];
            if (FrameworkSectionRoots.Contains(rootSegment))
            {
                continue;
            }

            var isBound = false;
            if (boundPathsByLastSegment.TryGetValue(leaf.LastSegment, out var candidatePaths))
            {
                foreach (var path in candidatePaths)
                {
                    if (leaf.Key.Equals(path, StringComparison.OrdinalIgnoreCase)
                        || leaf.Key.EndsWith(":" + path, StringComparison.OrdinalIgnoreCase))
                    {
                        isBound = true;
                        break;
                    }
                }
            }

            if (!isBound)
            {
                var (line, column) = FindKeyLocation(leaf.FilePath, leaf.LastSegment);
                findings.Add(new SnipperFinding(
                    RuleId: "SNP0007",
                    Title: "Unbound Configuration Key",
                    Message: $"Configuration key '{leaf.Key}' does not map to any property on discovered options types.",
                    Certainty: CertaintyTier.Advisory,
                    Category: FindingCategory.UnusedConfigurationSetting,
                    FilePath: leaf.FilePath,
                    LineNumber: line,
                    CharacterOffset: column,
                    Symbol: null));
            }
        }

        // SNP0008 — Options properties never populated by any settings file.
        foreach (var leafProperty in leafProperties)
        {
            var propertyName = leafProperty.Path[(leafProperty.Path.LastIndexOf(':') + 1)..];

            if (!configuredKeySegments.Contains(propertyName))
            {
                var location = leafProperty.Symbol.Locations.FirstOrDefault(static l => l.IsInSource);
                if (location is null)
                {
                    continue;
                }

                if (ExclusionEngine.IsNamespaceExcluded(leafProperty.Symbol, _exclusions))
                {
                    continue;
                }

                var lineSpan = location.GetLineSpan();
                findings.Add(new SnipperFinding(
                    RuleId: "SNP0008",
                    Title: "Unconfigured Options Property",
                    Message: $"Options property '{leafProperty.Path}' is never set in any discovered appsettings file.",
                    Certainty: CertaintyTier.Advisory,
                    Category: FindingCategory.UnusedConfigurationSetting,
                    FilePath: lineSpan.Path ?? string.Empty,
                    LineNumber: lineSpan.StartLinePosition.Line + 1,
                    CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                    Symbol: leafProperty.Symbol));
            }
        }

        return findings;
    }

    private static List<string> DiscoverSettingsFiles(Solution solution)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            var directory = project.FilePath is not null ? Path.GetDirectoryName(project.FilePath) : null;
            if (!string.IsNullOrEmpty(directory))
            {
                directories.Add(directory);
            }
        }

        var files = new List<string>();
        foreach (var directory in directories)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "appsettings*.json", SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory unreadable — skip; other projects may still yield settings files.
            }
        }

        return files;
    }

    private static async Task<List<INamedTypeSymbol>> DiscoverBoundOptionsTypesAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        var optionsTypes = new Dictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);

        // Sequential binding: workspace compilations are built with
        // ConcurrentBuild=false; concurrent GetTypeInfo is unsupported.
        // ALL documents are scanned: discovery is evidence — more discovered options
        // types only ever suppresses false SNP0007 reports.
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

                foreach (var genericName in root.DescendantNodes().OfType<GenericNameSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAddOptionsType(genericName, semanticModel, optionsTypes, cancellationToken);
                }
            }
        }

        return [.. optionsTypes.Keys];
    }

    private static void TryAddOptionsType(
        GenericNameSyntax genericName,
        SemanticModel semanticModel,
        Dictionary<INamedTypeSymbol, byte> optionsTypes,
        CancellationToken cancellationToken)
    {
        var identifier = genericName.Identifier.Text;

        if (OptionsGenericTypeNames.Contains(identifier))
        {
            // IOptions<T> / IOptionsSnapshot<T> / IOptionsMonitor<T> — T is the bound
            // POCO. Metadata types (options bound in a referenced library, e.g. via a
            // common package) count too: they suppress false SNP0007 reports, and
            // SNP0008 filters to source-located properties downstream.
            if (semanticModel.GetTypeInfo(genericName, cancellationToken).Type is INamedTypeSymbol constructed
                && constructed.TypeArguments.Length == 1
                && constructed.TypeArguments[0] is INamedTypeSymbol optionsType)
            {
                optionsTypes.TryAdd(optionsType.OriginalDefinition, 0);
            }

            return;
        }

        if (OptionsBindingMethodNames.Contains(identifier)
            && genericName.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax })
        {
            // Configure<T>(...) / Bind<T>(...) / AddOptions<T>(...)
            foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
            {
                if (semanticModel.GetTypeInfo(typeArgument, cancellationToken).Type is INamedTypeSymbol boundType)
                {
                    optionsTypes.TryAdd(boundType.OriginalDefinition, 0);
                }
            }
        }
    }

    private static void FlattenOptionsProperties(
        INamedTypeSymbol type,
        string prefix,
        HashSet<string> boundPaths,
        List<(string Path, IPropertySymbol Symbol)> leafProperties,
        HashSet<INamedTypeSymbol> visited,
        int depth)
    {
        const int MaxDepth = 6;
        if (depth >= MaxDepth || !visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.GetMethod is null)
            {
                continue;
            }

            var path = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";

            if (property.Type is INamedTypeSymbol propertyType
                && propertyType.SpecialType == SpecialType.None
                && propertyType.TypeKind is TypeKind.Class
                && !propertyType.AllInterfaces.Any(static i => i.Name is "IEnumerable"))
            {
                FlattenOptionsProperties(propertyType, path, boundPaths, leafProperties, visited, depth + 1);
            }
            else
            {
                boundPaths.Add(path);

                // SNP0008 reports at the property declaration — only meaningful for
                // source-located properties; metadata options types contribute bound
                // paths for SNP0007 matching only.
                if (property.Locations.Any(static l => l.IsInSource))
                {
                    leafProperties.Add((path, property));
                }
            }
        }
    }

    private static void FlattenSettingsFile(string filePath, List<(string Key, string LastSegment, string FilePath)> leafKeys)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Malformed or unreadable settings file — not Snipper's concern; skip.
            return;
        }

        using (document)
        {
            FlattenJsonElement(document.RootElement, prefix: string.Empty, filePath, leafKeys);
        }
    }

    private static void FlattenJsonElement(
        JsonElement element,
        string prefix,
        string filePath,
        List<(string Key, string LastSegment, string FilePath)> leafKeys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
                FlattenJsonElement(property.Value, key, filePath, leafKeys);
            }

            return;
        }

        // Arrays and scalar values terminate a key path.
        if (prefix.Length > 0)
        {
            leafKeys.Add((prefix, prefix[(prefix.LastIndexOf(':') + 1)..], filePath));
        }
    }

    private static (int Line, int Column) FindKeyLocation(string filePath, string lastSegment)
    {
        try
        {
            var needle = $"\"{lastSegment}\"";
            var lineNumber = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                lineNumber++;
                var index = line.IndexOf(needle, StringComparison.Ordinal);
                if (index >= 0)
                {
                    return (lineNumber, index + 1);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to default location.
        }

        return (1, 1);
    }
}
