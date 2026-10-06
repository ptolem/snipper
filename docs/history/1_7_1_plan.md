# Plan — Snipper 1.7.1: unopenable-target handling, and an SNP0031 tuning pass measured as a no-go

**Status: IMPLEMENTED** (2026-10-05). A patch release, one behavioural fix and one documented
non-change. Follows [`1_7_0_plan.md`](1_7_0_plan.md), which recorded both of these as open.

**567 tests green** (565 at 1.7.0 + 2). Dogfood on `Snipper.slnx` at the 1.6.3 baseline of 3
pre-existing findings, zero contributed.

## Why 1.7.1 and not 1.8.0

Neither item adds a rule, an option, or a report field. The `.slnx` fix changes only what happens when
the tool is handed a target it cannot open — an error path, not a capability. There is nothing a user
could newly do, so a minor bump would overstate it.

---

# 1. A malformed `.slnx` crashed the process

## What `1_7_0_plan.md` said, and why it was half right

The 1.7.0 plan recorded this as a known follow-up, in these words:

> Snipper surfaces a raw unhandled `System.Xml.XmlDocument` stack trace when handed a malformed `.slnx`
> (hit by hand-writing one). There is no try/catch around `OpenSolutionAsync`, so bad input produces a
> crash dump instead of a diagnostic.

**"There is no try/catch around `OpenSolutionAsync`" was wrong.** There was one, at
`CliRunner.cs:102`, and it returned exit 1 as documented. What it had was a *closed type filter*:

```csharp
catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException
    || ex.GetType().Name is "InvalidProjectFileException" or "InvalidSolutionFileException")
```

The misdiagnosis is worth recording, because the two situations call for different fixes. "No
try/catch" implies adding one. "A try/catch that does not cover this case" implies the filter has to be
widened — and widening a closed list is a losing game, because each new exception type from a
third-party dependency is one more thing to remember.

## Root cause

`System.Xml.XmlException` derives from `System.SystemException`, **not** `System.IO.IOException`, so it
satisfies none of the filter's clauses. It is thrown from a genuinely unrelated layer:
`Microsoft.VisualStudio.SolutionPersistence` 1.0.52 constructs a `LineInfoXmlDocument` and calls
`Load(stream)` in the `.slnx` serializer's `Reader` constructor. `SingleFileSerializerBase.OpenAsync`
wraps only the file handle in a `using` and adds no try/catch, and Roslyn's `SolutionFileReader` does not
wrap either. So the exception propagates untouched out of `CliRunner.RunAsync`, through `Program.Main`
(which has no handler and no `AppDomain`/`TaskScheduler` hook), and the runtime prints the stack trace
and aborts.

**Two further shapes were reachable through the same line and equally unhandled:**

- `SolutionException` — a `.slnx` that is *well-formed XML with an invalid schema*: wrong root element, or
  a bad project `Type` GUID. Valid XML is not a valid solution. Thrown from `SlnxFile.ToModel`.
- `InvalidDataException` — a `.slnf` filter naming a solution that is not there.

None of these are IO types either.

## Measured, before and after

Reproduced against the **installed 1.7.0 tool**, not a local build:

| Target | 1.7.0 | 1.7.1 |
|---|---|---|
| malformed `.slnx` (unterminated element) | exit **-532462766**, `Unhandled exception. System.Xml.XmlException…` | exit **1**, `Error: Failed to open '…': Unexpected end of file…` |
| well-formed XML, wrong root element | unhandled `SolutionException` | exit **1**, same diagnostic shape |
| missing target file | exit 1 | exit 1 (unchanged) |

`-532462766` is `0xE0434352`, the CLR's unhandled-exception code. A pipeline checking `exit -eq 1` was
seeing neither success nor the documented failure.

## The fix

`CliRunner.cs:102-136`. Two changes, both deliberate:

**Catch `Exception` at this boundary, rethrowing cancellation first.** The `try` block contains a
dispatch ternary and two `await` calls into MSBuild/SolutionPersistence — and no Snipper logic. There is
no defect of ours for a broad catch to hide, so the boundary is the right place to be generous, and it is
future-proof against new exception types from a dependency whose surface we do not control.
`OperationCanceledException` is rethrown ahead of it so Ctrl+C is not misreported as a bad solution file.

This is a deliberate asymmetry with `ProjectFileReader.cs:73`, which catches `XmlException` *explicitly*.
That is correct there and wrong here: `ProjectFileReader` loads one known file, so an explicit list is
knowable; the open call is a boundary into three assemblies, so an explicit list is not.

**`Markup.Escape` both interpolations.** This was a latent second bug on the same line. An
`XmlException` message routinely ends `[at line 3, position 12]`, and unescaped brackets are parsed as
Spectre markup — so the diagnostic aimed at fixing a crash could itself throw or mangle output. Neither
the path nor the message was escaped before.

`InvalidProjectFileException` and `InvalidSolutionFileException` are no longer matched by name, since the
broad catch subsumes them. The reason they were matched by name — living in runtime-resolved MSBuild
assemblies rather than being compile-time references — is retained in `code_architecture.md`, because it
is why the list could not confidently be widened type by type.

## Verification

Two tests in `CliRunnerShould`, both in the existing `[Collection("CliRuns")]` so they serialise against
the other CLI-driving tests. Both were confirmed to **fail against the old filter and pass against the
new one**:

- `Return_One_Rather_Than_Throwing_When_The_Solution_File_Is_Malformed_Xml_For_RunAsync`
- `Return_One_Rather_Than_Throwing_When_The_Solution_Has_An_Invalid_Schema_For_RunAsync`

They assert `NotThrowAsync` rather than the exit code alone, because an unhandled exception also exits
non-zero — just the wrong way. Asserting `Be(1)` would have passed against the bug.

**A third test was written and deleted.** An attempt to pin the cancellation rethrow through
`CliRunner.RunAsync(string[])` could not work: that overload takes no `CancellationToken`, so the test
could not have exercised the code it claimed to. A test that cannot fail is worse than no test, so it is
recorded here rather than left in the suite pretending to be coverage. The rethrow is correct defensive
practice and is currently untested for that reason.

## Docs

`code_architecture.md`'s claim that "`OpenSolutionAsync`/`OpenProjectAsync` failure itself is fatal
(exit 1)" was aspirational rather than descriptive before this fix. It is now true, and the section
documents the broad catch and why it is safe there. `README.md` and `usage.md`'s exit-`1` rows now
enumerate the newly-covered cases instead of saying "unopenable solution".

---

# 2. SNP0031 volume — measured, and deliberately not changed

`1_7_0_plan.md` recorded 4,373 SNP0031 findings on `MILKRUN.slnx` across 747 files, median fragment 76
tokens, 54 % at or below 80 tokens, and said the rule "wants its own tuning pass". This section is that
pass. **It concluded there is nothing to tune, and the count was left alone.**

Every number below comes from one `--duplicate-detection` run over the monorepo with the
owner-specified exclusions, parsing the `N-token fragment (M lines) duplicated K time(s)` message that
all 4,373 findings already carry. The threshold curve was then computed offline from that one run rather
than by re-running the analyser per candidate setting.

## Hypothesis 1 — a low-signal tail below some line count. Rejected.

`MinimumFragmentLines` is 4. The line distribution is a smooth curve peaking at **10–13 lines**, not a
curve with a spike near the floor:

| lines | findings | share |
|---|---|---|
| 4 | 55 | 1.3 % |
| 6 | 241 | 5.5 % |
| 8 | 237 | 5.4 % |
| 10 | 364 | 8.3 % |
| 12 | 281 | 6.4 % |
| 15 | 268 | 6.1 % |
| 20 | 65 | 1.5 % |

Raising the floor to 6 removes 3.3 % of findings. To remove 25 % you must go to 10 lines — at which point
you are deleting the mode of the distribution, not a tail.

## Hypothesis 2 — a mass of fragments barely exceeding the 60-token window. Rejected as a lever.

Token distribution: min 60 (the window floor), p25 67, median 76, p75 109, p90 154, max 1,674.

- exactly 60 tokens: **348** findings (8.0 %)
- ≤62 tokens: 653 (14.9 %)
- ≤70 tokens: 1,781 (40.7 %)

8 % at the floor is the entire harvest available, and the floor cohort is not obviously junk — a
fragment that is exactly the window length is the *minimum detectable* duplication, which is what the
threshold promises to report. Raising the window to 80 would cut 54 % of findings by discarding the bulk
of what Type-1/Type-2 detection legitimately finds in a codebase with copy-pasted DTO layers.

## Hypothesis 3 — noise concentrated in generated code. Rejected outright.

**Zero** findings in any path matching `.g.cs`, `.designer.cs`, `.generated.cs`, `obj/`, `bin/`,
`AssemblyInfo.cs` or `GlobalUsings`. All 4,373 are in 747 ordinary hand-written files. A generated-file
suppression filter, the obvious thing to reach for, would remove nothing.

## What actually drives the volume

The rule emits **one finding per occurrence**, not per clone set. This is documented behaviour
(`DuplicateFragmentAnalyser`: "One Advisory finding is emitted per occurrence"), and the message's own
`K` shows the scale:

- Σ K over all findings = **134,675** occurrences behind 4,373 findings
- size-weighted mean set size = 134,675 / 4,373 ≈ **30.8** occurrences
- 1,424 findings come from 2-copy sets; 661 from sets of ≥50; 524 from sets of ≥100; 2 from a 250-copy set

So most findings are not near-duplicate pairs — they are members of large copy-paste families. The count
is a direct consequence of the granularity choice.

## The lever that does exist, and why it was not pulled here

Emitting **one finding per clone set** instead of per occurrence. Estimating from Σ K and the size
histogram, that is roughly **4,373 → ~1,170 findings, about −73 %**, with no duplication information lost
— the set is still fully described.

It is not a tuning knob, and that is the problem. It changes the rule's contract, and everything
downstream of granularity inherits the change:

- **Location precision is lost.** Today each finding anchors at a specific file and line, which is what
  makes it actionable. A set-level finding must list its members in prose or lose navigability.
- **Baseline churn inverts.** Adding a 5th copy today *adds a finding*; under set-level reporting it
  *modifies an existing one*. 4A-2 exists to eliminate exactly that kind of churn, and 4B's entropy
  ledger divides new findings by churn — so the rate would have to be re-derived, not just re-measured.
- **SARIF semantics change.** Fewer results, none one-to-one with an occurrence.

That is a plan of its own, with its own fixtures and exit criteria, not a patch release. Recorded as the
follow-up it is.

## Recommendation to the maintainer, recorded rather than assumed

`1_7_0_plan.md` framed the tuning pass as necessary because 4,373 sounds like a defect. It is not one.
It is the honest answer for Type-1/Type-2 clone detection over 2,763 files containing heavy DTO
copy-paste. Lowering it by discarding the mode would make the rule find less while making the number
look better, which is the failure mode the whole certainty-tier system exists to prevent.

---

# Release gate

| # | Step | State |
|---|---|---|
| 1 | Version bump `1.7.0` → `1.7.1` | done |
| 2 | Full suite green | done — 567 |
| 3 | Dogfood clean or triaged | done — 3 pre-existing findings, exit 0 |
| 4 | Monorepo A/B vs 1.7.0, per rule, incl. the 1.4.2 SNP0005/0006 precedent | done — see below |
| 5 | Determinism across processes | done |
| 6 | `dotnet pack` + global install + self-run against the installed tool | done |
| 7 | Docs updated | done — this file, `code_architecture.md`, `README.md`, `usage.md`, `1_7_0_plan.md`, roadmap |

## Expected shape of the A/B, and why

This release changes one `catch` filter and one version string. Neither is on an analysis path: SNP0031
and SNP0032 counts must be **identical** to 1.7.0, and every other rule too. The only rule that could
move is one that depends on how many documents loaded — and a malformed target now exits before analysis
starts rather than after, so a *valid* target is unaffected.

The SNP0005/0006 check is not a formality here either. Per the locked 1.4.2 decision, any increase on
the owner's monorepo blocks the cut, and 1.7.0 measured every rule count-identical to 1.6.3. A regression
in this patch would break that chain.

# Not in 1.7.1

- **Per-clone-set SNP0031 reporting** — the only real volume lever, above. Needs its own plan.
- **Multi-TFM `CollectFiles` first-document selection** — still latent. For a multi-targeted project the
  same `FilePath` appears once per TFM, so which instance wins depends on `Solution.Projects` enumeration
  order and a different TFM resolves `#if` differently. `MILKRUN.slnx` is multi-targeted, so the gate
  above exercises the code path but does not assert determinism across TFM orderings. Untouched for the
  same reason as in 1.7.0: making the winner deterministic is cheap, but it would change reported output
  for multi-TFM solutions and deserves its own measurement.
- **Clone-set breakup** and **per-team CODEOWNERS attribution** — still deferred, unchanged.
- **4A-2's baseline grows on first run after upgrade** — needs a release-note line. Still owed from 1.7.0;
  not fixed here because it is documentation owed to already-shipped behaviour.