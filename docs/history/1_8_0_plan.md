# 1.8.0 — SNP0033 cyclomatic complexity

**Status:** implemented, calibrated, dogfooded. Unreleased at time of writing.

## What it is

`CyclomaticComplexityAnalyser` reports methods whose cyclomatic complexity exceeds a
configurable maximum (default **15**, `--max-complexity <n>` to change it). Each callable body is
scored independently: methods, constructors, destructors, operators, accessors, local functions and
lambdas. A containing method is **not** charged for a lambda or local function nested inside it.

It is deliberately **not** part of SNP0024 / `FindingCategory.Tightening`. Those four sub-checks are
provable local transforms — "can be static", "can be readonly" are decidable facts. A complexity
threshold is a judgement about a tunable number, so it gets its own rule ID and its own category.

## Why it is Advisory, and why that is not a demotion

Every other rule here is Tier 1 because something *proved* it: the compiler emitted a hidden
diagnostic, or a symbol has no reference in the solution. Nothing proves that 17 branches is too
many. The count is arithmetic; the threshold is policy. Splitting those is why the message carries
the number (`complexity 23 exceeds the maximum of 15`) — the finding is triagable without re-running
anything, and the reader can disagree with the threshold rather than the tool.

## The counting table is the contract

Parity target is ReSharper's **classical** cyclomatic complexity. Matching it means matching its
**non-counts**, which is where a naive implementation over-reports — often by 2x on ordinary code.

| Construct | Count | Note |
|---|---|---|
| `if`, and each `else if` | +1 | an `else if` is itself an `if` |
| bare `else` | **0** | the negative branch is already the `if` |
| `while` / `do` / `for` / `foreach` | +1 each | |
| `case` label | +1 each | |
| `default` label | **0** | |
| `catch` clause | +1 | |
| `catch ... when` filter | +1 | so a filtered catch is +2 |
| `&&` / `||` | +1 each | short-circuiting is a branch |
| `?:` | +1 | |
| switch-expression arm | +1 each | |
| `_` switch arm | **0** | the fallthrough, not a branch |
| `?.` null-conditional | **0** | see below |
| `??`, `??=`, `try`, `finally`, `throw`, `goto`, `is`, `as` | **0** | |

Baseline is **1** per function, matching ReSharper: a branch-free method scores 1, and a finding
requires `complexity > max`.

**`?.` is the one judgement call, and it is not free.** A null-conditional *does* introduce a
branch; counting it would raise every method that touches a nullable API. ReSharper's classical
metric does not count it, and matching that is the parity commitment. It is one line to change if
the call is ever revisited — the table is the spec, not the implementation.

**Not in scope: cognitive complexity.** ReSharper also computes it, and it is a *different*
algorithm that penalises nesting and operator mixing differently. Deliberately excluded.

`Test/Snipper.Tests/CyclomaticComplexityAnalyserShould.cs` pins every row above — including every
zero — by running at `maxComplexity = 1` and reading the exact count back out of the message, so
the tests cannot drift from what the rule emits.

## Why it is cheap and why it is deterministic

Purely syntactic: no semantic model, no binding, no cross-project state. The whole MILKRUN pass is
**2.2s for 3,234 scored methods**. Contrast SNP0019, where the cost is the compiler's own
`GetDiagnostics` and the output order is inherited from a producer-completion race.

Being syntax-only also makes it deterministic by construction, which is a stronger guarantee than
any other rule in the tool.

## Calibration on MILKRUN

Distribution measured in one run at threshold 1 (every method with a branch), 3,234 methods scored:

| Threshold | Findings |
|---|---|
| > 5 | 534 |
| > 8 | 199 |
| > 10 | 128 |
| > 12 | 89 |
| **> 15 (default)** | **43** |
| > 20 | 18 |
| > 25 | 4 |
| > 30 | 2 |

43 findings against a ~3,000-finding report is triageable rather than a flood, which is what the
default is for. The worst offenders are real: a 50-parameter test-double factory at **38**, an event
mapper at **31**, a status formatter at **29**. Verified by hand against the first one — a chain of
`if (x is not null)` guards, one per parameter — and the count is right.

## The open decision this creates for the owner

Dogfooded on Snipper itself at threshold 15, the rule reports **29 findings in its own codebase** —
against a report of 32 total. `CliRunner.AnalyzeAsync` is 59, `HierarchyDeadCodeAnalyser` 67,
`CommandLineParser` 42.

That is not a defect in the rule. Snipper is nineteen branch-heavy analysers in one repository, so
its complexity profile is nothing like ordinary application code. But it is a real consequence:
enabling this on a CI gate adds 29 findings on day one. The options are

- keep 15 and triage or suppress the 29,
- raise the default to 20 (~18 on MILKRUN, ~12 here), or
- make the rule opt-in, like `--config-analysis` and `--duplicate-detection`.

Left as a decision, not silently resolved. It should not be decided by whoever writes the next
commit.