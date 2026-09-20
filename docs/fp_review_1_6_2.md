# FP Review — 1.6.2 (monorepo Advisory-tier review, 2026-09-20)

**Trigger:** manual review of the 1.6.1 monorepo report's Guaranteed/High/Advisory tiers (~1,900 findings). Verdict: nothing at Guaranteed/High; ~40 Advisory findings clustered into six framework-evidence gaps, one SNP0020 misreport, and one SNP0005/0006 reference-discovery bug. Each class's disposition and the 1.6.2 fix below. Validation: 297/297 suite (45 new pins), dogfood 0, fixture2 pins 23/25, same-tree monorepo A/B (installed 1.6.1 vs build): **SNP0006 1,125 → 1,085 (−40), SNP0023 3 → 1 (−2), SNP0020 12 → 12 (locations corrected), everything else count-identical.**

## R2-1 — OpenApi transformer contract names were wrong (5 findings) → FIXED

**Claim:** `TransformAsync` implementations (`BearerSecuritySchemeTransformer`, `InfoDocumentTransformer`, `RequestContextHeadersConfigurer`, `ServerHttpsDocumentTransformer`, +1) flagged despite being framework-dispatched.

**Verification:** the 1.5.1 contract list carried `IDocumentTransformer`/`IOperationTransformer`/`ISchemaTransformer` — names that match nothing real. The .NET 9+ contracts are `IOpenApiDocumentTransformer`/`IOpenApiOperationTransformer`/`IOpenApiSchemaTransformer`; the Swashbuckle equivalents are `IDocumentFilter`/`IOperationFilter`/`ISchemaFilter` (the last was already present). Simple-name matching never fired.

**Fix:** contract list gained the three `IOpenApi*Transformer` names plus `IDocumentFilter`/`IOperationFilter`; the old wrong names stay (harmless).

## R2-2 — Swashbuckle examples + filters (9 findings) → FIXED

**Claim:** three `IExamplesProvider<T>` implementations (types AND `GetExamples` methods) and three `IOperationFilter.Apply` implementations flagged.

**Verification:** `AddSwaggerExamplesFromAssemblyOf<T>()` instantiates example types by assembly scan — no C# reference exists for either the type or the method. `IOperationFilter` was missing from the contract list.

**Fix:** `IExamplesProvider` added, and — the design correction — **implementations of framework-dispatched contracts are now evidence at the TYPE level too** (1.5.1 suppressed only the contract members, reasoning that registration always references the type; scan-instantiated implementations break that assumption). Members NOT on the contract still stand on their own (fixture negative `FwAuxiliaryHelper` unchanged).

## R2-3 — xUnit dispatched contracts (6 findings) → FIXED

`CustomSerializable<T>`/`CustomDescribable : IXunitSerializable` (Serialize/Deserialize) and `TestCasePriorityOrderer : ITestCaseOrderer` (type + `OrderTestCases`, registered via `[TestCaseOrderer("full.type.name", "assembly")]` string attribute). Fix: `IXunitSerializable`, `ITestCaseOrderer`, `IXunitTestCaseOrderer` added.

## R2-4 — MVC filter family (3 findings) → FIXED

`HttpResponseFilter : IActionFilter, IOrderedFilter` — `OnActionExecuting`/`OnActionExecuted`/`Order` are MVC-dispatched. Fix: `IActionFilter`, `IAsyncActionFilter`, `IOrderedFilter`, `IExceptionFilter`, `IAsyncExceptionFilter`, `IResultFilter`, `IAsyncResultFilter`, `IResourceFilter`, `IAsyncResourceFilter`, `IAuthorizationFilter`, `IAsyncAuthorizationFilter` added.

## R2-5 — MediatR pipeline middleware (5 findings) → FIXED

`Handle` on `PerformanceBehaviour`/`PipelineExceptionBehaviour`/`ValidationBehaviour`/`LoggingBehavior`/`ExceptionHandlerBehaviour`. Pipeline behaviours are middleware — never directly callable, always runtime-dispatched, so the 1.5.1 "test-callable" carve-out (which keeps request/notification HANDLERS out) does not apply. Fix: `IPipelineBehavior`, `IStreamPipelineBehavior`, `IRequestExceptionHandler`, `IRequestPreProcessor`, `IRequestPostProcessor` added.

## R2-6 — Reflection plugin-by-scan (12 findings) → FIXED (new evidence source)

**Claim:** 8 `*CommandGenerator` types, 2 `ProductTypeDefinitionBase` subclasses, 1 `EcfUnknownMessage`, and (on the pre-fix report) 7 endpoint-group types flagged with zero references.

**Verification:** all are instantiated by assembly scan + DI/Activator: `typeof(IOrderCommandGenerator).IsAssignableFrom(x)` style scans in the CommandsGenerator hosts, `t.IsSubclassOf(typeof(ProductTypeDefinitionBase))` in `ProductTypesSetupTask`, `IsSubclassOf(typeof(EndpointGroup))` in `EndpointWebApplicationExtensions.MapEndpoints()`.

**Fix (FrameworkEvidenceIndex pass 4):** syntax-first seeds for `x.IsSubclassOf(typeof(T))` and `typeof(T).IsAssignableFrom(...)`, semantically confirmed to bind `System.Type` (unresolvable → over-approximate, per doctrine); a simple-name closure over base lists then marks derived types (transitively) as evidence for the TYPE finding only. Members still stand on their own — the fixture pins a never-read property on a discovered type staying flagged (the milkrun `GroupDescription` true positive survives), and the commented-out `Carts` handlers stay flagged.

**Known over-approximation:** the closure matches by simple name, so `EcfUnknownMessage` (MockingService) was suppressed via a name collision — its base shares the simple name `EcfEventMessageBase` with an unrelated `IRequest`-implementing class in FulfilmentUpdatesConsumer (`typeof(IRequest).IsAssignableFrom` is a real MediatR registration scan). Doctrine-consistent (err toward "used", Advisory tier); the type is also genuinely a wire DTO twin. Recorded, not chased.

## R2-7 — SNP0020 reported the anchor's location, not the block's (12 findings) → FIXED (bug)

**Claim (initial hypothesis):** two findings on `LocalHostedService.cs` line 6 char 5 — "25 line(s)" and "10 line(s)" — looked like a nested-block double emission needing a containment dedupe.

**Root cause (ground truth):** no overlap. Two genuinely distinct commented blocks (own lines 22–46 and 48–57) both attach as leading trivia to the same closing-brace token, and the finding reported the anchor NODE's location (the method's `{`, line 6) instead of the block's. Every SNP0020 finding was misplaced to its anchor — e.g. a 992-line commented file reported at line 993. A containment dedupe would have wrongly DELETED a true positive.

**Fix:** findings report the first trivia's own line/character. Monorepo: 12 findings removed + 12 added — pure relocations, count-identical, each verified against the source.

## R2-8 — Applied attribute classes flagged as unreferenced (1 finding) → FIXED (bug)

**Claim:** `UserJourneyTestPriorityAttribute` flagged "type has no references" while actively applied on test methods (`[UserJourneyTestPriority(...)]`).

**Root cause:** attribute applications omit the `Attribute` suffix, so the textual usage index (`SolutionUsageIndex`, which keys documents by spelled identifier) only ever contains the SHORT spelling; the full-name lookup returned zero documents, which SNP0005/0006 treats as proof of unreferenced.

**Fix (UnusedNonPrivateMemberAnalyser):** for type candidates whose name ends in `Attribute`, union the suffix-stripped spelling's documents into the reference-search set. The semantic search still does the real filtering (fixture: the annotated targets' own unused-member findings survive). Same-shaped lookups elsewhere fail safe (they gate findings, never produce them) and were left alone.

## R2-9 — SNP0023 on attribute classes (2 findings) → FIXED (exclusion)

`CustomProductTypeNestedTypeReferenceAttribute` / `CustomProductTypeReferenceAttributeAttribute` flagged as never-inherited virtual classes. Attributes are terminal by convention — a virtual member on one is not a speculative extension point. Fix: `System.Attribute`-derived classes are excluded from SNP0023 candidacy. `MilkrunDiscount` (domain type) stays — 3 → 1 as predicted.

## Confirmed true positives (not "fixed")

4 unused global usings (CS8019), SNP0009 `regionSettings`/`searchResponse` ×2, SNP0025 ×3 (proven strippable in the 1.6.1 review), SNP0012s (compile-closure-safe by construction), SNP0003 ×16 (spot-checked: Orders.Webhooks has no `IExamplesProvider` usage; the NewtonsoftJson trio never call `AddNewtonsoftJson`), SNP0004 ×10 in Milkrun.UnitTests (zero namespace usages verified), SNP0018 obsolete DTOs (factually correct at Moderate — removal is the teams' stated intent; the certainty tier carries the API-contract risk).

## Validation

297/297 suite (45 new pins across the five fixes), dogfood 0, fixture2 pins 23/25, same-tree monorepo A/B at the top of this document. Residual documented risks: simple-name matching (contract list and scan closure) over-approximates by design at the Advisory tier; scan shapes with a non-`typeof` receiver (`variable.IsAssignableFrom(t)`) remain invisible and under-suppress (false-positive direction — safe).
