namespace Snipper.Cli;

using System.Text.Json.Serialization;

internal sealed record SarifLog(
    [property: JsonPropertyName("$schema")] string Schema,
    string Version,
    SarifRun[] Runs);

internal sealed record SarifRun(
    SarifTool Tool,
    SarifResult[] Results);

internal sealed record SarifTool(SarifToolDriver Driver);

internal sealed record SarifToolDriver(
    string Name,
    string Version,
    string InformationUri,
    SarifReportingDescriptor[] Rules);

internal sealed record SarifReportingDescriptor(
    string Id,
    string Name,
    SarifMultiformatMessageString ShortDescription);

internal sealed record SarifMultiformatMessageString(string Text);

internal sealed record SarifResult(
    string RuleId,
    string Level,
    SarifMessage Message,
    SarifLocation[] Locations,
    Dictionary<string, string> Properties);

internal sealed record SarifMessage(string Text);

internal sealed record SarifLocation(
    SarifPhysicalLocation PhysicalLocation,
    SarifPhysicalLocation[]? RelatedLocations = null);

internal sealed record SarifPhysicalLocation(
    SarifArtifactLocation ArtifactLocation,
    SarifRegion Region);

internal sealed record SarifArtifactLocation(string Uri);

internal sealed record SarifRegion(int StartLine, int StartColumn, SarifArtifactContent? Snippet = null);

internal sealed record SarifArtifactContent(string Text);
