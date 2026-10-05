# Plan - Snipper 1.7.2: two false-positive fixes from the MILKRUN sweep, and one withdrawn

## Why 1.7.2 and not 1.8.0

Two of the three fixes proposed in `1_7_1_false_positives_investigation.md` were defects in the
analysis itself - a rule reporting a claim the repository contradicts. The third, on inspection,
was not a defect at all and has been withdrawn rather than shipped. A patch release is the right
shape for "same rules, fewer wrong answers"; nothing here adds or removes a rule, changes a
certainty tier, or alters the report schema.

---

# 1. SNP0032 - a file-creation commit was reported as a one-sided defensive fix

## What the sweep found

Of the 86 SNP0032 findings in the investigated slice, **74 blamed a commit that had *created* the
file it pointed at**. `094f266` is titled *"Merged PR 4399: m60-4634: abstract CT graphql query"* -
the merge that **introduced** a clone set, reported as drift within it:

```
$ git show 094f266 -- src/HelloWorld/GraphQL/GetOrdersQuery.cs
  new file mode 100644
  @@ -0,0 +1,66 @@
```

A commit that creates a file cannot have left a sibling un-fixed: before it there was no copy at
all. "One-sided change" and "one-sided *fix*" are different claims, and the rule was making the
second about the first.

## Root cause

`CloneDriftDetector.Evaluate`'s hunk loop had no pre-image guard. The detector already knew this
was wrong - `SiblingWasFixedEarlier` guarded the same condition for siblings:

```csharp
if (hunk.OldCount > 0
    && CloneDriftClassifier.HunkTouchesRegion(hunk, sibling.StartLine, sibling.EndLine))
```

with the comment *"Creation commits are excluded: a commit that adds a file has not 'fixed' a
clone"*. The exclusion existed on the sibling side and was never applied to the member side.

## The fix that was wrong, and the fix that is right

The obvious patch is `if (hunk.OldCount == 0) continue;`. **That would have broken the rule.** git
reports a *pure insertion* the same way - and inserting a guard above a cloned block is the
canonical shape this rule exists to report:

```
@@ -0,0 +1,66 @@     a file creation
@@ -17,0 +18,4 @@    a guard inserted above a copied block
```

Both consume nothing from the pre-image. `OldCount` cannot separate them, so the guard is read from
git's own `new file mode` header instead, captured by the patch parser into a new
`PatchHunk.IsFileCreation`. `CloneDriftShould.Distinguish_A_Pure_Insertion_From_A_File_Creation`
is the test that pins the distinction.

`SiblingWasFixedEarlier` is deliberately **left on `OldCount > 0`**. Its stated intent is *"only
hunks that modify existing lines count"*, which `OldCount > 0` expresses exactly - it is not a
coordinate proxy for creation. Switching it to `!IsFileCreation` was tried, measured, and reverted:
it admitted pure insertions as "the sibling was fixed earlier" and suppressed **28 findings whose
commit genuinely modified the file**.

## A bug in the first attempt, caught by mutation testing

The first version of the parser change reset `isFileCreation` on `diff --git` *before* closing the
outgoing section, so the last hunk of every added file was silently unmarked - and under-reported
the fix (47 removals instead of 90). `CloneDriftShould.Keep_The_Creation_Verdict_Per_File_Section_In_A_Mixed_Commit`
covers it, and was confirmed to fail when the reset is moved back ahead of the close.

## Measured, before and after

Identical flags, identical tree, packaged 1.7.1 vs this branch. Whole report, not the slice:

| | 1.7.1 | 1.7.2 | delta |
|---|---|---|---|
| SNP0032 High | 82 | **29** | −53 |
| SNP0032 Advisory | 220 | **183** | −37 |
| SNP0032 total | 302 | **212** | −90 |

**Every one of the 90 removals was verified against git**: all 90 blame a path their commit
*added*, checked across 61 distinct SHAs. Removals: 90. **Additions: 0.** No file gained a
finding. SNP0031 is byte-identical at 4,373, confirming the change does not reach the shingling
path.

---

# 2. SNP0019 - one directive reported twice

## What the sweep found

62 of 250 SNP0019 findings were a second copy of a directive already reported. When an ordinary
`using` duplicates a project-level `global using`, Roslyn emits **two** diagnostics on the same
`UsingDirectiveSyntax`: CS8019 ("unnecessary") and CS8933 ("duplicates a global using"). Both were
surfaced.

This is not cosmetic. `BaselineService.ComputeFingerprint` hashes `RuleId|relativePath|Message`,
the two messages differ, so both enter the baseline and a consumer acting on one sees the other
resurface as new.

## The fix

Deduplicated on `(DocumentId, UsingDirectiveSyntax.SpanStart)` rather than on the diagnostic's own
span, which the two do not share. CS8933 wins whichever order they arrive in - and they do not
arrive in a fixed order, because the loop walks `GetDiagnostics()` in producer-completion order.

## Measured

| | 1.7.1 | 1.7.2 | delta |
|---|---|---|---|
| SNP0019 | 250 | **188** | −62 |

All 62 removals are the redundant CS8019 half. The 62 surviving CS8933 findings are **byte-identical**
to the ones 1.7.1 emitted - same message, same location. No file gained a finding.

---

# 3. SNP0012 - **withdrawn**, not a false positive

The investigation proposed suppressing a direct `PackageReference` when the project's own source
uses one of its assemblies, on the grounds that *"does this project's own source reference it" is
the whole discriminator*. **That is wrong, and the existing fixture already said so.**

`App/Worker.cs:74` uses `Serilog.Log.Logger` directly, and `App.csproj:19` declares `Serilog` while
`Serilog.Sinks.Console:6.0.0` requires `Serilog >= 4.0.0`. That is exactly the shape the fix would
have suppressed - and an existing test asserts it is flagged.

Tested in isolation: removing the direct reference still compiles.

```
$ dotnet build Iso.csproj     # Serilog.Sinks.Console only; source still uses Serilog.Log.Logger
  Build succeeded.
  0 Error(s)
```

So the finding is **true and actionable**, exactly as the rule documents and tiers it:

> a direct reference also documents intent and pins against upstream dependency changes, so the
> message names the providing parent and advises verification

The resolved dependency graph is unchanged by removing the edge; that is the rule's whole argument,
and it holds. What the rule reports is *latent* fragility - remove the parent one day and the
direct edge is gone - which is a Moderate advisory with a stated caveat, not an error.

Suppressing it would have removed a true positive. **Not done.** The genuine weakness here is
advice quality, not correctness - flagging `ZiggyCreatures.FusionCache` as removable because a
*backplane* package happens to pull it in is technically true and practically bad counsel. That is a
message-wording question for a later wave, and no evidence was gathered for it here.

---

# Release gate

- 572 tests pass (567 at 1.7.1, plus 5 new).
- Total findings 6,812 → 6,660 (−152). Removals 152, **additions 0**.
- SNP0031 unchanged at 4,373; all 19 other rules byte-identical.
- Both fixes verified against `MILKRUN.slnx` with the owner's exclusions; no file gained a finding
  under either rule.

# Not in 1.7.2

Everything the investigation listed but did not fix, unchanged in priority:

1. **E1 - `Extend` never verifies the 60-token window it was handed** (`DuplicateFragmentAnalyser.cs:356`).
   A 32-bit FNV-1a bucket collision becomes a 60-token "clone". Deliberately *not* bundled: this
   will **reduce** SNP0031's count and invalidate any SNP0031 baseline, so it needs its own release
   with a fresh baseline. The dead `length < WindowTokens` guard at `:254` and the incorrect
   collision comment at `TokenShingleIndex.cs:193-198` are part of the same fix.
2. **F2 / F3** - serialisation-contract awareness for SNP0006 (49) and SNP0018 (48). The largest
   remaining class, and the only one needing response-graph resolution.
3. **F5** - `DefensiveFixMarkers` matches comment and string-literal text, and does not detect
   relocated hunks (~17 High).
4. **F6** - the four single-finding classes, including SNP0003's consumer closure ignoring detached
   `.csproj` descendants.
5. **Pre-existing fixture defect found during this work, not fixed here:**
   `CoreLib/PrivateSerializationPatterns.cs:9` and `CoreLib/WriteOnlyFields.cs:6` both declare
   `JsonIncludeAttribute`, so `test/Snipper.Tests/TestAssets/SampleApp` does not compile
   (CS0101). Snipper reports findings rather than requiring a clean build, so no test noticed.
   This means no claim in this repository may rest on "the SampleApp fixture builds" - including
   the compile-level check above, which is why it was run in a separate isolated project.
