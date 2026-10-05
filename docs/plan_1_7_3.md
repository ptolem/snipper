# Plan - Snipper 1.7.3: verify the clone window, and repair a test fixture that never compiled

## Why 1.7.3 and not 1.8.0

Two defects, both found by the 1.7.1 false-positive investigation, plus the repair of the fixture
that investigation exposed. One is a correctness defect in the clone engine; the other is a testing
defect that had been hiding analyser behaviour. No rule is added, removed or re-tiered.

---

# 1. E1 - `Extend` never verified the 60-token window it was handed

## The defect

`DuplicateFragmentAnalyser.FindFragments` pairs two windows that landed in the same shingle bucket
and hands the pair straight to `Extend`. `Extend` began:

```csharp
var forward = WindowTokens;                                    // 60
while (leftStart + forward < left.Count
    && rightStart + forward < right.Count
    && string.Equals(left[leftStart + forward], right[rightStart + forward], …))
```

It only ever compared offsets **≥ 60**. The window itself - offsets `[0, 60)` - was never compared.
So the premise "these two windows matched" was assumed, not proven, and the bucket key is FNV-1a/32.

Three consequences, all now fixed:

1. **A hash collision became a 60-token clone.** At ~10⁶ windows the birthday bound puts collisions
   in the hundreds.
2. **`if (length < WindowTokens) continue;` at `DuplicateFragmentAnalyser.cs:254` was dead code.**
   `Extend` returned `forward + Math.Max(leftBackward, rightBackward)` with `forward >= 60`
   unconditionally, so `length` was never below 60. It is reachable again now.
3. **`TokenShingleIndex.Hash`'s documentation was false.** It claimed *"every candidate is
   re-verified token by token during extension, so a collision costs time and never correctness."*
   It did not cost time. It cost correctness. The comment now says so, and says not to optimise the
   verification away.

## Caught in the wild, before the fix

1.7.2 reported this pair as a 60-token clone across 14 lines:

- `src/M60.BridgingServices.Client/DataIngestion/EventHub/Model/EventHubPartitionStats.cs:34` -
  a partition-stats `ToString()` interpolating partition and batch counters.
- `test/Milkrun.UnitTests/Web/BFF/Metro60.Rewards.API/Services/EdrNZServiceShould.cs:744` -
  `var response = Substitute.For<Refit.IApiResponse>();`

An NSubstitute mock setup and an interpolated `ToString()`. They share no tokens; they share a
32-bit hash. 1.7.3 does not report it.

## The fix

`Extend` proves the window before seeding anything, and bounds-checks rather than trusting its
caller:

```csharp
if (leftStart < 0
    || rightStart < 0
    || leftStart + WindowTokens > left.Count
    || rightStart + WindowTokens > right.Count)
{
    return (leftStart, rightStart, 0);
}

for (var offset = 0; offset < WindowTokens; offset++)
{
    if (!string.Equals(left[leftStart + offset], right[rightStart + offset], StringComparison.Ordinal))
    {
        return (leftStart, rightStart, 0);
    }
}

// Verified, so the window is earned rather than assumed.
var forward = WindowTokens;
```

Genuine clones are unaffected by construction: an equal window still passes and still extends.

## Measured, before and after

Identical flags, identical tree, packaged 1.7.2 vs this branch.

| | 1.7.2 | 1.7.3 | delta |
|---|---|---|---|
| SNP0031 | 4,476 | **4,244** | −232 |
| SNP0032 | 213 | **177** | −36 |
| total | 6,893 | **6,625** | −268 |

- **232 SNP0031 findings removed, 0 added.** 192 files shrank, **0 files grew**.
- SNP0032: 36 files shrank, **0 grew**. Its 5 additions are relocations inside files that lost a
  finding - clone sets are derived from SNP0031, so fewer fragments means different sets.
- **No other rule's count changed at all.**
- Wall clock 213.8 s → 228.9 s (**+7%**). The verification is 60 comparisons per candidate pair,
  which sounds expensive and is not: it short-circuits on the first mismatch, and most pairs differ
  early.

> **Baseline caveat, because it changes how the headline should be read.** An earlier 1.7.2 run
> measured **6,660**; this one measured **6,893**. `MILKRUN.slnx` carries uncommitted changes and Snipper
> warns about drift on every run. The two 1.7.2 numbers are not comparable, and neither is against
> 1.7.1's 6,812. Only the same-tree, minutes-apart pair above is a valid A/B. Absolute totals on this
> target are only meaningful within a single session.

---

# 2. The SampleApp fixture had never compiled

## Why nobody noticed

Snipper is a linter: it reports findings rather than requiring a clean build. Every analyser test in
the suite reads `test/Snipper.Tests/TestAssets/SampleApp`, and none of them asserted that it builds.
Semantic models were partially unbound under a compile error, so tests passed against *partial*
information - and assertions written against the intended shape quietly tested something else.

The fixture carried **eight distinct defects / eleven diagnostics**:

| | Defect |
|---|---|
| CS0101 | `JsonIncludeAttribute` declared in both `PrivateSerializationPatterns.cs:9` and `WriteOnlyFields.cs:6` |
| CS0122 ×5 | `internal` CoreLib fixtures named from `App/Worker.cs`: `InternalRegisteredService`, `CanBePrivateScenarios`, `CanBePrivateConsumer`, `FamilyRoot`, `FamilyDerived`, `EventScenarios`, `EventConsumer` |
| CS8209 | `_ = new LegacyHelper().StillUsedApi();` - a `void` call assigned to a discard |
| CS1061 | `Serilog`'s `Console()` sink is an extension method; `Worker.cs` had no `using Serilog;` |
| CS0136 | `LocalShadowingPatterns.OuterShadowedIsUnused` shadowed a local in a nested block, which C# forbids outright |
| CS0117 | `JsonRoundTrip` wrote `JsonSerializer.Serialize`, which bound to the `CoreLib.JsonSerializer` **stand-in** rather than `System.Text.Json.JsonSerializer` - the enclosing namespace beats a `using` |
| CS0841 ×2 + CS0029 | `DeadCode.LocalHelper` used `_ = …` as a discard in the same scope as a real `var _ = 5;` local; a lone `_` is a discard only while no such local is in scope |
| CS0266 | `RedundantTypeArgs` returned a `long` from an `int` method |

## The trap worth recording: `InternalsVisibleTo` is the wrong fix

The obvious repair for the five CS0122s is `<InternalsVisibleTo Include="App" />` on CoreLib. It
compiles, and it is **wrong for this fixture**.

`HierarchyDeadCodeAnalyser` demotes every finding on a type that has friend assemblies from Moderate
to Advisory, on the reasoning that external code could then inherit from it:

```csharp
return hasFriendAssemblies || IsOnExportedType(symbol) ? CertaintyTier.Advisory : CertaintyTier.Moderate;
```

Adding the friend assembly silently rewrote the expected certainty of the SNP0023 and SNP0005
scenarios across the whole project - **five existing tests failed**, each asserting `Moderate`, each
now correctly `Advisory`. The analyser was right; the fixture had been changed underneath it.

Making the types `public` instead is no better: "internal, not on exported API surface" is precisely
what several of them exist to test, and `IsOnExportedType` demotes public types too.

So the cross-assembly reach goes through `CoreLib/InternalFixtureBridge.cs` - public entry points that
call the internals - and CoreLib keeps **no friend assemblies**. That also follows the roadmap's
standing rule that fixture scenarios go in new files.

**Cost of getting this wrong, recorded:** the friend-assembly version passed 5 of the 6 failures it
caused by looking reasonable. It was reverted, and the reasoning is now in the bridge's own header so
the next person does not re-try it.

## A scenario that cannot exist in C#

`OuterShadowedIsUnused` needs an inner local shadowing an outer one, with the inner one read - the
property that forces `UnusedLocalVariableAnalyser` to bind rather than match text. That shape is
**CS0136 in every form**: nested block, `for`, `foreach`, `using`, `catch`, lambda parameter, and
`out var`/`is var` (CS0128). Of nine shapes tried, exactly one compiles - a **local function's
parameter**:

```csharp
var shadowed = value;                        // line 17, never read
int Inner(int shadowed) => shadowed;         // reads the parameter
return Inner(value * 2);
```

Semantically identical to the intent, and the outer local stays on **line 17** because
`Flag_The_Outer_Local_When_An_Inner_Local_Shadows_Its_Name_For_AnalyzeAsync` pins that line.

## The guard

`FixtureBuildShould` is the only test in the suite that asserts the fixture *builds*, and it is
deliberately the broadest assertion here:

```csharp
errors.Should().BeEmpty("every analyser test in this suite reads this fixture");
```

It found the CS0101 first, and then surfaced five more defects one at a time - which is exactly how a
single "the fixture is broken" report would have been much slower to diagnose.

## Outcome

**581 tests pass, and no existing assertion needed changing.** That is the load-bearing result: the
fixture now compiles and every fixture-driven expectation still holds, so the six failures seen
mid-way were caused solely by the friend assembly and not by the repairs. The repairs are
behaviour-preserving by measurement rather than by assertion.

---

# 3. Release gate

- **581 tests pass** (572 at 1.7.2, +9: 8 window-verification cases and `FixtureBuildShould`).
- `TokenShingleIndex.Hash` and `DuplicateFragmentAnalyser.Extend` went `private` → `internal` for
  testing; `InternalsVisibleTo("Snipper.Tests")` already existed.
- The window guard is **mutation-checked**: removing the verification loop turns all 6
  window-verification tests red, including one that brute-forces a genuine FNV-1a/32 collision
  (found at ~130k trials) and asserts the two colliding windows are rejected.
- A/B above: 152 fewer findings, **0 added**, no file gained one, no other rule moved.
- Pack + global tool install + self-run.

# Not in 1.7.3

From [`1_7_1_false_positives_investigation.md`](1_7_1_false_positives_investigation.md), unchanged:

1. **F2 / F3** - serialisation-contract awareness for SNP0006 (49) and SNP0018 (48). The largest
   remaining class, and the only one needing response-graph resolution.
2. **F5** - `DefensiveFixMarkers` matches comment and string-literal text, and does not detect
   relocated hunks (~17 High).
3. **F6** - the four single-finding classes, including SNP0003's consumer closure ignoring detached
   `.csproj` descendants.
4. **Normalisation is too aggressive, and this is not a bug** - identifiers collapse to `ID`, so a
   DI registration list, a `const EventId` table and a DTO property list all normalise to the same
   tokens and match exactly. That is why 42 of the 4,373 SNP0031 findings were real-but-worthless
   bootstrap and const tables, and **no threshold removes them**. The remedy is carrying identifier
   spellings alongside the normalised stream and requiring overlap - a contract change to the rule,
   so it is a plan of its own rather than a patch. See the correction in the investigation's E1
   section, which originally mis-attributed that example to hash collisions.
