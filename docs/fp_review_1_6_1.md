# FP Review — 1.6.1 (monorepo application report, 2026-09-20)

**Trigger:** an external agent applied 461 Guaranteed/High findings from the 1.6.0 monorepo report and build/test-verified them (dotnet build + 3,970 tests). Verdict: 52 findings (~11%) false positive or stale — 47 of them in a single broken rule category. This document records each claim's disposition and the 1.6.1 fix.

## FP-1 — SNP0003/SNP0004 transitive blindness (47 findings) → FIXED

**Claim:** the dependency rules evaluated usage per-project, ignoring that SDK-style references flow transitively — hub references unused in the declaring project but consumed downstream (e.g. `Google.Cloud.Diagnostics.AspNetCore3` declared in two hub projects, consumed via `global using` in ~20 downstream projects with no direct reference), and project references whose *transitive flow* is used (`Metro60.Streaming.API` → `Metro60.Delivery.Core`, where the consumed interface actually lives in `Milkrun.Delivery`, flowing through the reference).

**Verification:** all three proven chains reproduced from the report and sources. The "direct-use miss" sub-claim (`IDeliveryLiveTrackingDataService`) resolved as upstream transitive flow one level down — the interface is defined in `Milkrun.Delivery`, not `Metro60.Delivery.Core`.

**Fix (UnreferencedPackageAnalyser):**
- *Downstream consumer closure* — reverse transitive walk over the project-reference graph; a reference is suppressed when any consumer's used-assembly set intersects the reference's flow (package: own assemblies + exclusive subtree; project: full transitive flow). `PrivateAssets="all"` stops the flow: consumer usage is no evidence (parsed for both reference kinds; the direct consumer still counts, onward propagation stops).
- *Upstream exclusive flow* for SNP0004 — removing P→R evicts R's assembly plus the project/package assemblies flowing through it; when P uses a flowed assembly it cannot reach via any other reference (flows of P's other references + P's own direct-package reachable subtrees), the reference is load-bearing.

**Monorepo A/B:** SNP0003 31 → 16, SNP0004 16 → 10; all three proven chains suppressed. Remaining findings sampled: the 10 SNP0004 (all in `Milkrun.UnitTests`) have zero namespace usages in the tree and no test-hosting patterns; the 16 SNP0003 are explainable leftovers (shared-registration packages whose own reference is redundant).

## FP-2 — SNP0026 cast on a collection expression (1 finding) → FIXED

**Claim:** `(List<IProductType>)[.. a, .. b]` flagged as identity-redundant, but the cast IS the collection expression's target type (removal → CS9176).

**Verification:** reproduced in the fixture. The hole: `ClassifyConversion` on the *parenthesized* operand reports identity because parentheses inherit the converted type. The fixture negative test initially passed vacuously (message assertion missed the qualified type display) — a reminder that substring pins must match the qualified form.

**Fix:** both cast evaluators paren-unwrap the operand and suppress when the unwrapped operand has no natural type (collection expressions, target-typed `new`/conditionals, lambdas, method groups, null/default literals — every shape where the cast is structural). Monorepo: the site is no longer flagged.

## FP-3 — SNP0025 "wider notion of inferable" (3 findings) → MISDIAGNOSED, no rule change

**Claim:** explicit type arguments on `AddSingleton<IClient>(sp => { … })` and `EventHubMessageConsumer.Create<ArticleInventoryUpdated>(…, filter)` are not inferable; removal is a compile error.

**Verification:** replicated both exact shapes in an isolated project against the **real** Microsoft DI overload set (`fp3-repro`). Snipper flags both; **stripping the type arguments compiles cleanly** (0 errors). Output type inference flows from a block-bodied lambda's return expressions, and lower-bound inference applies through a concrete class's unique matching interface (`InventoryMessagesFilter : IEventHubMessageFilter<ArticleInventoryUpdated>` — exactly one). The observed compile errors were attribution fallout: the "does it compile" checks ran in a tree already broken by the 47 reference removals (91 pre-existing CS0234/CS0246). Corroborating: all three sites survive in the monorepo tree (unremovable at the time) and remain flagged in 1.6.1 — they can be stripped safely.

**Action:** no gate change. The two canonical shapes are pinned in the fixture as true-positive regression tests (`RegisterViaFactory` / `CreateViaUniqueInterface`) so a future "fix" cannot regress them into false negatives unnoticed.

## FP-4 — SNP0019 duplicate flagged asymmetrically (1 hazard) → FIXED (message)

**Claim:** a verbatim-duplicate `global using` was flagged only at its second occurrence; automation deduplicating by namespace removed both copies and broke the build.

**Verification:** the compiler (CS8019) flags exactly the removable occurrence — the finding was correct, the message indistinguishable from "unused".

**Fix:** when a CS8019-flagged directive has an identical earlier sibling (same name, global/static/alias shape), the message becomes "duplicates another using directive in this file — exactly one occurrence must remain."

## FP-5 — stale-snapshot findings (line drift, phantom symbols) → FIXED (report stamping)

**Claim:** findings referencing already-deleted code and off-by-one lines — the analysis snapshot had diverged from the working tree. (Root cause in that run: findings were applied over days against a tree being mutated by the application itself.)

**Fix:** the JSON report is now a stamped envelope: `toolVersion`, `generatedAtUtc`, `commitSha` (best-effort `git rev-parse HEAD` in the target directory; a dirty tree prints a console warning), and a `findings` array whose entries carry `lineText` — the trimmed source line at the finding's location, so consumers can mechanically verify before applying. SARIF regions carry the same text as `snippet`. Missing files/lines degrade to omission, never failure. **Schema note:** the JSON report changed from a bare array to the stamped envelope (additive for envelope-aware consumers; the 1.5.2 report-exclusion sniff still matches `ruleId` inside).

## Validation

252/252 suite (14 new tests/pins), dogfood 0, fixture2 pins 23/25, monorepo A/B above. Residual documented risks: runtime-only package consumption (e.g. MVC formatter registration) remains symbol-invisible by design at the High tier; consumer-usage over-approximation can suppress a genuinely removable reference (false-negative direction, per doctrine).
