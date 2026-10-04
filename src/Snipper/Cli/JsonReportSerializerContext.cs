namespace Snipper.Cli;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<SuppressionChannel>), typeof(JsonStringEnumConverter<SuppressionConfidence>), typeof(JsonStringEnumConverter<EntropyRateStatus>)])]
[JsonSerializable(typeof(SnipperReport))]
[JsonSerializable(typeof(SarifLog))]
[JsonSerializable(typeof(BaselineFile))]
[JsonSerializable(typeof(EntropyLedgerFile))]
[JsonSerializable(typeof(JsonConfigModel))]
internal sealed partial class JsonReportSerializerContext : JsonSerializerContext;
