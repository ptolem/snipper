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

// ---- F2: framework-dispatched contract implementations (no C# call exists) ----

// Stand-in for Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck: the
// framework dispatches the contract invisibly. Matched by simple name.
internal interface IHealthCheck
{
    string CheckFwStatus();
}

internal sealed class FwHealthCheck : IHealthCheck
{
    // Contract member — framework-called, never flagged.
    public string CheckFwStatus() => "green";

    // Not on the contract — nothing calls this. Must STILL be flagged.
    public string FwAuxiliaryHelper() => "aux";
}

// Stand-in for ZiggyCreatures FusionCache's IFusionCacheSerializer.
internal interface IFusionCacheSerializer
{
    string SerializeFwEntry();

    string DeserializeFwEntry();
}

internal sealed class FwCacheSerializer : IFusionCacheSerializer
{
    public string SerializeFwEntry() => "{}";

    public string DeserializeFwEntry() => "{}";
}

// ---- F2-negative: plain DI registration is NOT evidence — calls through the
// registered contract are ordinary C# references, so an uncalled contract
// member must STILL be flagged even when an AddScoped-shaped call exists ----

internal static class DiContainer
{
    public static void AddScoped<TContract, TImplementation>()
    {
    }
}

internal interface IFwCartRepository
{
    string GetFwActiveCart();
}

internal sealed class FwCartRepository : IFwCartRepository
{
    // On the contract and registered below, yet never called anywhere — dead.
    public string GetFwActiveCart() => "cart";
}

// ---- Evidence seeds (deliberately never called: the syntax pass scans all
// documents; these methods being flagged proves evidence doesn't blanket-suppress) ----

internal static class FwEvidenceSeeds
{
    internal static void SeedFwEvidence()
    {
        DiContainer.AddScoped<IFwCartRepository, FwCartRepository>();
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

// ---- F5: 1.6.2 framework-dispatched contract additions (OpenApi transformers,
// Swashbuckle filters/examples, xUnit, MVC filters, MediatR pipeline) — matched
// by simple name like F2. The implementation TYPES are framework-instantiated
// (generic registration, assembly scan, DI activation), so the type is evidence
// too; members off the contract still stand on their own. ----

internal interface IOpenApiDocumentTransformer
{
    string TransformFwDocument();
}

internal sealed class FwOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public string TransformFwDocument() => "doc";
}

internal interface IOpenApiOperationTransformer
{
    string TransformFwOperation();
}

internal sealed class FwOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public string TransformFwOperation() => "op";
}

internal interface IOpenApiSchemaTransformer
{
    string TransformFwSchema();
}

internal sealed class FwOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public string TransformFwSchema() => "schema";
}

internal interface IDocumentFilter
{
    string ApplyFwDocument();
}

internal sealed class FwDocumentFilter : IDocumentFilter
{
    public string ApplyFwDocument() => "doc";
}

internal interface IOperationFilter
{
    string ApplyFwOperation();
}

internal sealed class FwOperationFilter : IOperationFilter
{
    public string ApplyFwOperation() => "op";
}

internal interface IExamplesProvider<T>
{
    T GetFwExamples();
}

internal sealed class FwPriceExample : IExamplesProvider<string>
{
    public string GetFwExamples() => "1.00";
}

internal interface IXunitSerializable
{
    string SerializeFwData();

    string DeserializeFwData();
}

internal sealed class FwXunitSerializer : IXunitSerializable
{
    public string SerializeFwData() => "data";

    public string DeserializeFwData() => "data";
}

internal interface ITestCaseOrderer
{
    string OrderFwTestCases();
}

internal sealed class FwTestCaseOrderer : ITestCaseOrderer
{
    public string OrderFwTestCases() => "ordered";
}

internal interface IXunitTestCaseOrderer
{
    string OrderFwXunitCases();
}

internal sealed class FwXunitTestCaseOrderer : IXunitTestCaseOrderer
{
    public string OrderFwXunitCases() => "ordered";
}

internal interface IActionFilter
{
    string OnFwActionExecuting();

    string OnFwActionExecuted();
}

internal sealed class FwActionFilter : IActionFilter
{
    public string OnFwActionExecuting() => "before";

    public string OnFwActionExecuted() => "after";
}

internal interface IAsyncActionFilter
{
    string OnFwActionExecutionAsync();
}

internal sealed class FwAsyncActionFilter : IAsyncActionFilter
{
    public string OnFwActionExecutionAsync() => "around";
}

internal interface IOrderedFilter
{
    int FwFilterOrder { get; }
}

internal sealed class FwOrderedFilter : IOrderedFilter
{
    public int FwFilterOrder => 1;
}

internal interface IExceptionFilter
{
    string OnFwException();
}

internal sealed class FwExceptionFilter : IExceptionFilter
{
    public string OnFwException() => "handled";
}

internal interface IAsyncExceptionFilter
{
    string OnFwExceptionAsync();
}

internal sealed class FwAsyncExceptionFilter : IAsyncExceptionFilter
{
    public string OnFwExceptionAsync() => "handled";
}

internal interface IResultFilter
{
    string OnFwResultExecuting();
}

internal sealed class FwResultFilter : IResultFilter
{
    public string OnFwResultExecuting() => "result";
}

internal interface IAsyncResultFilter
{
    string OnFwResultAsync();
}

internal sealed class FwAsyncResultFilter : IAsyncResultFilter
{
    public string OnFwResultAsync() => "result";
}

internal interface IResourceFilter
{
    string OnFwResourceExecuting();
}

internal sealed class FwResourceFilter : IResourceFilter
{
    public string OnFwResourceExecuting() => "resource";
}

internal interface IAsyncResourceFilter
{
    string OnFwResourceAsync();
}

internal sealed class FwAsyncResourceFilter : IAsyncResourceFilter
{
    public string OnFwResourceAsync() => "resource";
}

internal interface IAuthorizationFilter
{
    string OnFwAuthorization();
}

internal sealed class FwAuthorizationFilter : IAuthorizationFilter
{
    public string OnFwAuthorization() => "allowed";
}

internal interface IAsyncAuthorizationFilter
{
    string OnFwAuthorizationAsync();
}

internal sealed class FwAsyncAuthorizationFilter : IAsyncAuthorizationFilter
{
    public string OnFwAuthorizationAsync() => "allowed";
}

internal interface IPipelineBehavior<TRequest, TResponse>
{
    TResponse HandleFwRequest(TRequest request);
}

internal sealed class FwPipelineBehavior : IPipelineBehavior<string, string>
{
    public string HandleFwRequest(string request) => request;
}

internal interface IStreamPipelineBehavior<TRequest, TResponse>
{
    TResponse HandleFwStream(TRequest request);
}

internal sealed class FwStreamPipelineBehavior : IStreamPipelineBehavior<string, string>
{
    public string HandleFwStream(string request) => request;
}

internal interface IRequestExceptionHandler<TRequest, TResponse, TException>
{
    TResponse HandleFwException(TRequest request);
}

internal sealed class FwRequestExceptionHandler : IRequestExceptionHandler<string, string, string>
{
    public string HandleFwException(string request) => request;
}

internal interface IRequestPreProcessor<TRequest>
{
    string ProcessFwRequest(TRequest request);
}

internal sealed class FwRequestPreProcessor : IRequestPreProcessor<string>
{
    public string ProcessFwRequest(string request) => request;
}

internal interface IRequestPostProcessor<TRequest, TResponse>
{
    string ProcessFwResponse(TRequest request);
}

internal sealed class FwRequestPostProcessor : IRequestPostProcessor<string, string>
{
    public string ProcessFwResponse(string request) => request;
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
