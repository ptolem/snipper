namespace Snipper.Cli;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliRunner.SnipperReport))]
[JsonSerializable(typeof(SarifLog))]
[JsonSerializable(typeof(BaselineFile))]
[JsonSerializable(typeof(JsonConfigModel))]
internal sealed partial class JsonReportSerializerContext : JsonSerializerContext;
