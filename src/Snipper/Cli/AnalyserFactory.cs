namespace Snipper.Cli;

using Snipper.Analysis;

/// <summary>
/// Builds the analyser set for a run.
/// <para>
/// Extracted from <see cref="CliRunner"/> because "which analysers run" is a decision
/// worth naming and testing on its own, rather than a list buried in the middle of a
/// 1,000-line method.
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
    public static List<IWorkspaceAnalyser> Build(
        CommandLineOptions options,
        string targetPath,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var built = new List<IWorkspaceAnalyser>
        {
            new UnreachableCodeAnalyser(),
            new UnusedLocalVariableAnalyser(),
            new UnusedPrivateMemberAnalyser(),
            new UnusedParameterAnalyser(),
            new UnusedNonPrivateMemberAnalyser(),
            new UnreferencedPackageAnalyser(),
            new ObsoleteMemberAnalyser(),
            new OrphanProjectAnalyser(),
            new RedundantTransitivePackageAnalyser(),
            new FrameworkInboxPackageAnalyser(),
            new UnusedUsingDirectiveAnalyser(),
            new CommentedCodeAnalyser(),
            new WriteOnlyFieldAnalyser(),
            new RedundancyAnalyser(),
            new HierarchyDeadCodeAnalyser(),
            new TighteningAnalyser(),
            new EventNeverInvokedAnalyser(),
            new CyclomaticComplexityAnalyser(options.MaxCyclomaticComplexity),

            // Both complexity rules ship by default and are suppressed together. The
            // metrics disagree by construction, so a consumer who only wants one turns
            // the other off by ID in snipper.json — the report shows both scores, which
            // is the point: the disagreement is the signal.
            new CognitiveComplexityAnalyser(options.MaxCognitiveComplexity),
        };

        if (options.IncludeConfigAnalysis)
        {
            built.Add(new ConfigurationBindingAnalyser());
        }

        if (options.IncludeDuplicateDetection)
        {
            // The repository root for drift detection is the directory git reports, not the
            // analysed project, so paths resolve identically to the history index.
            //
            // baseDirectory is a different root on purpose: it is the one
            // BaselineService shortens finding paths against, and SNP0031 names its
            // other copies in the message it hashes. Passing the same root to both is
            // what stops the message reintroducing machine dependence through the back
            // door.
            built.Add(new DuplicateFragmentAnalyser(
                options.IncludeCloneDrift ? new CloneDriftDetector(FindRepositoryRoot(targetPath)) : null,
                baseDirectory));
        }

        return built;
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
