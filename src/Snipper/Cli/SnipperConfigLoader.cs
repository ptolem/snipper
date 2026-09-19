namespace Snipper.Cli;

using System.Collections.Frozen;
using System.Text.Json;
using Snipper.Models;

/// <summary>
/// Discovers and parses snipper.json: walk up from the target's directory, first file
/// wins. Tolerant by design — missing file means defaults; malformed JSON, unreadable
/// files, unsupported schema versions, unknown rule ids, and unknown severities
/// degrade to warnings, never to failures.
/// </summary>
internal static class SnipperConfigLoader
{
    private const string FileName = "snipper.json";

    public static SnipperConfig Load(string targetPath, out string? configPath, List<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(warnings);

        configPath = Discover(targetPath);
        if (configPath is null)
        {
            return SnipperConfig.Empty;
        }

        JsonConfigModel? model;
        try
        {
            model = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                JsonReportSerializerContext.Default.JsonConfigModel);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not parse config file '{configPath}' — defaults apply. ({ex.Message})");
            return SnipperConfig.Empty;
        }

        if (model is null)
        {
            return SnipperConfig.Empty;
        }

        if (model.Version != 1)
        {
            warnings.Add($"Config file '{configPath}' has unsupported schema version {model.Version} — defaults apply.");
            return SnipperConfig.Empty;
        }

        var disabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var severityOverrides = new Dictionary<string, CertaintyTier>(StringComparer.OrdinalIgnoreCase);
        if (model.Rules is not null)
        {
            foreach (var (rawRuleId, rawSeverity) in model.Rules)
            {
                if (rawRuleId.Length < 4 || !rawRuleId.StartsWith("SNP", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Ignoring unknown rule id '{rawRuleId}' in '{configPath}'.");
                    continue;
                }

                var ruleId = rawRuleId.ToUpperInvariant();
                if (string.Equals(rawSeverity, "off", StringComparison.OrdinalIgnoreCase))
                {
                    disabledRules.Add(ruleId);
                }
                else if (Enum.TryParse<CertaintyTier>(rawSeverity, ignoreCase: true, out var tier))
                {
                    severityOverrides[ruleId] = tier;
                }
                else
                {
                    warnings.Add($"Ignoring unknown severity '{rawSeverity}' for rule '{ruleId}' in '{configPath}'.");
                }
            }
        }

        return new SnipperConfig
        {
            DisabledRules = disabledRules.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SeverityOverrides = severityOverrides.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ExcludedNamespaces = (model.Exclude?.Namespaces ?? [])
                .Where(static n => !string.IsNullOrWhiteSpace(n))
                .ToFrozenSet(StringComparer.Ordinal),
            ExcludedPathGlobs = (model.Exclude?.Paths ?? [])
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToFrozenSet(StringComparer.Ordinal),
        };
    }

    private static string? Discover(string targetPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        while (directory is { Length: > 0 })
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}

internal sealed record JsonConfigModel(
    int Version = 1,
    Dictionary<string, string>? Rules = null,
    JsonConfigExclude? Exclude = null);

internal sealed record JsonConfigExclude(
    string[]? Namespaces = null,
    string[]? Paths = null);
