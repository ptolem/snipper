# 1.7.1 false-positive investigation — first 800 findings on MILKRUN.slnx

**Trigger:** triaging the newest release against the owner's monorepo. Not a scheduled gate — this is
the first pass over the first 800 findings of a 6,812-finding report, done to find out whether Snipper's
newest rules (SNP0031/SNP0032) and its established ones hold up on 2,763 real files.

**Target:** `C:\ws\milkrun\MILKRUN.slnx` @ `01d60f8db`, 81 projects, 3,896 commits, multi-targeted.
Owner-specified namespace exclusions. Packaged tool **1.7.1**, report taken from the first run of the
1.7.1 release gate.

## Headline

| | count |
|---|---|
| Investigated | **800** |
| True positive — worth fixing | **482** (60.3 %) |
| Real duplication, worthless to a human ("benign") | **53** (6.6 %) |
| **False positive** | **265** (33.1 %) |
| Stale (construct no longer in source) | **0** |
| Undetermined | **0** |

**265 false positives in 800 findings.** Two engine-level defects account for **75** of them and are
bugs in Snipper rather than tuning problems. Four rule-level classes account for the other 188.

**The 33 % must not be extrapolated to the full report.** The first 800 is a *biased* sample: report order
is certainty, then path, so this slice is dominated by the Guaranteed/High/Moderate rules plus the start
of the Advisory block — and the Advisory block is 4,675 findings of SNP0031/SNP0032. A whole-report FP
rate would be far lower, and a whole-report rate is the wrong question anyway because these are the
findings a reviewer is most likely to act on.

## The sample

The 800 break down across 20 rules:

| rule | n | FP | FP rate |
|---|---|---|---|
| SNP0019 Unused Using Directive | 250 | 62 | 25 % |
| SNP0032 Clone Set Fix Drift | 86 | **74** | **86 %** |
| SNP0006 Unused Public Member | 70 | 49 | 70 % |
| SNP0028 Redundant Qualifier | 62 | 0 | — |
| SNP0031 Duplicate Code Fragment | 57 | 1 | 2 % |
| SNP0009 Unused Local Variable | 54 | 0 | — |
| SNP0018 Obsolete Unreferenced Member | 49 | **48** | **98 %** |
| SNP0025 Redundant Type Arguments | 39 | 0 | — |
| SNP0012 Redundant Transitive Package | 30 | **29** | **97 %** |
| SNP0022 Redundant Default Argument | 19 | 0 | — |
| SNP0003 Unreferenced Package | 16 | 1 | 6 % |
| SNP0001 Unused Private Member | 15 | 0 | — |
| SNP0026 Redundant Cast | 12 | 0 | — |
| SNP0004 / SNP0010 / SNP0021 | 8 each | 0 | — |
| SNP0005 Unused Internal Member | 7 | 0 | — |
| SNP0024 Member Can Be Static | 6 | 1 | 17 % |
| SNP0020 Commented-Out Code | 2 | 0 | — |
| SNP0029 Empty Constructor | 2 | 0 | — |

Zero FPs across 234 findings on eleven rules — but see §5, because "zero FP" is not the same as "correct
reasoning", and on two of those rules the stated premise is demonstrably false.

---

# 1. Two engine-level defects

These are the ones that matter. Both were re-verified by hand in Snipper's source and, for the second,
against MILKRUN's git history, independently of the agent that found them.

## E1 — `Extend` never verifies the 60-token window it was handed

`DuplicateFragmentAnalyser.FindFragments` pairs two windows that landed in the same hash bucket and hands
the pair straight to `Extend`. `Extend` (`DuplicateFragmentAnalyser.cs:356`) begins:

```csharp
var forward = WindowTokens;                                    // 60
while (leftStart + forward < left.Count
    && rightStart + forward < right.Count
    && string.Equals(left[leftStart + forward], right[rightStart + forward], …))
```

It only ever compares offsets **≥ 60**. Tokens `[0, 59]` — the window itself — are never compared. So the
premise "these two windows matched" is assumed, not proven.

Three consequences, all verified:

1. **A hash collision becomes a clone.** The bucket key is 32-bit FNV-1a. At ~10⁶ windows on this
   codebase, birthday collisions are guaranteed in the hundreds. Each one produces a 60-token "fragment"
   between two unrelated files.
2. **`if (length < WindowTokens) continue;` at `DuplicateFragmentAnalyser.cs:254` is dead code.** `Extend`
   returns `forward + Math.Max(leftBackward, rightBackward)` with `forward >= 60` unconditionally, so
   `length` is never below 60.
3. **`TokenShingleIndex.Hash`'s own documentation is wrong** (`:193-198`): "Collisions are possible at
   this scale; every candidate is re-verified token by token during extension, so a collision costs time
   and never correctness." It does not cost time. It costs correctness.

Compounding it, `Normalize` collapses every identifier to `ID`, string literal to `STR` and number to
`NUM`, so even a *verified* match is a shape match. Verified shapes still collide across unrelated
domains — const tables, DI registration lists and DTO property lists all normalise to the same tokens.

**Evidence** — `src/M60.BridgingServices.Client/ServiceHost/ArticleEventListenerServiceHostFactory.cs:15`
vs `src/Metro60.CommerceTools.Utility.API/WebApplicationBuilderExtensions.cs:48`:

```csharp
services.AddSingleton<IGraphQLService, GraphQLService>();      // 10 tokens
services.AddSingleton<IProductService, ProductService>();
services.AddSingleton<IGraphQLService, GraphQLService>();
```
```csharp
services.AddScoped<ICustomerServiceCommon, CustomerServiceCommon>();   // identical 10-token shape
services.AddScoped<ICartServiceCommon, CartServiceCommon>();
services.AddScoped<IOrderSummaryBuilder, OrderSummaryBuilder>();
```

Two different bounded contexts, zero shared logic. Another family: `LogEvents.cs:1` ↔ `LogEventIds.cs:1`
(11 findings) — disjoint services with disjoint EventId ranges, `public const int X = 1002001;` matching
`public const int Y = 200100;`.

**Fix** — prove the window before seeding:

```csharp
private static (int LeftStart, int RightStart, int Length) Extend(
    IReadOnlyList<string> left, int leftStart,
    IReadOnlyList<string> right, int rightStart)
{
    // A bucket match is a hash collision until proven otherwise. FNV-1a/32 collides
    // at this scale, and Extend only compares offsets >= WindowTokens, so an
    // unverified window silently becomes a 60-token "clone".
    for (var i = 0; i < WindowTokens; i++)
    {
        if (!string.Equals(left[leftStart + i], right[rightStart + i], StringComparison.Ordinal))
        {
            return (leftStart, rightStart, 0);
        }
    }

    var forward = WindowTokens;   // now provably earned
    …
```

This alone revives the `length < WindowTokens` guard at `:254`. It will reduce SNP0031's count
substantially, which is worth stating plainly: **some of the 4,373 findings are hash collisions, not
duplication.** The 1.7.1 tuning pass measured the fragment-length distribution and concluded there was no
noise tail to trim; that conclusion was drawn from the output distribution and was not wrong about the
*tail*, but it could not see this, because the defect is upstream of the reporting layer.

Secondary hardening, if E1 alone leaves too much shape-collision: carry each window's **identifier
spellings** alongside the normalised stream and require ≥50 % overlap before offering a candidate to
`Extend`. Copy-paste clones keep identical names and Type-2 rename clones keep high overlap; const tables
and DI lists have none. Then extend `IsExcludedSubtree` (`TokenShingleIndex.cs:41`) to prune
`services.Add*(…)` invocation subtrees, property-declaration subtrees, `const` field declarations and
`[LoggerMessage]`/`[Counter]`/`[Histogram]` methods — the same treatment `UsingDirectiveSyntax` already
gets.

## E2 — file-creation commits reported as "one-sided defensive fix"

`CloneDriftDetector.Evaluate`'s hunk loop (`:486`) has **no** pre-image guard:

```csharp
foreach (var hunk in hunks)
{
    if (!CloneDriftClassifier.HunkTouchesRegion(hunk, member.StartLine, member.EndLine))
    {
        continue;
    }
    var renameOnly  = RenameDetector.IsRenameOnly(hunk.AddedLines, hunk.RemovedLines);
    var fixShaped   = !renameOnly && DefensiveFixMarkers.IsFixShaped(hunk.AddedLines);
```

The detector already knows this is wrong. `SiblingWasFixedEarlier` (`:608`) guards it:

```csharp
foreach (var hunk in hunks)
{
    if (hunk.OldCount > 0
        && CloneDriftClassifier.HunkTouchesRegion(hunk, sibling.StartLine, sibling.EndLine))
    {
        return true;
    }
}
```

`hunk.OldCount > 0` — the comment on it reads *"Creation commits are excluded: a commit that adds a file
has not 'fixed' a clone"*. The exclusion exists on the **sibling** side and was never applied to the
**member** side.

**This is 74 of the 86 SNP0032 findings in this batch — 58 of 82 High and all 4 Advisory.** Every one
carries a `@@ -0,0 +1,N @@` hunk. Verified by hand:

```
$ git show 094f266 -- src/HelloWorld/GraphQL/GetOrdersQuery.cs
  new file mode 100644
  @@ -0,0 +1,66 @@
$ git show 0d61321 -- src/M60.BridgingServices.Core/Helpers/TimeSpanHelper.cs
  new file mode 100644
  @@ -0,0 +1,22 @@
```

A commit that *creates* a file cannot have left a sibling copy un-fixed, because before it there was no
copy at all. The commit titles make the absurdity plain: `094f266` is
*"Merged PR 4399: m60-4634: abstract CT graphql query"* — the merge that **introduced** the clone set is
being reported as drift within it.

Worse, it inverts the rule's meaning: the "later (… on …) reached …" catch-up clause fires for commits
that predate the sibling, so the finding can name a catch-up that happened *before* the change it is
supposed to resolve.

**Fix** — one condition, in `Evaluate` at `:486`:

```csharp
foreach (var hunk in hunks)
{
    // A hunk with an empty pre-image is a file or region creation. It cannot be a
    // one-sided fix: nothing existed to fall out of sync. Mirrors the OldCount guard
    // SiblingWasFixedEarlier already applies to siblings (:608).
    if (hunk.OldCount == 0)
    {
        continue;
    }
    …
```

**Expected effect: SNP0032 High 82 → roughly 24** in this sample, and the remaining findings become
arguable. This is the single highest-value fix in this document.

---

# 2. Rule-level false-positive classes

## F1 — SNP0019: one directive reported twice (62 of 250, 25 %)

`UnusedUsingDirectiveAnalyser.cs:47-88` surfaces every diagnostic the compilation emits. When a
file-level `using` duplicates a project-level `global using`, Roslyn emits **two** diagnostics on the
**same** `UsingDirectiveSyntax` node — CS8019 (unnecessary) and CS8933 (duplicates a global) — and the
loop appends both. Nothing dedupes on `(document, span)`.

Examples: `src/Milkrun.Integration.MongoDb/InstrumentedMongoClientFactory.cs:2`,
`src/Milkrun.Orders.CommandsProcessor/Models/ECFOrder/EcfOrderCancellationRequest.cs:1`,
`test/Milkrun.UnitTests/HostedListener/…/MongoIndexerShould.cs:14`. Worst file:
`ClearLiquorSelfExclusionCommandHandlerShould.cs` — 22 findings for 11 directives.

`BuildMessage` already reasons about exactly this hazard for the *in-file* duplicate case
(`IsVerbatimDuplicateEarlierInFile`, `:130-165`, whose comment cites "monorepo FP-4"). The CS8019+CS8933
pair needs the same treatment and does not get it.

**Why it matters beyond tidiness:** `BaselineService.ComputeFingerprint` hashes
`RuleId|relativePath|Message`, and the two findings have *different* messages, so both enter the baseline
and a consumer acting on one sees the other resurface as new. They also sit at different character
offsets, so dedupe by `(file, line)` misses them too.

**Fix** — key on `(document.Id, node.SpanStart)`. Enumeration order is producer-completion order (the file
says so at `:91-96`), so **overwrite** on the later diagnostic rather than skip-on-first-write; prefer the
CS8933 text since it names the real reason. Add a fixture case — `TestAssets/SampleApp/CoreLib/UnusedUsings.cs`
already contains a CS8933 `using System;` at line 4, but the fixture asserts only `Contain`, never count 1.

*Positive control:* all 250 directives were removed one at a time and every project still compiled clean,
and 188/188 had byte-identical symbol bindings before and after. Harness sensitivity was validated by
perturbing the namespace name (broke 250/250). 250 findings in, 250 real compiler diagnostics matched —
none invented.

## F2 — SNP0006: DTO properties populated by a serializer, never read (49 of 70, 70 %)

The rule's own code acknowledges the gap: `FrameworkEvidenceIndex.cs:160` routes the residual to
SNP0005/0006. The residual is the whole problem here.

| evidence | flagged |
|---|---|
| `StandalonePriceService.cs:74,110,133` — `graphQLService.Query<AllStandalonePriceIdDataWithCustomQueryResponse>(…)` | `StandalonePriceResponse.cs:11,13,21,50,51,52,63,82,87` (9) |
| `ProductService.cs:30` — `Query<StandalonePriceResultsWrapper>(…)` | `StandalonePriceResults.cs:10,19,20,21,22,23,29,30` (8) |
| `ProductService.cs:40,96,129` — `Query<ProductPricingInfoQueryResponse…>` | `ProductResponses.cs:86,87,99,100,101,159,160,161` (8) |
| `OrderService.cs:68` — `Query<OrderVersionResponse>(…)`, only `.Order.Version` read | `OrderVersionResponse.cs:9` (1) |
| `ServiceHostHelper.cs:51` — `configuration.GetSection(…).Get<DataSourcesConfiguration>(…)` | `DataSources/WebApiDataSource.cs:8,9,14,15,16` (5) — config-bound POCO |
| `client.GetAsync<AppContentPage>(…)` in `RegisteredUserBasic.cs:20`, `GuestBasic.cs:29,80,97,120` | `Contracts/AppContentPage.cs:24,26,28,30,35,36,38,43,45,50` (10) — `[JsonPolymorphic]` contract |
| `IFeatureManager` used at `ArticleEventListener.cs:82` | `FeatureManager.cs:9` (1) — interface property, rescue is methods-only |
| `ArticleEventListener.cs:15`, `PricingEventListener.cs:39` override it | `StoreProductDataIngestor.cs:260` (1) — `abstract`, not `override` |
| `ServiceCollectionExtensions.cs:50` — `var (a, b) = milkrunCacheSetupBuilder.Build();` | `MilkrunCacheSetupBuilder.cs:30` — `Deconstruct` on a **struct** |
| MongoDB driver cursor contract | `PlcTransactionQueryServiceShould.cs:129` — external interface |

**Fixes, each narrow and determined:**

- *Serialiser-written DTO properties* — in `UnusedNonPrivateMemberAnalyser.cs:101`, extend
  `FrameworkEvidenceIndex.IsUsed`: a member is live if its containing type is ever a
  `Deserialize`/`Query<T>`/`FromJson`/`GetAsync<T>` type argument, or carries `[JsonPropertyName]` or is
  reachable from a member that does. `FrameworkEvidenceIndex.cs:160` already names this extension point.
- *Config-bound POCO* — route through the existing `ConfigurationBindingAnalyser.cs`: a property of a
  type reachable from a `.Get<T>()`/`Bind`/`Configure<T>` type argument is config-driven.
- *Interface rescue is methods-only* — `UnusedNonPrivateMemberAnalyser.cs:134` gates on
  `candidate.Symbol is IMethodSymbol method`; widen to `IPropertySymbol`/`IEventSymbol`
  (`FeatureManager.cs:9`).
- *Interface members are unreportable* — `IsCandidate` (`:225-228`) rejects any type that is an interface,
  so for an unused interface member the finding can only ever land on the implementation. When
  `HasUsedInterfaceContractAsync` finds the interface member unused, downgrade to Advisory and name it.
- *`Deconstruct` on a non-record* — `:236` exempts it only when `ContainingType?.IsRecord == true`.
  `MilkrunCacheSetupFinalised` is a primary-constructor struct, so `IsRecord` is false. Exempt on
  `Name == "Deconstruct"` plus a matching `out` parameter list.
- *`abstract` members* — `IsCandidate` (`:232-237`) excludes `IsOverride` but not `IsAbstract`; add it.
- *External interface implementations* — `FrameworkEvidenceIndex` should treat members implementing a
  non-project interface as live.

## F3 — SNP0018: `[Obsolete]` members that are live serialisation contracts (48 of 49, 98 %)

Two classes, both with the finding's own attribute text as evidence.

**F3a — serialised response DTOs (41).** Expression-bodied getters on ASP.NET response types that exist
solely to be written by the JSON writer. `src/Metro60.CommerceTools.Products/Program.cs:34-42` only adds
converters — no `DefaultIgnoreCondition`, no `IgnoreReadOnlyProperties` — so `[Obsolete]` does **not**
suppress serialisation.

```csharp
// src/Metro60.CommerceTools.Products/Models/ListingProduct.cs:77
[Obsolete("Not required for listing, but keeping it for backward compatibility")]
public string? StockStatus => string.Empty;
```
Shipped as `"stockStatus": ""` in every browse/search response, per the routes at
`ProductsRoutes.cs:47,65` returning `Ok<ResponseWrapper<…>>`. 32 such members across `ListingProduct.cs`
and its nested `ListProductRewardOffer`. Same in `ApiRequestResponseDtos.cs:44,46` (attribute literally
says *"referenced by frontend"*), `MyAccount.cs:19-20` (`CartTotal => null`), `CustomerAddressModel.cs:36-37`,
`ProductItem.cs` ×5.

The proof the rule is inconsistent is in the same file: `MyAccount.cs:18`'s `CartCount` is **not** flagged,
because a test reads it (`RegisteredUserBasic.cs:357,364`). The sibling property with no in-repo reader is
flagged, though the out-of-band client reads it just as surely.

**F3b — enum members in polymorphic message contracts (6).** `*CommandType` enums are Pub/Sub
discriminators whose readers accept **legacy integer ordinals**, so deleting a member renumbers every later
member and silently re-routes in-flight messages. The maintainers documented it:

```csharp
// src/Milkrun.Customers.Core/Commands/CustomerCommandType.cs:10
[Obsolete("Removing existing value will cause issues since we are using Enum int value in serialization.")]
SaveCustomerPurchaseHistory,
```

Hard evidence at `src/Milkrun.Core/Serialization/MessageJsonConverter.cs:22-31`:

```csharp
if (messageTypeProp.ValueKind == JsonValueKind.Number)
{
    var eventTypeInt = messageTypeProp.GetInt32();
    if (!Enum.IsDefined(typeof(TMessageType), eventTypeInt)) return null;
    messageType = (TMessageType)Enum.ToObject(typeof(TMessageType), eventTypeInt);
}
```

Plus reflection over the whole enum at `ProductCommandProcessor.cs:12` (`Enum.GetValues<ProductCommandType>()`).

**F3c (1) — a static extension class that *is* referenced.** `M60.Shared.Core/Extensions/StringExtensions.cs:3`
is reported unreferenced, but `CustomerExtensions.cs:85` calls `.ToMaskedCreditCardNumberString()`, a member
that exists only on that class, via `global using M60.Shared.Core.Extensions;`.

**Fixes:**
- `ObsoleteMemberAnalyser.EvaluateMember` — bail early for serialisation-contract members: a public
  get-only/expression-bodied property on a type reachable from a serialisation root. Snipper already has
  `FrameworkEvidenceIndex.cs` and `DocumentIdentifierIndex.cs` for exactly this resolution. Failing that,
  down-tier to Advisory with "may be part of a serialisation contract" instead of "has no references".
- Reject enum members outright (`IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum }`) in
  `IsEligibleKind` (`:174`), or gate on `Enum.GetValues`/`Enum.Parse` usage of the enum and emit Advisory
  with an ordinal-shift warning.
- For an `INamedTypeSymbol` static class, treat bindings to its *members* as references of the type.

## F4 — SNP0012: **WITHDRAWN — not a false positive** (29 of 30, 97 %)

> **Correction, recorded after implementation was attempted.** The claim in this section — that own-source
> usage is "the whole discriminator" — is **wrong**. Every one of these 29 findings is *true and
> actionable*: removing the direct edge leaves the resolved graph unchanged and the project compiles.
> Tested in an isolated project (see `plan_1_7_2.md` §3): drop the direct `Serilog` reference while the
> source still calls `Serilog.Log.Logger`, and `Serilog.Sinks.Console` supplies it — build succeeds.
>
> The rule's own contract already says so: *"a direct reference also documents intent and pins against
> upstream dependency changes, so the message names the providing parent and advises verification."* That
> is a Moderate advisory about latent fragility, correctly tiered. Suppressing it would have removed 29
> true positives. **Not shipped.**
>
> The 29 are reclassified as **true positives with weak advice**, not false positives — which makes the
> investigation's tally 236 FP / 536 TP rather than 265 / 482. The genuine weakness is *which* parent is
> acceptable: calling `ZiggyCreatures.FusionCache` removable because a *backplane* package happens to pull
> it in is technically true and practically bad counsel. That is a message-wording question, deferred.

`RedundantTransitivePackageAnalyser` only asks *"is another direct reference's transitive closure already
pinning this at ≥ the resolved version?"*. It then uses assembly usage **solely** to dedup against SNP0003
(`:106`). It never asks *"does this project's own source reference this package's assemblies?"*.

- `Refit` ×12 — flagged "already supplied transitively by `Refit.HttpClientFactory`" in
  `Milkrun.Integration.Uber.csproj:14` etc., while `Refit` is used directly in 3 source files in each.
- `Newtonsoft.Json` ×1 — `M60.BridgingServices.Core.csproj:21`, with `global using Newtonsoft.Json;` at
  `GlobalUsings.cs:74` and `JsonConvert.DeserializeObject<…>` at `GoogleService.cs:61`.
- `ZiggyCreatures.FusionCache` ×1 — the core abstraction flagged in favour of the *backplane* package,
  while `ServiceCollectionExtensions.cs:57` calls `.AddFusionCache(…)`.
- `Swashbuckle.AspNetCore.Swagger` ×3 (`app.UseSwagger()` at `Program.cs:80`),
  `Polly.Extensions.Http` ×3 (`HandleTransientHttpError()`), `Microsoft.AspNetCore.Mvc.NewtonsoftJson` ×2
  (`AddNewtonsoftJson()`), `Azure.Messaging.EventHubs` ×2, `DistributedLock.Redis` ×1,
  `Microsoft.IdentityModel.Tokens` ×1, `commercetools.Base.Client` ×1, `AutoFixture` ×1.

The gate I proposed, and rejected:

```csharp
// REJECTED - this suppresses true positives. "Available transitively" is not what
// makes a direct reference redundant; the resolved graph is unchanged either way,
// which is the rule's actual (and correct) argument.
if (usage.AssemblyNamesByPackage.TryGetValue(package.Id, out var asmNames)
    && asmNames.Any(usage.UsedAssemblyNames.Contains))
{
    continue;
}
```

That single condition silences all 29 and leaves the one real finding
(`M60.BridgingServices.Client.csproj:16`, `Microsoft.AspNetCore.Hosting` 2.3.0, zero hits for
`IWebHostBuilder|WebHost\.|GenericWebHost`). Note that finding also deserves a "remove as a set" caveat —
it sits in a hand-pinned 2.x block (`Mvc.Core` 2.3.0, `Server.IIS` 2.2.6) on a `net10.0` library.

*Positive control:* the transitive half was validated mechanically against `obj/project.assets.json` for all
30. The version claim is correct in every case; only the remediation is wrong.

## F5 — SNP0032: marker matching promotes non-defensive text (≈17 High)

Survives E2's fix. `DefensiveFixMarkers.IsFixShaped` matches bare words (`null`, `Count`, `Length`, `try`,
`IsNullOrEmpty`) against **raw added patch text**, and `CarriesCode` only asks whether a line contains a
letter — so comments and string literals are scanned. Git reports a moved or re-indented block as
removed+added, so relocating code containing `null` re-triggers the marker.

- **A pure reformat scored as a null guard** — `BatchEventHubMessageProcessor.cs:163`, commit `2292e17`:
  ```
  -        if (checkpoint != null) return checkpoint;
  +        if (checkpoint != null)
  +            return checkpoint;
  ```
  The sibling is already byte-equivalent.
- **A relocated options block** — `AzureEventHubDataListener.cs:32`, commit `76add0b`; the marker fired on
  `MaximumWaitTime = … : null` inside a moved initialiser.
- **A marker inside a string literal** — `EdrLoyaltyProgramOfferRealTimeService…Should.cs:545`, commit
  `a550072`: the marked line is a 1.5 KB JSON fixture and `null` comes from `"priceFamilyId":null`.
- **A marker inside a doc comment** — `src/Milkrun.Core/Instrumentation/Instrumentation.cs:45`: the
  marked line is `/// … (null = tag omitted)`.
- **The marker set matches its own method names** — `EnumerableExtensions.cs:5`: the single added line is
  `public static bool IsNullOrEmpty<T>(…)`, which self-triggers the `IsNullOrEmpty` marker.

**Fix:** (1) skip comment lines — extend `CarriesCode` to reject `//`, `/*`, `*` prefixes; (2) drop bare
`Length`/`Count`/`null` as sufficient markers and require a *shape* (`is null`, `?? throw`, `?.`,
`ArgumentNullException.ThrowIfNull`); keep `try`/`catch`/`finally` but require statement position;
(3) reject relocated hunks outright:

```csharp
public static bool IsRelocation(IReadOnlyList<string> added, IReadOnlyList<string> removed)
{
    var before = removed.Select(l => l.TrimStart()).ToHashSet(StringComparer.Ordinal);
    return added.Count > 0 && added.All(l => before.Contains(l.TrimStart()));
}
```

Pass it through `Evaluate` as a fourth suppressor next to `renameOnly`.

## F6 — remaining single-finding classes

- **SNP0003 (1)** — `M60.BridgingServices.Client.csproj:21` `Microsoft.AspNetCore.Mvc.NewtonsoftJson`.
  `UnreferencedPackageAnalyser.cs:135-144` builds `consumersClosure` from solution projects only, and
  `M60.BridgingServices.ProductsUp.WebHost.csproj` exists in the tree but is **absent from
  `MILKRUN.slnx`** (`:113-116`). Its only project reference is `M60.BridgingServices.Client`, and it calls
  `AddNewtonsoftJson()` at `ProductsUpWebHostStartup.cs:51-59`. **Fix:** feed
  `DetachedProjectScanner.FindUnattachedProjectFiles(rootDirectory, loadedProjectPaths)` (already exists,
  `DetachedProjectScanner.cs:10`) into the consumer closure; or downgrade SNP0003 to Moderate when the
  referencing project has detached `.csproj` descendants.
- **SNP0024 (1)** — `ArticleEventListener.cs:97`: a primary-constructor capture
  (`ILogger<…> listenerLogger`, read at line 100) is not surfaced as a field symbol by `GetSymbolInfo`,
  so `TighteningAnalyser.TryEvaluateCanBeStaticAsync`'s instance-state bail-out (`:238-247`) finds nothing
  and proposes `static` on a method that cannot be static. **Fix:** at `:202-206`, bail when the containing
  class has a primary-constructor parameter list and the body references one of those parameters —
  `FieldReferenceClassifier` already classifies captures.
- **SNP0031 (1 hard)** — the DI-registration collision in E1's evidence.
- **SNP0022 (1 duplicate emission)** — `CustomerService.cs:93` yields two byte-identical findings on one
  line holding two occurrences. Dedupe by `(rule, file, span)` and emit a column/span field.

---

# 3. Benign: real duplication, worthless to a human (53)

Not false positives, and worth keeping out of the FP count, but they are why the numbers look big.

- **42 × SNP0031** — 19 ASP.NET host / Swagger / DI bootstrap fragments (`Program.cs`,
  `*ServiceHostFactory.cs`, `*WebApplicationBuilderExtensions.cs`), 11 `LogEventIds` const tables, 5
  `IOptions` property lists, plus metric-attribute stacks, a hard-coded coordinate polyline, and two
  intentionally-parallel per-aggregate services.
- **11 × SNP0032** — real clones whose cited change is a feature or a rename, not a fix:
  `PaymentExtensions.cs:52` adds a new `HasFailedCharge`; `IOrderApi.cs:15` adds four new computed
  properties; `Instrumentation.cs:21` adds a new `[Counter]`. And `TaxInvoiceSenderService.cs:142` vs
  `TaxInvoiceBuilder.cs:90` is the same bundle filter refactored with a renamed local
  (`!isBundledRoot` vs `item.IsBundleRoot != true`) — genuinely parallel code that E2's fix would not
  remove, since that commit is a real modification.

**One SNP0032 High in this batch is a genuine inconsistency worth fixing:**
`src/Metro60.CommerceTools.MyProfile/Models/UpdateCustomerRequest.cs:9` (commit `0fb1790`, verified
`@@ -6,5 +6,10 @@` — a real modification) changed `DateOnly? DateOfBirth` to `string? DateOfBirth` plus a
parse-and-validate, while `src/Metro60.CommerceTools.Checkout/RequestDto/CustomerDetailsRequest.cs:1` still
declares `DateOnly?`. One copy hardened its input; the other did not. The finding is still partly wrong
though — it also names `src/Metro60.CMS.API/Model/Bloomreach/BloomreachImageSetData.cs:1`, a Bloomreach
image DTO, as a sibling, which is F-class shape collision.

---

# 4. What I verified myself

The 800 classifications came from five parallel source investigations. Two claims contradicted existing
code comments, so I re-derived them by hand before writing them here:

| claim | verified how | result |
|---|---|---|
| E1 — window never verified | read `Extend` at `DuplicateFragmentAnalyser.cs:356`; read the `length < WindowTokens` guard at `:254`; read `TokenShingleIndex.cs:193-198` | **confirmed** — `forward` seeds at 60 so offsets `[0,59]` are never compared; `length >= 60` always, so the guard is dead; the doc comment is wrong |
| E2 — creation commits | read `Evaluate`'s loop at `:486` and `SiblingWasFixedEarlier` at `:606-608`; ran `git show` on three cited SHAs | **confirmed** — no `OldCount` guard on the member side; all cited commits carry `new file mode 100644` and `@@ -0,0 +1,N @@`; a genuine modification (`0fb1790`) shows `@@ -6,5 +6,10 @@` by contrast |

Everything else in this document rests on the subagents' source reading and is labelled accordingly.
Spot-checks that agreed: `SendGrid` as a true positive despite a `SendGrid,` enum member at `Enums.cs:22`
that would fool a text matcher; the 8 SNP0004 findings confirmed by namespace and 17 distinctive type
names; `MyAccount.cs:18` `CartCount` correctly not flagged while `:19` `CartTotal` is.

---

# 5. Zero-FP rules — and three caveats

**234 findings across eleven rules with no false positives:** SNP0001 (15), SNP0004 (8), SNP0005 (7),
SNP0009 (54), SNP0010 (8), SNP0020 (2), SNP0021 (8), SNP0022 (19), SNP0025 (39), SNP0026 (12),
SNP0028 (62).

Three caveats, because "no FP" is not "sound reasoning":

**SNP0028 is right by coincidence three times over.** All 62 removals are legal — but only because
surrounding code happens to provide the binding:
- *extern aliases* (4 findings) — `ProductApi::Metro60.…ProductItem` is load-bearing: every
  `ProjectReference` in `Milkrun.IntegrationTests.csproj` carries `Aliases=`, so without an alias
  `Metro60.*` is unreachable (proved: dropping the alias gives `error CS0246`). It survives only because
  `GlobalUsings.cs:66` happens to hold an alias-qualified `global using` of the same namespace.
- *using-aliases* (13 findings) — `CoreInstrumentation` is an alias, not a namespace, and the message
  claims a namespace binding. Legal only because line 2 *also* has `using global::Milkrun.Core.Instrumentation;`.
- *mock expression trees* (11 of SNP0022's 19) — `_mongoClientMock.Setup(x => x.GetDatabase(name, null))`
  where `null` is a **matcher constraint**. Verified that Moq 4.20.70 and NSubstitute 5.1.0 both fill an
  omitted optional with the literal default, so removal is equivalent — but any mock library with
  "omitted = any" semantics would make these findings actively weaken the assertion.

**SNP0026's stated premise is false on 9 of its own 12 findings.** It says "the expression is already of
that type", but the cast is `(IOrder?)` over an `IOrder` — a *nullability* change, not a type-identity
change, and CS8604 proves it is observable. Safe here only because the repository returns `Task<IOrder?>`.
Fix: compare `NullableAnnotation` alongside type identity and emit a distinct message or suppress.

**SNP0028 also has false negatives.** `CTMilkrunCustomer.cs:915` carries two provably removable qualifiers
(`[System.Text.Json.Serialization.JsonSourceGenerationOptions(…)]`) that were not reported, while line 917
two lines below was. It never reports alias-qualified *member* access. Fix: also visit
`AttributeSyntax.Name` and `MemberAccessExpressionSyntax` over a `TypeSyntax` receiver.

**SNP0009 is 54/54 correct but 8 suggest the wrong fix.** `out var`, deconstruction elements and pattern
variables cannot be deleted — they must become `_`. e.g.
`AlgoliaSearchService.cs:80`, `VerificationService.cs:34`, `HtmlWidgetMapper.cs:12`. Emit a distinct
"replace with `_`" message and no delete fix for those forms.

---

# 6. Recommended order — and what 1.7.2 actually shipped

| # | Fix | FPs removed | Risk | Status |
|---|---|---|---|---|
| 1 | **E2** — skip file-creation hunks in `Evaluate` | 90 whole-report | small | **shipped in 1.7.2** |
| 2 | **F1** — dedupe SNP0019 per directive | 62 | tiny | **shipped in 1.7.2** |
| 3 | **F4** — invert the SNP0012 gate | 29 | — | **withdrawn: not a false positive** |
| 4 | **E1** — verify the window in `Extend` | 1 hard FP + a large share of the 53 benign | medium; will *reduce* SNP0031's count, so re-baseline | open |
| 5 | **F2 / F3** — serialisation-contract awareness for SNP0006/SNP0018 | 97 | large; needs the response-graph resolution to be principled | open |
| 6 | **F5** — comment/string-aware markers, relocation detection | ~17 | small–medium | open |

**Items 1 and 2 shipped**, together removing **152 findings** across the whole MILKRUN report
(6,812 → 6,660) with **zero additions** and no file gaining a finding. Item 3 was withdrawn on
evidence. The revised tally is therefore **236 false positives**, not 265.

Two corrections to this document, both found by implementing rather than reading:

- **Item 1's first patch (`OldCount == 0`) was wrong** and would have broken the rule — a guard
  inserted above a cloned block is also `@@ -N,0 +N,K @@`. The shipped fix keys on git's `new file
  mode` header via a new `PatchHunk.IsFileCreation`.
- **`SiblingWasFixedEarlier` was deliberately not changed.** Its `OldCount > 0` is not a coordinate
  proxy for creation; it is the exact expression of its stated intent ("only hunks that modify
  existing lines count"). Switching it to `!IsFileCreation` suppressed 28 findings whose commit
  genuinely modified the file. Measured, reverted, and the reasoning left in the code.

Item 4 is the one that changes an engine invariant and invalidates any SNP0031 baseline, so it should
ship on its own with a fresh baseline rather than bundled with anything else.

Two consequences worth stating before anyone acts on this:

- **1.7.1's SNP0031 tuning conclusion needs revisiting.** `plan_1_7_1.md` measured the fragment-length
  distribution and concluded no threshold could trim noise. That was sound reasoning about the *output*
  distribution, and E1 does not contradict it — but E1 means some reported fragments are hash collisions
  that were never duplication at all, which no output-distribution analysis could have detected. The
  "1.7.1 changed nothing" decision was right given the evidence available; the evidence was incomplete.
- **The 1.7.0 gate recorded SNP0032 High at 82 with 4 presentation defects fixed and "the other 78 not
  individually inspected".** E2 is what those 78 were hiding. The gate's own caveat is what surfaced it.
  Confirmed in 1.7.2: SNP0032 High fell 82 → 29 across the whole report.
- **A false claim in this document cost a near-miss.** F4 asserted that own-source usage is "the whole
  discriminator" for SNP0012, and it read as well-evidenced. Testing it showed the opposite, and the
  existing fixture (`App/Worker.cs:74`) had been asserting the correct behaviour all along. A finding
  class that is "true but the advice is weak" is not a false positive, and the distinction has to be
  settled by compiling something, not by reading the analyser.

## Method and limits

- **Sample is the first 800 in report order** (certainty, then path). Biased by construction; see the
  headline. Not a random sample and not representative of all 6,812.
- **SNP0032 verification ratios, explicitly:** 86/86 git claims verified mechanically (SHA validity, file
  existence, sibling-path existence, one-sidedness); 86/86 diff hunks read; **source read at both the
  changed copy and an unmodified sibling in 28/86 = 33 %** — exactly the non-creation-commit findings,
  where the premise is live and a semantic judgement is needed. The other 58 were judged on the
  `@@ -0,0 +1,N @@` header alone, since source reading cannot overturn that verdict; 8 were read anyway as
  a spot check with no disagreement.
- **One agent built a re-implementation of `TokenShingleIndex.Normalize`** to reproduce token runs. It
  matches reported fragment sizes where a real clone exists (112/112 on `Program.cs`) but is not Roslyn,
  so it under-reports on interpolated strings, verbatim literals and qualified names. Its 26
  unreproduced anchors were treated as leads only; a finding was called a hard FP only when source reading
  agreed. E1 makes that harness unnecessary.
- **Never read the contents of any `appsettings*.json`.** Section *names* were cited from C# binding code
  (`ServiceHostHelper.cs:51-52`) where relevant; no JSON was opened.
- **Coverage gap:** `src/M60.BridgingServices.ProductsUp.WebHost` is not referenced by `MILKRUN.slnx`, so
  its 10 `.cs` files were never analysed. No finding here depends on it, but it is the direct cause of F6's
  SNP0003 false positive and a real limit on any "whole repo" claim.
- **Two structural unknowns remain untestable on this repo:** no multi-targeted project exists (per-TFM
  `CollectFiles` divergence), and none of the 26 SNP0028 files contains conditional compilation (the `#if`
  shadowing branch). Neither FP class can be closed by testing here.
- **Nothing outside Snipper's own analysis was assumed.** Two SNP0006 true positives
  (`M60.BridgingServices.Core` consumed by another repository) cannot be *proved* false — the projects are
  plain `Microsoft.NET.Sdk` libraries with no `IsPackable=false`, so "unused within the analysed solution"
  is the strongest claim available.