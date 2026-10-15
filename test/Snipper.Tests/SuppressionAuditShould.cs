namespace Snipper.Tests;

using System.Collections.Frozen;
using FluentAssertions;
using Snipper.Analysis;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

/// <summary>
/// Wave 4 / 4A. The attribution rules are the whole point of the audit, so they are
/// pinned here as pure functions — no workspace, no Roslyn. The end-to-end shadow-pass
/// behaviour is covered by CliRunnerShould.
/// </summary>
public sealed class SuppressionAuditShould
{
    private const string GeneratedGlob = "**/Generated/**";
    private const string VendoredGlob = "**/Vendor/**";

    [Fact]
    public void Report_The_Path_Glob_That_Matched_And_The_Rules_It_Hid()
    {
        var findings = new[]
        {
            Finding("SNP0006", "C:/repo/src/Generated/A.cs"),
            Finding("SNP0006", "C:/repo/src/Generated/B.cs"),
            Finding("SNP0024", "C:/repo/src/Generated/C.cs"),
            Finding("SNP0001", "C:/repo/src/Real/D.cs"),
        };

        var audit = SuppressionAuditBuilder.Build(findings, Config(paths: [GeneratedGlob]), null, null, null, false, null, null);

        var entry = audit.PathGlobs.Should().ContainSingle().Subject;
        entry.Selector.Should().Be(GeneratedGlob);
        entry.SuppressedCount.Should().Be(3);
        entry.RuleIds.Should().Equal("SNP0006", "SNP0024");
        audit.Totals.FindingsDropped.Should().Be(3);
        audit.Totals.FindingsAfterSuppression.Should().Be(1);
    }

    [Fact]
    public void Compute_Hidden_Debt_Percentage_Against_Total_Analysed_Debt()
    {
        var findings = new[]
        {
            Finding("SNP0006", "C:/repo/src/Generated/A.cs"),
            Finding("SNP0024", "C:/repo/src/Generated/B.cs"),
            Finding("SNP0001", "C:/repo/src/Real/C.cs"),
        };

        var audit = SuppressionAuditBuilder.Build(findings, Config(paths: [GeneratedGlob]), null, null, null, false, null, null);

        audit.Totals.HiddenDebtPercent.Should().Be(67);
    }

    [Fact]
    public void Report_Zero_Hidden_Debt_For_An_Unfiltered_Run()
    {
        var findings = new[] { Finding("SNP0001", "C:/repo/src/A.cs") };

        var audit = SuppressionAuditBuilder.Build(findings, SnipperConfig.Empty, null, null, null, false, null, null);

        audit.Totals.HiddenDebtPercent.Should().Be(0);
        audit.Totals.FindingsDropped.Should().Be(0);
        audit.Totals.FindingsDowngraded.Should().Be(0);
        audit.Obsolete.Should().BeEmpty();
    }

    [Fact]
    public void Count_A_Severity_Override_As_Downgraded_Rather_Than_Hidden()
    {
        var findings = new[]
        {
            Finding("SNP0018", "C:/repo/src/A.cs", CertaintyTier.High),
            Finding("SNP0018", "C:/repo/src/B.cs", CertaintyTier.High),
        };

        var config = Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory });
        var audit = SuppressionAuditBuilder.Build(findings, config, null, null, null, false, null, null);

        audit.Totals.FindingsDowngraded.Should().Be(2);
        audit.Totals.FindingsDropped.Should().Be(0);

        var entry = audit.SeverityOverrides.Should().ContainSingle().Subject;
        entry.Selector.Should().Be("SNP0018");
        entry.DowngradedCount.Should().Be(2);
        entry.Detail.Should().Contain("Advisory").And.Contain("Highx2");
    }

    [Fact]
    public void Credit_A_Downgrade_That_Pushes_A_Finding_Past_The_Tier_Floor_To_The_Override_Not_The_Floor()
    {
        // CertaintyTier runs Guaranteed=1..Advisory=4 and the floor keeps `Certainty <=
        // floor`, so a downgrade moves a finding *away* from the floor. High(2) clears a
        // Moderate(3) floor, but Advisory(4) does not — so the override is what hides it.
        // Attributing the drop to the floor would point the user at the wrong setting.
        var findings = new[] { Finding("SNP0018", "C:/repo/src/A.cs", CertaintyTier.High) };
        var config = Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory });

        var audit = SuppressionAuditBuilder.Build(findings, config, CertaintyTier.Moderate, null, null, false, null, null);

        audit.Totals.FindingsDropped.Should().Be(1);

        // Credited to the override...
        audit.SeverityOverrides.Should().ContainSingle().Subject.SuppressedCount.Should().Be(1);

        // ...and explicitly not to the tier floor.
        audit.CertaintyFilter.Should().BeNull();
    }

    [Fact]
    public void Credit_The_Tier_Floor_When_No_Override_Pushed_The_Finding_Out()
    {
        var findings = new[] { Finding("SNP0025", "C:/repo/src/A.cs", CertaintyTier.Advisory) };
        var config = Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0001"] = CertaintyTier.Advisory });

        var audit = SuppressionAuditBuilder.Build(findings, config, CertaintyTier.Guaranteed, null, null, false, null, null);

        audit.CertaintyFilter.Should().NotBeNull();
        audit.CertaintyFilter!.SuppressedCount.Should().Be(1);
        audit.SeverityOverrides.Should().ContainSingle().Subject.SuppressedCount.Should().Be(0);
    }

    [Fact]
    public void Count_The_Tier_Floor_As_Its_Own_Channel_When_It_Drops_Findings()
    {
        var findings = new[] { Finding("SNP0025", "C:/repo/src/A.cs", CertaintyTier.Advisory) };

        var audit = SuppressionAuditBuilder.Build(findings, SnipperConfig.Empty, CertaintyTier.Guaranteed, null, null, false, null, null);

        audit.CertaintyFilter.Should().NotBeNull();
        audit.CertaintyFilter!.SuppressedCount.Should().Be(1);
        audit.Totals.FindingsDropped.Should().Be(1);
    }

    [Fact]
    public void Attribute_A_Disabled_Rule_Finding_From_The_Shadow_Pass_When_The_Analyser_Was_Removed()
    {
        // A sole-rule analyser is removed pre-analysis, so its findings are absent from
        // analysedFindings entirely and can only come from the shadow pass.
        var shadow = new[] { Finding("SNP0024", "C:/repo/src/A.cs") };

        var audit = SuppressionAuditBuilder.Build(
            [], Config(disabled: ["SNP0024"]), null, shadow, [], true, null, null);

        var entry = audit.DisabledRules.Should().ContainSingle().Subject;
        entry.SuppressedCount.Should().Be(1);
        audit.Totals.FindingsHiddenByShadow.Should().Be(1);
        audit.Totals.FindingsAnalysed.Should().Be(0);
        audit.Totals.HiddenDebtPercent.Should().Be(100);
    }

    [Fact]
    public void Credit_A_Finding_Suppressed_By_Both_A_Disabled_Rule_And_A_Namespace_To_Exactly_One_Channel()
    {
        // A disabled analyser never runs in the normal pass, so namespace exclusion never
        // sees its findings: the disabled-rule pass hands them in unfiltered, and a finding
        // hidden by both is counted once, against the rule. This pins that the builder does
        // not re-introduce the overlap.
        var disabledShadow = new[] { Finding("SNP0024", "C:/repo/src/A.cs") };
        var namespaceExcluded = new[] { new NamespaceExcludedFinding(Finding("SNP0030", "C:/repo/src/B.cs"), "Acme.Generated") };

        var audit = SuppressionAuditBuilder.Build(
            [],
            Config(disabled: ["SNP0024"], namespaces: ["Acme.Generated"]),
            null,
            disabledShadow,
            namespaceExcluded,
            true,
            null,
            null);

        audit.DisabledRules.Should().ContainSingle().Subject.SuppressedCount.Should().Be(1);
        audit.NamespaceExclusions.Should().ContainSingle().Subject.SuppressedCount.Should().Be(1);
        audit.Totals.FindingsHiddenByShadow.Should().Be(2);
        audit.Totals.FindingsDropped.Should().Be(0);
        audit.Totals.HiddenDebtPercent.Should().Be(100);
    }

    [Fact]
    public void Report_One_Row_Per_Namespace_Exclusion_With_Its_Own_Count()
    {
        NamespaceExcludedFinding[] namespaceExcluded =
        [
            new(Finding("SNP0001", "C:/repo/src/A.cs"), "Acme.Vendor"),
            new(Finding("SNP0006", "C:/repo/src/B.cs"), "Acme.Vendor"),
            new(Finding("SNP0006", "C:/repo/src/C.cs"), "Acme.Legacy"),
        ];

        var audit = SuppressionAuditBuilder.Build(
            [], Config(namespaces: ["Acme.Vendor", "Acme.Legacy", "Acme.Clean"]), null, [], namespaceExcluded, true, null, null);

        audit.NamespaceExclusions.Select(e => (e.Selector, e.SuppressedCount))
            .Should().Equal(("Acme.Clean", 0), ("Acme.Legacy", 1), ("Acme.Vendor", 2));
        audit.NamespaceExclusions.Single(e => e.Selector == "Acme.Vendor").RuleIds
            .Should().Equal("SNP0001", "SNP0006");
        audit.Totals.FindingsHiddenByShadow.Should().Be(3);
    }

    [Fact]
    public void Add_A_Row_For_A_Command_Line_Exclusion_That_Hid_Something()
    {
        // --exclude-namespaces is not in snipper.json, but what it hides is still hidden
        // debt, so it gets a row of its own rather than vanishing into the totals.
        NamespaceExcludedFinding[] namespaceExcluded = [new(Finding("SNP0001", "C:/repo/src/A.cs"), "Acme.FromCli")];

        var audit = SuppressionAuditBuilder.Build(
            [], Config(namespaces: ["Acme.Configured"]), null, [], namespaceExcluded, true, null, null);

        audit.NamespaceExclusions.Select(e => e.Selector).Should().Equal("Acme.Configured", "Acme.FromCli");
    }

    [Fact]
    public void Report_A_Path_Glob_Matching_No_File_On_Disk_As_Certainly_Obsolete()
    {
        var audit = SuppressionAuditBuilder.Build(
            [], Config(paths: [VendoredGlob]), null, null, null, false,
            globMatchesNoFile: static glob => glob == VendoredGlob,
            namespaceExists: null);

        var obsolete = audit.Obsolete.Should().ContainSingle().Subject;
        obsolete.Channel.Should().Be(SuppressionChannel.PathGlob);
        obsolete.Selector.Should().Be(VendoredGlob);
        obsolete.Confidence.Should().Be(SuppressionConfidence.Certain);
        obsolete.Reason.Should().Contain("no file");
    }

    [Fact]
    public void Not_Report_A_Path_Glob_That_Matches_A_File_As_Obsolete_Even_When_It_Hides_Nothing()
    {
        // A glob over clean generated code is a legitimate suppression, not rot. Only a
        // glob that matches nothing on disk is certainly stale.
        var audit = SuppressionAuditBuilder.Build(
            [], Config(paths: [GeneratedGlob]), null, null, null, false,
            globMatchesNoFile: static _ => false,
            namespaceExists: null);

        audit.Obsolete.Should().BeEmpty();
    }

    [Fact]
    public void Report_A_Disabled_Rule_That_Produces_Nothing_As_Only_Suspected_Obsolete()
    {
        var audit = SuppressionAuditBuilder.Build(
            [], Config(disabled: ["SNP0001"]), null, [], [], true, null, null);

        var obsolete = audit.Obsolete.Should().ContainSingle().Subject;
        obsolete.Channel.Should().Be(SuppressionChannel.DisabledRule);
        obsolete.Confidence.Should().Be(SuppressionConfidence.Suspected);
        obsolete.Reason.Should().Contain("no findings");
    }

    [Fact]
    public void Report_A_Namespace_Exclusion_For_An_Undeclared_Namespace_As_Certainly_Obsolete()
    {
        var audit = SuppressionAuditBuilder.Build(
            [], Config(namespaces: ["Acme.Deleted"]), null, null, null, false, null,
            namespaceExists: static _ => false);

        var obsolete = audit.Obsolete.Should().ContainSingle().Subject;
        obsolete.Channel.Should().Be(SuppressionChannel.NamespaceExclusion);
        obsolete.Confidence.Should().Be(SuppressionConfidence.Certain);
    }

    [Fact]
    public void Not_Report_Stale_Namespaces_When_No_Namespace_Inventory_Was_Available()
    {
        var audit = SuppressionAuditBuilder.Build(
            [], Config(namespaces: ["Acme.Deleted"]), null, null, null, false, null, namespaceExists: null);

        audit.Obsolete.Should().BeEmpty();
    }

    [Fact]
    public void Sort_Obsolete_Entries_By_Channel_Then_Selector_So_Output_Is_Deterministic()
    {
        ObsoleteSuppression[] obsolete =
        [
            new(SuppressionChannel.PathGlob, "b/**", SuppressionConfidence.Certain, "r"),
            new(SuppressionChannel.DisabledRule, "SNP0002", SuppressionConfidence.Suspected, "r"),
            new(SuppressionChannel.PathGlob, "a/**", SuppressionConfidence.Certain, "r"),
        ];

        var sorted = SuppressionAuditBuilder.SortObsolete(obsolete);

        sorted.Select(o => $"{o.Channel}/{o.Selector}")
            .Should().Equal("DisabledRule/SNP0002", "PathGlob/a/**", "PathGlob/b/**");
    }

    [Fact]
    public void Report_Path_Glob_Entries_In_Config_Order_So_Output_Mirrors_Snipper_Json()
    {
        var findings = new[]
        {
            Finding("SNP0001", "C:/repo/src/Vendor/A.cs"),
            Finding("SNP0002", "C:/repo/src/Generated/B.cs"),
        };

        var audit = SuppressionAuditBuilder.Build(
            findings, Config(paths: [VendoredGlob, GeneratedGlob]), null, null, null, false, null, null);

        audit.PathGlobs.Select(e => e.Selector).Should().Equal(VendoredGlob, GeneratedGlob);
    }

    [Fact]
    public void Say_That_A_Glob_Matching_No_File_Is_Stale_Only_When_The_Inventory_Is_Non_Empty()
    {
        var inventory = new GlobFileInventory(["C:/repo/src/A.cs", "C:/repo/src/Generated/B.cs"]);

        inventory.Count.Should().Be(2);
        inventory.MatchesNoFile(VendoredGlob).Should().BeTrue();
        inventory.MatchesNoFile(GeneratedGlob).Should().BeFalse();
    }

    [Fact]
    public void Treat_A_Namespace_As_Existing_When_Only_A_Child_Of_It_Is_Declared()
    {
        // Prefix semantics must mirror AnalysisExclusions.FindCoveringExclusion, otherwise the
        // audit would call a live exclusion stale.
        var probe = NamespaceInventory.ExistenceProbe(
            Inventory(["Acme.Domain.Models"], hasFileScopeCode: false));

        probe("Acme.Domain").Should().BeTrue();
        probe("Acme.Domain.Models").Should().BeTrue();
        probe("Acme.Other").Should().BeFalse();
    }

    [Fact]
    public void Treat_The_Global_Sentinel_As_Existing_Only_When_Some_File_Declares_No_Namespace()
    {
        // "<global>" matches no declaration, so the prefix walk cannot see it. Without
        // its own answer the audit would report it stale while it was suppressing
        // findings in file-scope code.
        var withFileScopeCode = NamespaceInventory.ExistenceProbe(
            Inventory(["Acme.Domain"], hasFileScopeCode: true));
        var namespacedThroughout = NamespaceInventory.ExistenceProbe(
            Inventory(["Acme.Domain"], hasFileScopeCode: false));

        withFileScopeCode(AnalysisExclusions.GlobalNamespaceMarker).Should().BeTrue();
        namespacedThroughout(AnalysisExclusions.GlobalNamespaceMarker).Should().BeFalse();
    }

    private static NamespaceInventoryResult Inventory(string[] declared, bool hasFileScopeCode)
    {
        return new NamespaceInventoryResult(declared.ToFrozenSet(StringComparer.Ordinal), hasFileScopeCode);
    }

    private static SnipperConfig Config(
        string[]? disabled = null,
        string[]? namespaces = null,
        string[]? paths = null,
        Dictionary<string, CertaintyTier>? overrides = null)
    {
        return new SnipperConfig
        {
            DisabledRules = disabled is null
                ? FrozenSet<string>.Empty
                : disabled.ToFrozenSet(StringComparer.Ordinal),
            ExcludedNamespaces = namespaces is null
                ? FrozenSet<string>.Empty
                : namespaces.ToFrozenSet(StringComparer.Ordinal),
            ExcludedPathGlobs = paths is null
                ? FrozenSet<string>.Empty
                : paths.ToFrozenSet(StringComparer.Ordinal),
            SeverityOverrides = overrides is null
                ? FrozenDictionary<string, CertaintyTier>.Empty
                : overrides.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    private static SnipperFinding Finding(
        string ruleId,
        string path,
        CertaintyTier certainty = CertaintyTier.Guaranteed)
    {
        return new SnipperFinding(
            RuleId: ruleId,
            Title: "Test",
            Message: $"test finding for {ruleId}",
            Certainty: certainty,
            Category: FindingCategory.UnusedPrivateMember,
            FilePath: path,
            LineNumber: 1,
            CharacterOffset: 0);
    }
}
