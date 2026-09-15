namespace Snipper.Cli;

using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snipper.Models;

/// <summary>
/// Baseline support for CI adoption: existing findings are recorded as accepted
/// so only NEW dead code is reported. Fingerprints are content hashes over
/// rule + relative path + message — deliberately excluding line numbers so
/// incidental edits don't churn the baseline.
/// </summary>
internal static class BaselineService
{
    public static string ComputeFingerprint(SnipperFinding finding, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var relativePath = finding.FilePath.Length > 0
            ? Path.GetRelativePath(baseDirectory, finding.FilePath).Replace('\\', '/')
            : string.Empty;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{finding.RuleId}|{relativePath}|{finding.Message}"));
        return Convert.ToHexStringLower(hash);
    }

    public static FrozenSet<string> Load(string baselinePath)
    {
        if (!File.Exists(baselinePath))
        {
            return FrozenSet<string>.Empty;
        }

        try
        {
            var json = File.ReadAllText(baselinePath);
            var baseline = JsonSerializer.Deserialize(json, JsonReportSerializerContext.Default.BaselineFile);
            return baseline?.Fingerprints is { Length: > 0 } fingerprints
                ? fingerprints.ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return FrozenSet<string>.Empty;
        }
    }

    public static void Write(string baselinePath, IReadOnlyCollection<string> fingerprints)
    {
        var parentDirectory = Path.GetDirectoryName(baselinePath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        var baseline = new BaselineFile(Version: "1", Fingerprints: [.. fingerprints.Order(StringComparer.Ordinal)]);
        var json = JsonSerializer.Serialize(baseline, JsonReportSerializerContext.Default.BaselineFile);
        File.WriteAllText(baselinePath, json);
    }
}

internal sealed record BaselineFile(string Version, string[] Fingerprints);
