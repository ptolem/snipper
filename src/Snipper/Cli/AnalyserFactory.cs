namespace Snipper.Cli;

using Snipper.Analysis;

/// <summary>
/// Builds the analyser set for a run.
/// <para>
/// Extracted from <see cref="CliRunner"/> because "which analysers run" is a decision
/// worth naming and testing on its own, rather than a list buried in the middle of a
/// 1,000-line method. It is also the one place that knows a construction detail no caller
/// should care about: that four analysers ignore namespace exclusions entirely.
/// </para>
/// <para>
/// The opt-in analysers (SNP0007/8 configuration binding, SNP0031/32 duplication) are
/// gated here rather than at the call site, because the gate and the construction must
/// not be able to drift apart.
/// </para>
/// </summary>
internal static class AnalyserFactory
{
    /// <summary>
    /// Builds every enabled analyser. Configuration binding is opt-in because indirect
    /// binding through referenced libraries and framework conventions makes its
    /// false-positive rate too high for default runs; duplication is opt-in because
    /// token-normalized clone detection flags structurally uniform code, which is
    /// duplication by design in most codebases.
    /// </summary>
    public static List<IWorkspaceAnalyser> Build(CommandLineOptions options, AnalysisExclusions exclusions, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exclusions);

        var built = new List<IWorkspaceAnalyser>
        {
            new UnreachableCodeAnalyser(exclusions),
            new UnusedLocalVariableAnalyser(exclusions),
            new UnusedPrivateMemberAnalyser(exclusions),
            new UnusedParameterAnalyser(exclusions),
            new UnusedNonPrivateMemberAnalyser(exclusions),
            new UnreferencedPackageAnalyser(),
            new ObsoleteMemberAnalyser(exclusions),
            new OrphanProjectAnalyser(),
            new RedundantTransitivePackageAnalyser(),
            new FrameworkInboxPackageAnalyser(),
            new UnusedUsingDirectiveAnalyser(exclusions),
            new CommentedCodeAnalyser(exclusions),
            new WriteOnlyFieldAnalyser(exclusions),
            new RedundancyAnalyser(exclusions),
            new HierarchyDeadCodeAnalyser(exclusions),
new TighteningAnalyser(exclusions),
              new EventNeverInvokedAnalyser(exclusions),
              new CyclomaticComplexityAnalyser(exclusions, options.MaxCyclomaticComplexity),
          };

        if (options.IncludeConfigAnalysis)
        {
            built.Add(new ConfigurationBindingAnalyser(exclusions));
        }

        if (options.IncludeDuplicateDetection)
        {
            // The repository root for drift detection is the directory git reports, not the
            // analysed project, so paths resolve identically to the history index.
            built.Add(new DuplicateFragmentAnalyser(
                exclusions,
                options.IncludeCloneDrift ? new CloneDriftDetector(FindRepositoryRoot(targetPath)) : null));
        }

        return built;
    }

    /// <summary>
    /// Splits a set into the analysers whose output can change when namespace exclusions
    /// are lifted, and those whose output cannot.
    /// <para>
    /// Four analysers — the package and project graph rules — take no
    /// <see cref="AnalysisExclusions"/> at all. Nothing they emit is namespace-filtered, so
    /// re-running them with exclusions lifted would return exactly what the normal pass
    /// already returned. Callers that need the suppression-independent finding set can
    /// therefore re-run only the aware half and reuse the agnostic half, which is a
    /// measured saving rather than an assumption.
    /// </para>
    /// <para>
    /// The partition is by construction, not by name matching: it is derived from the
    /// constructor arguments used in <see cref="Build"/>. Adding a new analyser makes it
    /// default to exclusion-aware, which is the safe direction — it would be re-run rather
    /// than wrongly assumed unaffected.
    /// </para>
    /// </summary>
    public static ExclusionPartition PartitionByExclusionSensitivity(
        IReadOnlyList<IWorkspaceAnalyser> analysers)
    {
        ArgumentNullException.ThrowIfNull(analysers);

        var aware = new List<IWorkspaceAnalyser>();
        var agnostic = new List<IWorkspaceAnalyser>();

        foreach (var analyser in analysers)
        {
            if (IsExclusionAgnostic(analyser))
            {
                agnostic.Add(analyser);
            }
            else
            {
                aware.Add(analyser);
            }
        }

        return new ExclusionPartition(aware, agnostic);
    }

    /// <summary>
    /// Whether an analyser ignores namespace exclusions. These four are the package and
    /// project graph rules: they reason over references and package graphs, which have no
    /// containing namespace to filter on.
    /// </summary>
    private static bool IsExclusionAgnostic(IWorkspaceAnalyser analyser)
    {
        ArgumentNullException.ThrowIfNull(analyser);

        return analyser is UnreferencedPackageAnalyser
            or OrphanProjectAnalyser
            or RedundantTransitivePackageAnalyser
            or FrameworkInboxPackageAnalyser;
    }

    /// <summary>
    /// Walks up from the analysed target to the directory git considers the repository root,
    /// so that drift detection resolves member paths exactly as the history index reports
    /// them. Falls back to the target's own directory, which makes clone drift a no-op
    /// rather than an error outside a checkout.
    /// </summary>
    private static string FindRepositoryRoot(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var start = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(start))
        {
            return Directory.GetCurrentDirectory();
        }

        var output = GitMetadata.TryResolveRepositoryRoot(start);
        return output ?? start;
    }
}
/// <summary>
/// The result of splitting analysers by whether namespace exclusions can affect their
/// output. <see cref="Agnostic"/> analysers produce identical findings whether or not
/// exclusions are in force, so a lifted pass can reuse their results verbatim.
/// </summary>
/// <param name="Aware">Analysers whose output changes when exclusions are lifted.</param>
/// <param name="Agnostic">Analysers that ignore namespace exclusions entirely.</param>
internal sealed record ExclusionPartition(
    IReadOnlyList<IWorkspaceAnalyser> Aware,
    IReadOnlyList<IWorkspaceAnalyser> Agnostic);