# Plan - Snipper 1.7.4: make namespace exclusion reach file-scope code

## Why this was found

Reviewing real findings on the owner's monorepo, not the test suite. The suite was green.

Excluding `Milkrun.Integration.MockingService` produced three findings from that project anyway:

```
src\Milkrun.Integration.MockingService\GlobalUsings.cs:8
src\Milkrun.Integration.MockingService\Program.cs:18
src\Milkrun.Integration.MockingService\Milkrun.Integration.MockingService.csproj:10
```

The first two are the subject of this release. The third is not — see "Not in 1.7.4".

## The defect

File-scope code declares no namespace. `ExclusionEngine` had no way to match it:

```csharp
// symbol path
if (exclusions.Namespaces.Count == 0 || symbol.ContainingNamespace is not { IsGlobalNamespace: false })
{
    return false;
}

// syntax path
var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
var typeSymbol = typeDeclaration is null ? null : semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken);
return typeSymbol is not null && IsNamespaceExcluded(typeSymbol, exclusions);
```

The symbol path returns `false` for anything whose containing namespace is global. The syntax path
requires an enclosing *type* declaration, and a top-level-statements `Program.cs` or a file of
global usings has none. Both therefore report "not excluded" while the CLI still lists the
namespace as excluded — the worst possible shape for a suppression, because it looks configured.

Measured on the monorepo: 53 namespace-less `.cs` files produced 107 findings.

## The fix: promote the marker that already existed

`<global>` was not invented for this. `DuplicateFragmentAnalyser` (SNP0031) already carried
`private const string GlobalNamespaceMarker = "<global>"`, documented in the README and reachable
through `snipper.json`, because SNP0031 resolves namespaces syntactically and so could exclude
file-scope clones when the symbol-based rules could not.

The fix makes it shared rather than adding a second convention:

| File | Change |
| --- | --- |
| `AnalysisExclusions.cs` | `GlobalNamespaceMarker` const, `IsGlobalNamespaceExcluded`, and `Covers` — the single ancestor walk, previously duplicated in two places |
| `ExclusionEngine.cs` | symbol path honours the sentinel; syntax path falls back type → namespace → sentinel |
| `DuplicateFragmentAnalyser.cs` | local const deleted; keeps its own syntactic resolution but delegates matching to `Covers` |
| `NamespaceInventory.cs` | `<global>` gets its own probe answer; the prefix walk cannot see a name that matches no declaration |
| `CommandLineParser.cs` | accepts `<global>` |

### The near-miss worth recording

The obvious syntax fallback walks *ancestors* for a namespace declaration. That is exact for a
block-scoped namespace and **wrong for a file-scoped one**: in

```csharp
using System.Diagnostics;      // ← the node

namespace Excluded.Fake;       // ← a SIBLING, not an ancestor
```

the ancestor walk returns null, and the using directive is classified as global. Shipping that
would have made `<global>` suppress using directives in *every namespaced file in the solution* —
strictly worse than the bug being fixed, and invisible unless you happened to use both together.

`ResolveFileScopeNamespace` therefore tries the ancestor first and falls back to the compilation
unit's own namespace declaration. `Not_Suppress_Usings_In_A_Namespaced_File_When_Only_The_Global_Marker_Is_Excluded`
exists to pin exactly this.

### Why the CLI restriction was lifted

`--exclude-namespaces "<global>"` was rejected as malformed and printed
`Warning: ignoring malformed namespace '<global>'.` while the identical config-file entry worked.
Reading the code showed why: the CLI validates namespace syntax and the config file does not
validate anything. The asymmetry was an accident of which path validated, not a safety property —
so it only made a documented feature hard to discover and use.

## A claimed second defect, withdrawn before any code was written

The same review suspected SNP0019 of judging global usings per-file. `UnusedUsingDirectiveAnalyser`
surfaces Roslyn's CS8019 verbatim at `Guaranteed` and its header comment says so, so if a needed
global using were reported, the compiler would have emitted a false positive.

**It does not.** An `AdhocWorkspace` fixture with two files — `GlobalUsings.cs` declaring
`global using System.Text;` and `global using System.Diagnostics;`, `Consumer.cs` using
`StringBuilder` — produces:

```
global using System.Text;        → not flagged   (consumed only from Consumer.cs)
global using System.Diagnostics; → CS8019 line 2 → SNP0019 [Guaranteed]
```

CS8019 is evaluated over the whole compilation, which is why the analyser calls
`compilation.GetDiagnostics()` rather than per-document. Snipper was never wrong.

The suspicion was an artefact of the harness that produced it, which matched flagged namespaces
against project-wide type *names*: four of the six candidate projects declared their own
`LogEventIds`, so a same-named local type made the global using look used. The harness reported
28/74 "verifiable false positives"; the real number was 0.

**Recorded because the cheap fix was wrong.** Gating CS8019 behind a project-wide re-check, as
originally planned, would have suppressed the 103 genuine findings in that run. The planned
`SolutionUsageIndex` + `GetSymbolInfo` two-stage check was never written, and
`Not_Flag_Global_Using_Consumed_Only_By_Another_File_For_AnalyzeAsync` now pins the correct
behaviour so a future change to diagnostic harvesting cannot regress into it.

The existing `App/GlobalUsings.cs` fixture asserted that "CS8019 fires on the global using exactly
as it does for ordinary ones" — but it only ever proved the *unused* case, since nothing in `App`
consumed `System.Text` anywhere. It passed whether CS8019 was per-file or project-wide. That is the
same fixture-that-proves-nothing pattern 1.7.3 found in `SampleApp`, in a smaller costume.

## Not in 1.7.4

- **`.csproj` findings (SNP0003/0004, 54 of them).** These are documented as having no namespace
  concept, and the four package/project-graph analysers take no `AnalysisExclusions` at all by
  design (`AnalyserFactory.IsExclusionAgnostic`). Suppressing them needs a project-scoped exclusion
  dimension — new CLI surface, not a bugfix.
- **A warning when an exclusion matched nothing.** Already implemented: `SuppressionAudit` reports
  stale suppressions via `NamespaceInventory.ExistenceProbe`, and `CliRunner` warns on malformed
  entries. The `<global>` probe was extended so this machinery does not report a *live* `<global>`
  exclusion as stale.
- **SNP0006 and JSON source generation.** Suspected, not confirmed. `ExclusionEngine` already
  excludes members by attribute (`LoggerMessage`, `GeneratedRegex`), so `[JsonSerializable]` has an
  obvious home, but 847 Advisory findings deserve a dedicated pass before anyone acts on them.

## Effect

581 → 590 tests. Nine added: five namespace-exclusion regressions, two SNP0019 cross-file
regressions, two CLI-parser tests replacing the config-only assertion. No production finding count
changes unless `<global>` is configured.

Release convention note: `Snipper.1.7.4.nupkg` is packed and `dotnet tool update --global` has been
run against it, so the installed tool reported 1.7.4 from this release onward. (This line originally
said the install had not been run and the tool still reported 1.7.3 — accurate when written, and
wrong for every reader after the install. Stale release state is worth correcting rather than
leaving to be discovered by someone who believes it.)
