# Phase 3 — Wave 4 Spec: Framework evidence (SNP0005/0006 false-positive reduction)

**Target release:** 1.5.0 · **Roadmap:** [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md) · **Baseline:** 1.4.4 (22 rules, 191 tests)
**Status:** ✅ **SHIPPED 2026-09-19 as 1.5.0** — 209/209 tests (18 new), fixture2 repinned 23/25 (verified), dogfood clean. **Monorepo A/B vs 1.4.4 (user's MILKRUN.slnx, same exclusions): SNP0006 1,515 → 1,090 (−425, −28%)**; every suppressed finding sits in the verified data-contract classes (BwsOrderRequest, Refit response graphs, GraphQL response DTOs, health-check implementations); all other rules count-identical except SNP0023 0 → 2, which is **parity with 1.4.1/1.4.3** — the 1.4.4 "0" was self-suppression via a stale report JSON sitting in the analysis root (string-name evidence reads it). Remaining findings spot-checked and individually explainable (genuinely dead `BwsOrderResponse`, dead test builders, non-record struct `Deconstruct`, closed-world `MilkrunCustomer` members). Two implementation-time fixes beyond the spec: (1) the DTO closure must expand generic type arguments *before* definition normalisation (`Task<Dto>`) and only source types enter the closure; (2) `IsUsed` must handle *type* candidates (their `ContainingType` is null — e.g. `AbstractValidator`-derived classes, registered types themselves). F3's record fix landed as a record-protocol `Deconstruct` exemption plus the `IsImplicitlyDeclared` filter; positional-record members were never candidates (no syntax nodes), so the fixture pins the *explicit* `Deconstruct` shape that actually occurred on the monorepo.

No new rules. This wave removes the dominant false-positive classes observed on the user's monorepo (MILKRUN.slnx, ~180 projects) by teaching SNP0005/0006 to recognise members that frameworks — not C# code — invoke: serializers, model binders, DI-activated contracts, compiler-synthesized record members. Doctrine: **err toward "used"**; evidence is never a finding; syntax pre-filters before any semantic call.

## Verified false-positive classes (read on C:\ws\milkrun, 2026-09-19)

| # | Class | Verified example | Available syntax evidence |
|---|---|---|---|
| F1 | Serialization DTO members (init/get-set props, computed wire props like `Type => "listings"`) | `BwsOrderRequest` props ([FromBody]-bound), `SponsoredListingAuctionApiResponse.Winner.Type` (Refit response), `GetOrdersQuery` GraphQL response DTOs | `[JsonSerializable(typeof(T))]`, Refit attributes, `[FromBody]`, serializer call sites |
| F2 | Framework-invoked interface implementations | `LaunchDarklyHealthCheck.CheckHealthAsync` (`AddCheck<T>` at registration), `IFusionCacheSerializer.SerializeAsync/DeserializeAsync` | registration call shapes |
| F3 | Record synthesized members | `Deconstruct` on positional records (`MilkrunCacheSetupBuilder`, `StoreUpdateResult`) | `IsImplicitlyDeclared` |
| F4 | Convention/framework base-class members | `*Middleware.InvokeAsync`, FluentValidation `AbstractValidator<T>`-derived public types, OpenAPI `Apply`/`TransformAsync` implementations | naming/base-type conventions + registration shapes |

Guaranteed + High tiers verified clean (counts match 1.4.1 baseline exactly; spot-checked true positives). Test-builder `WithX` methods flagged are **true positives** (genuinely uncalled fluent helpers) — out of scope.

## Design

### New shared artifact — `Analysis/FrameworkEvidenceIndex.cs`

Solution-keyed memoized index (same `ConditionalWeakTable<Solution, Lazy<…>>` pattern as `SolutionUsageIndex`). Build once per solution:

1. **Syntax seed pass** (per document, syntax only — no semantic calls; may parallelise):
   - `[JsonSerializable(typeof(T))]` attribute (name match `JsonSerializable`/`JsonSerializableAttribute`) → T.
   - Serializer generic call sites: method name ∈ {Deserialize, Serialize, DeserializeAsync, SerializeAsync} with receiver text ending `JsonSerializer`/`JsonConvert`; method name ∈ {ReadFromJsonAsync, GetFromJsonAsync, PostAsJsonAsync, PutAsJsonAsync} (any receiver); `RegisterClassMap` with receiver ending `BsonClassMap` → each generic type argument.
   - Refit interfaces: interface declarations whose methods carry attributes named exactly `Get`/`Post`/`Put`/`Delete`/`Patch`/`Head` (ASP.NET uses `HttpGet`-prefixed names — no collision) → every such method's parameter types + return type.
   - ASP.NET binding: parameters with attribute ∈ {FromBody, FromForm} → parameter type.
   - GraphQL response contracts: type arguments of base-list interfaces named `IGraphQueryRequest`/`IGraphQLRequest` → type argument.
   - Registration call shapes (registration set, not serialization): method name ∈ DiRegistrationScanner's set ∪ {AddCheck, AddTypeActivatedCheck, AddRefitClient, AddExceptionHandler, AddSchemaFilter, AddDocumentTransformer, AddOperationTransformer, AddSchemaTransformer} with a generic type argument or `typeof(T)` argument → T.
2. **Semantic resolution** (sequential binding; one semantic model per seed-bearing document): `GetTypeInfo` each seed TypeSyntax → `INamedTypeSymbol` (OriginalDefinition).
3. **DTO closure** (BFS from resolved seeds): enqueue public instance property/field types and all generic type arguments (OriginalDefinition); skip primitives, type parameters, error types; visited set = the closure. Handles `Task<Dto>`, `IApiResponse<Dto>`, nested response graphs (Winner, AuctionResult) transitively.

Products: `SerializationUsedTypes : FrozenSet<INamedTypeSymbol>`, `FrameworkRegisteredTypes : FrozenSet<INamedTypeSymbol>`.

Query — `IsUsed(ISymbol member)`:
1. Member-level serialization attribute (name ∈ {JsonPropertyName, JsonProperty, JsonInclude, JsonConstructor, JsonExtensionData, BsonElement, BsonId, BsonDiscriminator, DataMember, ProtoMember, XmlElement, XmlAttribute}) → used.
2. `member.ContainingType.OriginalDefinition ∈ SerializationUsedTypes` → used.
3. `ContainingType ∈ FrameworkRegisteredTypes` AND member implements an interface member (explicit impl non-empty, or `FindImplementationForInterfaceMember` match over `AllInterfaces`) → used.
4. Middleware convention: method named `Invoke`/`InvokeAsync` on a class whose name ends `Middleware` → used.
5. Type derives (any depth) from a type named `AbstractValidator` → used (FluentValidation assembly scanning).

### `UnusedNonPrivateMemberAnalyser` changes

- **`IsCandidate`**: also exclude `IsImplicitlyDeclared` methods/properties (record `Deconstruct`/`PrintMembers`/equality members, positional-record properties). Types were already excluded. (F3)
- **Evidence pre-check** immediately after exclusions, *before* the usage-index/FindReferencesAsync path: `if (FrameworkEvidenceIndex.Get(solution).IsUsed(symbol)) continue;` — cheap suppression that also skips the expensive reference search (perf side-benefit on DTO-heavy solutions).

### Out of scope (v1)

SNP0001 private members ([JsonInclude] private setters — Guaranteed tier verified clean); SNP0023 hierarchy; controllers' action methods (none in the dump); wholesale metadata-interface rescue (too broad — e.g. `IDisposable.Dispose` on a dead class must still flag); .resx/XAML reachability (roadmap M–L).

### New files / changes

| File | Content |
|---|---|
| `Analysis/FrameworkEvidenceIndex.cs` | Seed pass + resolution + closure + `IsUsed` (above). |
| `Analysis/UnusedNonPrivateMemberAnalyser.cs` | `IsCandidate` implicit-declaration filter; evidence pre-check. |
| `test/.../CoreLib/FrameworkPatterns.cs` | NEW fixture file (never `DeadCode.cs`): locally-defined attribute/framework stand-ins per working agreements. |
| `test/.../UnusedNonPrivateMemberAnalyserShould.cs` | New tests (below). |

## Test plan

Fixture scenarios (distinctive names; substring assertions; stand-in attribute classes `JsonSerializableAttribute`, `PostAttribute`, `FromBodyAttribute`, `JsonPropertyNameAttribute`; stand-in static `JsonSerializer`, `HealthCheckRegistration.AddCheck<T>()`, `AbstractValidator<T>`):

1. NotFlag: `[JsonSerializable(typeof(Dto))]` DTO with an unread property (incl. nested DTO reached only through the property graph).
2. NotFlag: Refit interface (`[Post]`) request + response DTO members, incl. computed wire property.
3. NotFlag: `[FromBody]`-bound model members.
4. NotFlag: `JsonSerializer.Deserialize<Dto>` / `ReadFromJsonAsync<Dto>` DTO members.
5. NotFlag: property with `[JsonPropertyName]` stand-in.
6. NotFlag: interface-implementing members of `AddCheck<T>`-registered type; **Flag**: non-interface members of the same registered type (they're not framework-called).
7. NotFlag: positional record (Deconstruct + positional properties absent from findings).
8. NotFlag: `AuditMiddleware.InvokeAsync`; `OrderRequestValidator : AbstractValidator<…>` type.
9. Flag (negative controls): an unregistered DTO with zero evidence; a plain unused public method — evidence must not blanket-suppress.

Gates: full suite green; dogfood 0; fixture2 pins **23/25**; then **monorepo A/B** (user's MILKRUN.slnx, same exclusions as 1.4.4 run): SNP0005/0006 count drops sharply (DTO flood recedes); Guaranteed + High counts **identical** to the 1.4.4 run (207/55/19/47/49/72-family); no finding class entirely new.

## Risk register

1. **Over-suppression** (worst): a registration shape too broad hides real dead code. Mitigation: evidence is name-set-scoped and per-shape; negative-control tests 6/9; monorepo A/B requires remaining findings to be individually explainable.
2. Attribute-name collisions (fixture stand-in `PostAttribute` vs ASP.NET `[HttpPost]`): exact-name sets (`Get`/`Post` bare = Refit; `HttpGet` family untouched).
3. Closure explosion on pathological generic graphs: visited-set bounded; primitives/type-parameters excluded.
4. Perf regression on small solutions: one extra syntax pass + lazy semantic resolution; budget +1s self-run (the 1.4.2 lesson: measure, and the suppression also *skips* FindReferencesAsync calls — net win expected on DTO-heavy solutions).
