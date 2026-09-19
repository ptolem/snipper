namespace CoreLib;

// Wave 4 framework-evidence scenarios (SNP0005/0006): members frameworks invoke
// without any C# reference — serializers, model binders, DI-activated contracts,
// record synthesized members. Stand-in attribute/framework classes keep the
// fixture self-contained; evidence matching is by simple name. All names carry a
// distinctive Fw prefix — assertions are substring-based across the whole fixture.

// ---- Stand-in framework surface (evidence name-matching targets) ----

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
internal sealed class JsonSerializableAttribute : Attribute
{
    public JsonSerializableAttribute(Type type)
    {
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostAttribute : Attribute
{
    public PostAttribute(string path)
    {
    }
}

[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class FromBodyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class JsonPropertyNameAttribute : Attribute
{
    public JsonPropertyNameAttribute(string name)
    {
    }
}

internal static class JsonSerializer
{
    public static T? Deserialize<T>(string json) => default;
}

internal static class HealthCheckRegistration
{
    public static void AddCheck<T>()
    {
    }
}

internal abstract class AbstractValidator<T>;

// ---- F1a: [JsonSerializable]-registered DTO graph ----

[JsonSerializable(typeof(FwOrderPayload))]
internal static class FwJsonContext;

internal class FwOrderPayload
{
    public string? FwSerializedNote { get; init; }

    public FwNestedPayload? FwChild { get; init; }
}

internal class FwNestedPayload
{
    public int FwNestedCount { get; init; }
}

// ---- F1b: Refit interface signature DTOs (incl. computed wire property) ----

internal interface IFwBillingApi
{
    [Post("/fw/billing")]
    Task<FwBillingReceipt> SubmitFwBilling(FwBillingDraft draft);
}

internal class FwBillingDraft
{
    public string FwDraftKind => "billing";

    public int FwDraftUnits { get; init; }
}

internal class FwBillingReceipt
{
    public string? FwReceiptCode { get; init; }
}

// ---- F1c: [FromBody]-bound model ----

internal static class FwInboxEndpoint
{
    public static string HandleFwInbound([FromBody] FwInboundEnvelope envelope) => "ok";
}

internal class FwInboundEnvelope
{
    public string? FwEnvelopeTag { get; init; }
}

// ---- F1d: serializer call-site DTO ----

internal class FwCachedProjection
{
    public long FwProjectionTicks { get; init; }
}

// ---- F1e: member-level serialization attribute ----

internal class FwAttributedContract
{
    [JsonPropertyName("fw_renamed")]
    public string? FwRenamedValue { get; init; }
}

// ---- F2: framework-registered contract implementation ----

internal interface IFwLifecycleProbe
{
    string ProbeFwStatus();
}

internal sealed class FwLifecycleProbe : IFwLifecycleProbe
{
    // Interface-implementing member of a registered type — framework-called.
    public string ProbeFwStatus() => "green";

    // Not on the interface — nothing calls this. Must STILL be flagged.
    public string FwAuxiliaryHelper() => "aux";
}

// ---- Evidence seeds (deliberately never called: the syntax pass scans all
// documents; these methods being flagged proves evidence doesn't blanket-suppress) ----

internal static class FwEvidenceSeeds
{
    internal static void SeedFwEvidence()
    {
        HealthCheckRegistration.AddCheck<FwLifecycleProbe>();
        _ = JsonSerializer.Deserialize<FwCachedProjection>("{}");
    }
}

// ---- F3: positional record (synthesized members are never candidates) ----

internal record FwAuditStamp(string FwAuditKey, int FwAuditVersion);

// Record with an EXPLICIT Deconstruct: a record-protocol member — never a finding
// even when nothing deconstructs it (the unused record type itself still flags).
internal record FwSettlementEntry
{
    public string? FwSettlementKey { get; init; }

    public void Deconstruct(out string? key)
    {
        key = FwSettlementKey;
    }
}

// ---- F4: middleware + FluentValidation conventions ----

internal sealed class FwAuditMiddleware
{
    public Task InvokeFwPipelineAsync() => Task.CompletedTask;

    public Task InvokeAsync() => Task.CompletedTask;
}

internal sealed class FwOrderRequestValidator : AbstractValidator<string>
{
    public bool FwValidateRuleSet() => true;
}

// ---- Negative controls: zero evidence — everything here must be flagged ----

internal class FwOrphanedDto
{
    public string? FwOrphanedNote { get; init; }
}

internal class FwPlainHelper
{
    public string FwNeverCalledLogic() => "x";
}
