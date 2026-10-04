namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using NuGet.Versioning;

/// <summary>
/// Shared, tolerant reader for the MSBuild project-file facts the artefact-level
/// analysers need. Pure XML parsing — no MSBuild evaluation, so imported props/targets
/// and glob expansions are invisible by design. Parse failures (IO/XML/access) return
/// null: callers skip the project rather than failing the run.
/// </summary>
internal static class ProjectFileReader
{
    /// <summary>
    /// Per-path memo, invalidated by last-write time.
    ///
    /// Five independent call sites read the same csproj files - the project graph, the
    /// unreferenced-package and redundant-transitive rules, the framework-inbox rule and
    /// the orphan-project rule - and each was building its own
    /// <see cref="XDocument"/> DOM with <see cref="LoadOptions.SetLineInfo"/>, the
    /// expensive <see cref="XmlReader"/> mode, then throwing it away. On a solution whose
    /// analysers run more than once (the lifted suppression pass) that multiplied again.
    ///
    /// A stat call is orders of magnitude cheaper than an XML parse, so the last-write
    /// check costs almost nothing and keeps the memo honest if a csproj changes under a
    /// long-lived host. Parse failures are cached too (as a null <see cref="Info"/>),
    /// because re-parsing a broken file on every call is the exact waste this removes;
    /// touching the file changes its timestamp and forces a re-read.
    ///
    /// Bounded by the number of distinct project files read, which for a CLI process is
    /// the project count. Pinned by ProjectFileReaderShould - mutating the invalidation
    /// check so the cache never expires left every other test green.
    /// </summary>
    private sealed record CacheEntry(DateTime LastWriteUtc, ProjectFileInfo? Info);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ProjectFileInfo? Read(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);

        var lastWriteUtc = default(DateTime);
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(projectFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable metadata: fall through to the parse, which reports the real failure.
        }

        if (Cache.TryGetValue(projectFilePath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached.Info;
        }

        var info = ReadCore(projectFilePath);
        Cache[projectFilePath] = new CacheEntry(lastWriteUtc, info);
        return info;
    }

    private static ProjectFileInfo? ReadCore(string projectFilePath)
    {

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var root = document.Root;
        var sdkName = root?.Attribute("Sdk")?.Value
            ?? document.Descendants("Sdk").FirstOrDefault()?.Attribute("Name")?.Value;

        var projectDirectory = Path.GetDirectoryName(projectFilePath) ?? string.Empty;
        var outputType = FirstPropertyValue(document, "OutputType");
        var assemblyName = FirstPropertyValue(document, "AssemblyName")
            ?? Path.GetFileNameWithoutExtension(projectFilePath);

        var targetFrameworks = ParseTargetFrameworks(document);
        var frameworkReferenceIds = document.Descendants("FrameworkReference")
            .Select(static e => e.Attribute("Include")?.Value?.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToImmutableArray();

        var packages = ParsePackageReferences(document);
        var projects = ParseProjectReferences(document, projectDirectory);
        var isTestProject = string.Equals(FirstPropertyValue(document, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || HasTestSdkReference(packages);

        return new ProjectFileInfo(
            FilePath: projectFilePath,
            AssemblyName: assemblyName,
            SdkName: sdkName,
            OutputType: outputType,
            TargetFrameworks: targetFrameworks,
            FrameworkReferenceIds: frameworkReferenceIds,
            PackageReferences: packages,
            ProjectReferences: projects,
            IsTestProject: isTestProject);
    }

    private static bool HasTestSdkReference(ImmutableArray<PackageReferenceItem> packages)
    {
        foreach (var package in packages)
        {
            if (package.Id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || package.Id.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FirstPropertyValue(XDocument document, string propertyName)
    {
        var value = document.Descendants(propertyName).FirstOrDefault()?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ImmutableArray<string> ParseTargetFrameworks(XDocument document)
    {
        var builder = ImmutableArray.CreateBuilder<string>();

        var single = FirstPropertyValue(document, "TargetFramework");
        if (single is not null)
        {
            builder.Add(single);
        }

        var multiple = FirstPropertyValue(document, "TargetFrameworks");
        if (multiple is not null)
        {
            foreach (var tfm in multiple.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                builder.Add(tfm);
            }
        }

        return builder.ToImmutableArray();
    }

    private static ImmutableArray<PackageReferenceItem> ParsePackageReferences(XDocument document)
    {
        var builder = ImmutableArray.CreateBuilder<PackageReferenceItem>();

        foreach (var element in document.Descendants("PackageReference"))
        {
            var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rawVersion = element.Attribute("VersionOverride")?.Value
                ?? element.Attribute("Version")?.Value
                ?? element.Element("Version")?.Value;

            var excludeAssets = element.Attribute("ExcludeAssets")?.Value
                ?? element.Element("ExcludeAssets")?.Value
                ?? string.Empty;

            var privateAssets = element.Attribute("PrivateAssets")?.Value
                ?? element.Element("PrivateAssets")?.Value
                ?? string.Empty;

            builder.Add(new PackageReferenceItem(
                Id: id.Trim(),
                DeclaredMinVersion: ParseMinimumVersion(rawVersion),
                CompileAssetsExcluded: excludeAssets.Contains("compile", StringComparison.OrdinalIgnoreCase),
                PrivateAssetsAll: privateAssets.Trim().Equals("all", StringComparison.OrdinalIgnoreCase),
                LineNumber: (element as IXmlLineInfo)?.LineNumber ?? 1));
        }

        return builder.ToImmutableArray();
    }

    private static NuGetVersion? ParseMinimumVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        // VersionRange handles plain versions ("8.0.5" → min 8.0.5) and interval
        // notation ("[8.0.5, )") alike; MinVersion is the conservative bound.
        return VersionRange.TryParse(rawVersion.Trim(), out var range) ? range.MinVersion : null;
    }

    private static ImmutableArray<ProjectReferenceItem> ParseProjectReferences(XDocument document, string projectDirectory)
    {
        var builder = ImmutableArray.CreateBuilder<ProjectReferenceItem>();

        foreach (var element in document.Descendants("ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, include.Trim()));
            var metadata = element.Attribute("ReferenceOutputAssembly")?.Value
                ?? element.Element("ReferenceOutputAssembly")?.Value;
            var outputItemType = element.Attribute("OutputItemType")?.Value
                ?? element.Element("OutputItemType")?.Value;
            var privateAssets = element.Attribute("PrivateAssets")?.Value
                ?? element.Element("PrivateAssets")?.Value
                ?? string.Empty;

            builder.Add(new ProjectReferenceItem(
                FullPath: fullPath,
                ReferenceOutputAssemblyDisabled: string.Equals(metadata, "false", StringComparison.OrdinalIgnoreCase),
                // MSBuild metadata value — defined by the ecosystem, must stay "Analyzer".
                IsAnalyser: string.Equals(outputItemType, "Analyzer", StringComparison.OrdinalIgnoreCase),
                PrivateAssetsAll: privateAssets.Trim().Equals("all", StringComparison.OrdinalIgnoreCase),
                LineNumber: (element as IXmlLineInfo)?.LineNumber ?? 1));
        }

        return builder.ToImmutableArray();
    }
}

internal sealed record ProjectFileInfo(
    string FilePath,
    string AssemblyName,
    string? SdkName,
    string? OutputType,
    ImmutableArray<string> TargetFrameworks,
    ImmutableArray<string> FrameworkReferenceIds,
    ImmutableArray<PackageReferenceItem> PackageReferences,
    ImmutableArray<ProjectReferenceItem> ProjectReferences,
    bool IsTestProject);

internal sealed record PackageReferenceItem(
    string Id,
    NuGetVersion? DeclaredMinVersion,
    bool CompileAssetsExcluded,
    bool PrivateAssetsAll,
    int LineNumber);

internal sealed record ProjectReferenceItem(
    string FullPath,
    bool ReferenceOutputAssemblyDisabled,
    bool IsAnalyser,
    bool PrivateAssetsAll,
    int LineNumber);
