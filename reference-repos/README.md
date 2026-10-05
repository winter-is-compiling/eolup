# Reference Repos

A small, version-pinned list of real public repositories Eolup is periodically
re-validated against — deliberately kept separate from the fast, deterministic
fixture suite in `/fixtures` (see [TESTING.md](../TESTING.md)).

## Why these are pinned to a commit, not "latest main"

If a check against one of these ever fails, that should mean **Eolup's
behavior changed**, not that the upstream repo changed underneath us. Pin a
specific commit when adding an entry, and only update the pin deliberately
(e.g. to pull in a newer real-world pattern worth testing against).

## Why these aren't committed into this repo

Real repos bloat clone size, drift out of date, and raise licensing/attribution
questions if vendored wholesale. `scripts/test-against-reference-repos.sh` clones
each one fresh into a temp directory, runs it, and throws it away — nothing here
is checked in except the pointer (name/url/commit) and the notes below.

## Running the check

```bash
scripts/test-against-reference-repos.sh
```

Prints the actual `scan` and `remediate` output for each entry — once against the
repo's real, unmodified state, and again with its optional config template
applied if one is listed (simulating a real user having added their own
`.eolup.yml`, since we can't commit one into someone else's repo upstream).
Compare it against
the "expected outcome" documented per entry below — there's no automated
pass/fail assertion here (unlike the fixture suite), because the whole point is
surfacing real-world surprises for a human to look at, not enforcing a fixed
verdict on code Eolup doesn't own.

## Entries

### eShopOnWeb

- **Commit pinned**: `4da8212117e87d808d4bbc7da6286fd2147ce606`
- **What it stress-tests**: a real, multi-project ASP.NET Core app with
  centrally-declared `<TargetFramework>` (in `Directory.Packages.props`, not any
  individual `.csproj`) and a `docker-compose.dcproj` sitting alongside its main
  `.sln` at the root.
- **Why it's here**: this exact repo drove four real bug fixes — see
  [MANUAL_TEST_PASS.md](../MANUAL_TEST_PASS.md). It's the closest thing Eolup
  has to a regression suite against real-world repo structure, not just the
  synthetic patterns in `/fixtures`.
- **Expected outcome, without any config** (the repo's actual, unmodified state —
  it genuinely has two competing solutions, `eShopOnWeb.sln` and
  `Everything.sln`): `scan` reports .NET 8, approaching EOL, target resolved to
  .NET 10. `remediate` correctly **refuses to guess** and reports a clear error
  asking for a `solution:` override — this is the intended behavior for a
  genuinely ambiguous repo, not a bug.
- **Expected outcome, with `configs/eShopOnWeb.eolup.yml` applied**
  (`solution: eShopOnWeb.sln`, simulating what a real user would add to their own
  copy of this repo): `remediate` reports **Blocked** — the real migration hits a
  genuine breaking change (`CS0433`: the `Program` type exists in both
  `PublicApi` and `Web` once compiled against net10.0). If either of these two
  outcomes ever changes, that's worth investigating before assuming it's fine.

### Dapper

- **Commit pinned**: `8becae8d0e2b360165ae03c0d5d1330b0273473d`
- **What it stress-tests**: a genuinely multi-targeted library (`<TargetFrameworks>net461;netstandard2.0;net8.0;net10.0</TargetFrameworks>`) with a root-level `Build.csproj` using the `Microsoft.Build.Traversal` SDK (referencing every other project rather than containing code itself) — the default shape for any published NuGet library, and completely untested by anything else here (eShopOnWeb and every fixture are single-target apps).
- **Why it's here**: found three real bugs across two rounds of fixes — see [MANUAL_TEST_PASS.md, Pass #2](../MANUAL_TEST_PASS.md). Kept as a permanent reference for a real limitation (multi-target support) that's deliberately not fixed yet, so its exact behavior stays honest and doesn't silently regress into something misleading again.
- **Expected outcome, without any config**: `scan` reports `net5.0` — the oldest modern entry, in `Dapper.Rainbow` (`net461;netstandard2.0;net5.0`) — with `Upgrade path: net5.0 -> net6.0 -> net8.0 -> net10.0`, notes that the two `net461` EntityFramework projects are left alone, and notes each multi-targeted library. `remediate` moves only that `net5.0` entry to `net6.0`, builds, and reports **NeedsReview**: "no test broke because of the migration" — the ~740 failing tests need a SQL Server and fail identically on the untouched code. History: this repo was refused outright before multi-target support ("Only 1 of 8 projects could be evaluated"), and before that silently scoped to `docs.csproj`. If `netstandard2.0`/`net461` entries are ever rewritten, or any entry other than Rainbow's `net5.0` moves, that's a regression.

### eShop

- **Commit pinned**: `b4a40872005d4bb29e5b1fa1ff7e244143d39215`
- **What it stress-tests**: a genuine monorepo — 28 projects, mostly single-target `net10.0` services alongside a multi-targeted MAUI mobile client (`net10.0-android;net10.0-ios;net10.0-maccatalyst`, plus a Tizen variant). Successor to the now-archived `eShopOnContainers`. This is the *positive* counterpart to `fixture-version-mismatch`: many candidate projects, but the ones Eolup can evaluate all agree, so it should proceed without complaint rather than over-triggering the version-mismatch check added after finding that bug.
- **Why it's here**: this repo's own services all happen to agree on version, which is exactly why testing it alone wasn't enough to catch the version-mismatch bug — a constructed fixture (`fixture-version-mismatch`) was needed to actually reproduce and fix it. Kept here as a real-world check that the fix doesn't false-positive on a large, legitimately-agreeing multi-project repo. See [MANUAL_TEST_PASS.md, Pass #2](../MANUAL_TEST_PASS.md).
- **Expected outcome, without any config**: `scan` reports `net10.0`, status `Current` (not yet approaching EOL). `remediate` should proceed past detection without a version-mismatch error — the multi-targeted MAUI client is excluded from the comparison (it can't be evaluated as a single TFM at all), and every single-target project agrees on `net10.0`. If this ever starts reporting an oldest version other than `net10.0`, or a note about projects on different versions, that's worth investigating (detection used to refuse such repos with a "multiple projects on different versions" error; it now uses the oldest version — see ARCHITECTURE.md, "Upgrades happen one hop per run").

### PhotinoBlazorNet9Template

- **Commit pinned**: `ee3ed4a6dc0cab7f19b41a1c9d19cbb061fe1db8`
- **What it stress-tests**: a real repo already on a **non-LTS starting version** (`net9.0`) — before this entry, every single test in this project (real or constructed) started from `net8.0`, leaving `TargetVersionResolver`'s "skip non-LTS, resolve to the next real LTS" logic completely unexercised against real code. Clean single-project, single-TFM case (no multi-targeting, no solution ambiguity) — chosen specifically to isolate this one gap.
- **Why it's here**: found via a deliberate pre-launch push to close every known blind spot before going public. See [MANUAL_TEST_PASS.md](../MANUAL_TEST_PASS.md) for the fuller write-up.
- **Expected outcome, without any config**: `scan` reports `net9.0`, target resolved to `net10.0` (correctly skipping non-LTS `9`), status `ApproachingEol`. `remediate` reports **Blocked** — "No test project found in the solution" — the repo has no test project, so this is the correct, safe outcome, not a bug. If target ever resolves to anything other than `10` from this starting point, that's a real regression in `next-lts` resolution.

## Adding a new entry

Good candidates stress-test a real-world pattern the fixture suite doesn't
already cover — not just "another repo that happens to build." Before adding one,
have a one-sentence answer to "what does this repo do to Eolup that nothing
else here does yet?" Candidates worth considering next:

Nothing is currently on this list — every candidate identified so far has been
tried. If you think of a new one, the bar is the same: a one-sentence answer to
what it does to Eolup that nothing else here does yet.

Already covered, so no longer needed on this list: a repo with a pinned SDK
version Eolup's environment doesn't have (Polly), a repo with a Windows-
suffixed TFM (ShareX, `net10.0-windows...` — turned out to already work
correctly), a repo where the "first project found" heuristic picks something
wrong (Dapper's traversal project, now fixed), a large monorepo with many
independent projects (eShop — 28 projects; turned out its services all agree on
version, which is exactly what led to constructing `fixture-version-mismatch`
separately once real repos didn't happen to expose that bug on their own) — see
Pass #2 in [MANUAL_TEST_PASS.md](../MANUAL_TEST_PASS.md) for all of these.
CliWrap and Polly's actual libraries are also multi-targeted like Dapper's, but weren't
added as separate entries here since they'd exercise the identical, already-
covered code path. A repo with no test project at all, and a repo already on a
non-LTS version, were both on this list at one point — `PhotinoBlazorNet9Template`
above covers both at once (net9.0 starting point, and it has no test project).
