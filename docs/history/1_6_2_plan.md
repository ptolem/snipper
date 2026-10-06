# Plan — Snipper 1.6.2: framework-evidence round 2 + SNP0020 dedupe + attribute-class investigation

**Status: APPROVED, ready to build.** Source: FP review of the 1.6.1 monorepo report (milkrun, ~1,900 findings). Verdict: nothing at Guaranteed/High; ~30 Advisory findings cluster into six evidence-gap classes, one probable SNP0020 bug, one unexplained attribute-class finding. All three judgment calls were put to the user and approved (MediatR pipeline contracts IN, reflection-discovery evidence IN, SNP0023 attribute exclusion IN).

Repo: C:\ws\trimmer. Monorepo acceptance target: C:\ws\milkrun\MILKRUN.slnx (dirty tree from another agent's applied fixes — expected). Validation gates for every wave: full suite green, dogfood 0 on Snipper.slnx, fixture2 pins 23/25 (C:\Users\admin\AppData\Local\Temp\opencode\fixture2-w3\Fixture.slnx), milkrun A/B. Release process: bump `<Version>` in src\Snipper\Snipper.csproj → commit → `dotnet pack src/Snipper -c Release -v q --nologo` → `dotnet tool update --global Snipper --add-source "C:\ws\trimmer\src\Snipper\bin\Release"` → `snipper --version` → self-run expect 0.

## Verified evidence (already checked — do NOT re-verify)

- `EndpointWebApplicationExtensions.MapEndpoints()` (Milkrun.Operations.API\Framework) discovers endpoint groups via `Assembly.GetExportedTypes().Where(t => t.IsSubclassOf(typeof(EndpointGroup)))` + `Activator.CreateInstance` — reflection plugin-by-scan. Flagged types (`CustomerEndpoints`, `OrderEndpoints`, `ProductEndpoints`, `PaymentLessCheckoutEndpoints`, `MilkrunSettingsEndpoints`, `SpendStretchEndpoints`, `StoreEndpoints`) are FPs. The `Carts.*` handler-method findings are TPs (that group's `Map` body is commented out). `GroupDescription` is genuinely never read — TP even under reflection.
- OpenApi transformers implement `IOpenApiDocumentTransformer`/`IOpenApiOperationTransformer`/`IOpenApiSchemaTransformer` (e.g. `BearerSecuritySchemeTransformer`, `BearerOrBasicSecuritySchemeTransformer`). Our 1.5.1 evidence list has `IDocumentTransformer`/`IOperationTransformer`/`ISchemaTransformer` — wrong names, never matched anything.
- Swashbuckle: `AuthenticationRequirementsOperationFilter.Apply` (×2), Utility.API `Apply(OpenApiOperation, OperationFilterContext)` = `IOperationFilter` implementations; `PartnerPriceExample`/`PartnerInventoryExample`/`PartnerProductExample : IExamplesProvider<T>` (+`GetExamples`) in Milkrun.Partners.Products.Webhooks, instantiated by Swashbuckle reflection.
- xUnit: `CustomSerializable<T>`/`CustomDescribable : IXunitSerializable` (Serialize/Deserialize dispatched); `TestCasePriorityOrderer : ITestCaseOrderer` registered via `[TestCaseOrderer("Milkrun.IntegrationTests.TestFramework.TestCasePriorityOrderer", "Milkrun.IntegrationTests")]` string attribute on test classes.
- MVC: `HttpResponseFilter : IActionFilter, IOrderedFilter` — OnActionExecuting/OnActionExecuted/Order are MVC-dispatched.
- MediatR pipeline: 5 `Handle` methods on `PerformanceBehaviour`/`PipelineExceptionBehaviour`/`ValidationBehaviour`/`LoggingBehavior`/`ExceptionHandlerBehaviour` — middleware, never directly callable.
- SNP0020 double emission: `LocalHostedService.cs` has TWO findings at the same position (line 6 char 5), 25 lines and 10 lines — overlapping blocks, missing containment dedupe.
- Unexplained: `UserJourneyTestPriorityAttribute` (and `TestPriorityAttribute`) flagged "type has no references" despite active applications (`GuestBasic.cs:11 [UserJourneyTestPriority(UserJourneyTestPriority.Authentication)]`). Needs fixture repro before any fix.
- Confirmed TPs not to "fix": 4 unused global usings (CS8019), SNP0009 `regionSettings`/`searchResponse` ×2, SNP0025 ×3 (proven strippable in 1.6.1 review), SNP0012s (compile-closure-safe by construction), SNP0003 ×16 (spot-checked: Orders.Webhooks has no IExamplesProvider — examples are in Products.Webhooks; Checkout/Client/DataImporter never call AddNewtonsoftJson), SNP0004 ×10 in Milkrun.UnitTests (zero namespace usages verified), SNP0018 obsolete-DTOs (factually correct at Moderate).

## Work items (red-green fixture-first per item; fixture scenarios in NEW files or the named existing fixture files, never CoreLib\DeadCode.cs)

### 1. Contract list additions — `src\Snipper\Analysis\FrameworkEvidenceIndex.cs` (simple-name match, no logic change)
Add: `IOpenApiDocumentTransformer`, `IOpenApiOperationTransformer`, `IOpenApiSchemaTransformer`, `IDocumentFilter`, `IOperationFilter`, `IExamplesProvider`, `IXunitSerializable`, `ITestCaseOrderer`, `IXunitTestCaseOrderer`, `IActionFilter`, `IAsyncActionFilter`, `IOrderedFilter`, `IExceptionFilter`, `IAsyncExceptionFilter`, `IResultFilter`, `IAsyncResultFilter`, `IResourceFilter`, `IAsyncResourceFilter`, `IAuthorizationFilter`, `IAsyncAuthorizationFilter`, `IPipelineBehavior`, `IStreamPipelineBehavior`, `IRequestExceptionHandler`, `IRequestPreProcessor`, `IRequestPostProcessor`. Keep existing entries (incl. the wrong-named ones — harmless). Request/notification handlers (IRequestHandler/INotificationHandler) stay OUT (1.5.1: test-callable).
Fixture: extend `test\Snipper.Tests\TestAssets\SampleApp\CoreLib\FrameworkPatterns.cs` with stand-in contracts per family; assert the implementing types' dispatched members produce no SNP0006 finding.

### 2. Reflection type-discovery evidence (new source feeding SNP0005/0006)
Syntax-first scan for `x.IsSubclassOf(T)` and `T.IsAssignableFrom(...)` invocations; semantic-confirm via GetSymbolInfo; any type derived from such a scan-base `T` within the solution counts as usage evidence (over-approximate on unresolvable shapes → treat named type as scan base). Fixture (new file): an `EndpointGroup`-style abstract base + reflection scan loop + two subclasses (one never instantiated) → both suppressed; a control subclass of a non-scanned base still flagged.

### 3. SNP0020 containment dedupe — `src\Snipper\Analysis\CommentedCodeAnalyser.cs`
After block collection, drop any block fully contained (same file, span within) in a larger reported block. Fixture: nested/overlapping commented regions mirroring LocalHostedService (outer 25-line + inner 10-line) → exactly one finding.

### 4. Attribute-class reference investigation (root-cause, NOT evidence-papering)
Fixture repro: attribute class applied on methods in the same project → if flagged, it's an SNP0006 reference-discovery bug — fix at the root. If not reproduced, investigate milkrun specifics (`extern alias ProductApi;`, test TFM) before touching anything.

### 5. SNP0023 attribute exclusion — `src\Snipper\Analysis\HierarchyDeadCodeAnalyser.cs`
Skip `System.Attribute`-derived classes in never-inherited-virtual-class (class + member paths share the candidate gate — extend it). `CustomProductType*Attribute` findings disappear; `MilkrunDiscount` stays. Fixture: virtual-declaring attribute class → suppressed; virtual-declaring plain class still flagged.

## Expected milkrun A/B (vs the reviewed 1.6.1 report)
The ~30 Advisory findings gone (endpoint groups, transformers, filters, examples, xUnit, MediatR pipeline); SNP0020 −1 (duplicate); SNP0023 3 → 1; all else count-identical. Then ship per the release process: version 1.6.2, docs (docs\1_6_1_fp_review.md gains a round-2 section or new 1_6_2_fp_review.md; roadmap status line; README "Framework entry points" paragraph gains the new families), commit, pack, install, verify.
