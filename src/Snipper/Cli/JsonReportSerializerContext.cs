namespace Snipper.Cli;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliRunner.FindingReportEntry[]))]
[JsonSerializable(typeof(SarifLog))]
[JsonSerializable(typeof(BaselineFile))]
internal partial class JsonReportSerializerContext : JsonSerializerContext;
