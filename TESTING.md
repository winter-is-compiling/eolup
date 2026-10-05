# Testing Strategy

## Why this needs more rigor than a typical CLI tool

Eolup's entire value proposition rests on one claim: *"you can trust this confidence score."* If that claim is wrong even occasionally, the product is worse than useless — it either causes a bad merge or trains people to ignore its verdicts entirely. Testing here isn't about coverage percentage; it's about proving the confidence verdict is correct and stays correct as the tool evolves.

## The four layers

### 1. Unit tests — pure logic, no external dependencies

Cover everything that doesn't touch a filesystem, a network call, or a subprocess:
- `.eolup.yml` parsing (explicit version / `next-major` / `next-lts`, missing file, malformed file)
- EOL-date comparison logic (given a version and an EOL date, is this urgent / approaching / fine?)
- Confidence-rule evaluation, given a *simulated* build/test/API-usage result as input (this is the core decision table — test every branch of high / needs-review / blocked explicitly)

These should be fast enough to run on every commit, no exceptions.

**Provider tests** (`Eolup.Providers.DotNet.Tests`) sit between this layer and the fixture suite: they exercise `CsProjHelper` — the .NET-specific file logic — against throwaway temp directories, so they *do* touch the filesystem (and three spawn real MSBuild to evaluate a property), but they're still fast (~2s) and need no network. They pin the behaviors real repos taught us the hard way: every branch of build-target resolution, traversal-project exclusion, that rewriting a project changes exactly one line (BOM and line endings preserved), and that a TFM declared only in a shared `Directory.Build.props` is found and rewritten there.

### 2. Fixture-based integration/system tests — automated, not manual

The fixture repos are not manual test cases — they are the automated test suite for the part of the product that actually matters:

| Fixture | Scenario | Expected verdict |
|---|---|---|
| `fixture-trivial` | No breaking changes, full test coverage | High confidence — migration committed on its branch, PR opened |
| `fixture-needs-review` | An obsolete-API call that's genuinely new after the version bump (TFM-conditional file inclusion — absent at net8.0, present at net10.0), in a file the tests never execute | Needs review — names both the obsolete call and the uncovered file |
| `fixture-preexisting-warning` | A package-compatibility warning (`NU1701`) that's identical before and after the bump | High confidence — proves baseline-diffing correctly ignores pre-existing noise (see MANUAL_TEST_PASS.md, Pass #1 finding 3) |
| `fixture-multi-root-projects` | One real `.sln` plus one unrelated project file at the root (mirrors eShopOnWeb's `.sln` + `docker-compose.dcproj`) | High confidence — proves automatic build-target resolution avoids the MSB1011 ambiguity without needing a `.eolup.yml` override (see MANUAL_TEST_PASS.md, Pass #1 finding 2) |
| `fixture-version-mismatch` | Two services under one directory, on different TFMs (net8.0 / net10.0) | The oldest (net8.0) is "current": only `ServiceA` is bumped (asserted from the migration commit's file list), `ServiceB` is left alone, and `scan` notes it. Began as an error test (a monorepo path must never silently report one service's version — MANUAL_TEST_PASS.md Pass #2 finding 5); real repos mix versions (Pass #5), so the rule became oldest-first, one hop per run |
| `fixture-no-tests` | No test project at all | Blocked — "cannot safely verify," no PR |
| `fixture-zero-coverage` | A test project that builds and passes but never calls into the application code | Blocked — the tests execute none of the migrated code |
| `fixture-no-coverage-collector` | Full tests, but the test project has no `coverlet.collector`, so coverage can't be measured | High confidence, with the reason stating that coverage was not measured |
| `fixture-coverage-hostile-test` | A test that passes normally but fails once the assembly is coverage-instrumented (as architecture tests do in real repos) | High confidence — a failing coverage run is re-checked with a plain run, and coverage is reported as not measured (found by migrating Equinox) |
| `fixture-netstandard-library` | A `netstandard2.1` shared library beside `net8.0` projects | High confidence — the library neither blocks detection nor gets retargeted (asserted from the commit's file list); `scan` notes it was left alone (Pass #5 finding) |
| `fixture-preexisting-test-failure` | One test fails on the untouched code too (a stand-in for an integration test that needs a database) | Needs review — "no test broke because of the migration", naming the test that was already failing; the repo is left on the migration branch after the comparison |
| `fixture-migration-breaks-test` | A test that passes on net8.0 and fails on net10.0 (a stand-in for a real behavioural change) | Needs review — "passed before the migration and fails after it", naming that test |
| `fixture-multi-target-library` | A library with `<TargetFrameworks>net8.0;netstandard2.1</TargetFrameworks>` beside a net8 app | High confidence — only the modern entry moves (`net10.0;netstandard2.1`), `scan` notes the netstandard entry stays |
| `fixture-chain-stops` | A net6.0 app whose test passes on net8.0 but fails on net10.0, remediated with chaining | Step 1 (net6.0 → net8.0) HighConfidence, step 2 (net8.0 → net10.0) NeedsReview naming the broken test; the chain stops there and the PR-able branch holds exactly step 1's commit |
| `fixture-stale-framework-package` | A net8.0 app referencing `Microsoft.Extensions.Options` 8.0.0, with a test that fails on net10.0 | NeedsReview naming the broken test, plus a reason naming the package that is still on the old framework major; with the package bump on, the retry can't fix it, so the speculative commit is dropped and the verdict says it was tried |
| `fixture-stale-package-fixed-by-bump` | A net8.0 minimal API whose in-memory test host comes from `Microsoft.AspNetCore.Mvc.Testing` 8.0.11 | After the bump to net10.0 the test fails with HTTP 500 (NeedsReview, package named). With the package bump on, `Mvc.Testing` moves to 10.x in its own commit, the retry passes, and the verdict is HighConfidence listing that package |
| `fixture-single-entry-target-frameworks` | A library and a test project that each list ONE framework in the plural `<TargetFrameworks>net8.0</TargetFrameworks>` (the shape of every Prowlarr project), plus an app that uses the singular element | High confidence — every project keeps the form it was written in (`<TargetFrameworks>net10.0</TargetFrameworks>`) and exactly one line changes per file. It used to crash after the branch was created (found by validating v0.3.0 on Prowlarr, MonoGame and workflow-core) |
| `fixture-framework-from-import` | A project whose target framework is set by an explicit `<Import>` of a file Eolup doesn't edit | `remediate` stops with a clear error naming the project, before any build, branch or edit (`git branch` and the tree are untouched) |
| `fixture-tests-excluded-from-build` | A test project that is in the solution but whose build is switched off (netch's shape): `dotnet test` exits 0, runs nothing and never creates its results directory | Blocked — "the test command succeeded but ran no tests". It used to crash with a `DirectoryNotFoundException` |
| `fixture-test-project-without-tests` | A test project that compiles but contains no test (`dotnet test` exits 0 with "No test is available") | Blocked — "ran no tests", not "tests passed" and not "zero coverage" |

A small test harness runs `eolup scan` then `eolup remediate` against each fixture and asserts the actual verdict matches the table above. This suite is what protects you from silently regressing trust in the tool as the confidence logic evolves — a change that flips `fixture-needs-review` to "high confidence" should fail CI immediately, not get discovered by chance later.

The suite runs against a **recorded snapshot** of endoflife.date (`RecordedEolClient`), so a verdict can only change because this repo's code changed — not because the service was down or a new .NET release shifted what `next-lts` means. The real `dotnet`, `git` and MSBuild still run. One test (`LiveEndOfLifeDateSmokeTests`, trait `Category=Live`) still hits the real API to catch its response shape drifting; it is excluded from the merge-gating CI (which runs `--filter "Category!=Live"`) and runs instead in a weekly `live-smoke.yml` workflow, so a third-party outage can't turn a merge red. Skip it offline with the same filter.

As real-world edge cases are found (see layer 3), add them here as new named fixtures with an asserted expected outcome — this is how the suite grows over time.

### 3. Manual / exploratory testing — hunting for what the fixtures don't cover yet

This is where a human tester's judgment matters most, and it should be spent looking for gaps, not re-confirming what the fixture suite already checks mechanically:
- Run against real public sample apps (e.g. eShopOnWeb) and sanity-check: does the verdict match what an experienced .NET developer would actually conclude?
- Deliberately try to construct a case where tests pass but the change is *not* actually safe (e.g. a behavioral change no test happens to cover) — every case like this found should become a new fixture, not just a one-off bug fix.
- Cross-platform checks (Windows vs. Linux CI runners) — .NET tooling behavior can differ subtly between them.
- CLI ergonomics: is `--help` clear, are error messages actionable when something goes wrong before any transformation even starts (e.g. the project doesn't build at all)?

**This already paid off once** — see [MANUAL_TEST_PASS.md](MANUAL_TEST_PASS.md): running against eShopOnWeb surfaced a case where the tool would have misattributed 13 pre-existing warnings to an unrelated migration, which directly threatened the "most migrations are trivial" premise the whole product rests on. Neither the unit tests nor the original fixtures caught it, because both were written by the same person with the same assumptions as the code — exactly the blind spot manual testing against unfamiliar real code is for.

### 4. Reference-repo corpus — the formalized, repeatable version of layer 3

Manual testing is where a new real-world repo earns its place; the [reference-repos](reference-repos/README.md) corpus is where it stays checked once it has. Each entry is a real public repo pinned to a specific commit, with a documented expected outcome — `scripts/test-against-reference-repos.sh` clones each one fresh and prints what Eolup actually does with it today, for a human to diff against what's documented.

This is deliberately **not** folded into the automated fixture suite: it's slower (real clones, real `dotnet build`/`test` against much larger real codebases), more network-dependent, and — critically — it has no fixed pass/fail assertion, because the point is surfacing real-world surprises, not enforcing a verdict on code Eolup doesn't own. Run it periodically (after a change to core detection/scoring logic, or whenever adding a new reference repo), not on every commit.

This already caught a real bug once: the very first version of the explicit-build-target fix passed all fixture tests, but running it against eShopOnWeb's actual unmodified state (not the manually-tweaked clone used during the original manual pass) revealed the ambiguity error-detection check only recognized one of the two error codes `dotnet build` can emit for the same underlying problem (`MSB1011` vs `MSB1050`, depending on how the directory is passed) — see MANUAL_TEST_PASS.md, Pass #1 finding 2.

## What v0 needs before it's "done" testing-wise

- [x] Unit tests covering the full confidence-rule decision table
- [x] Automated fixture suite covering all five scenarios above, running on every push/PR via `.github/workflows/ci.yml` (Windows + Linux matrix)
- [x] At least one real public sample app manually verified against the tool's verdict — see MANUAL_TEST_PASS.md
- [x] Determinism check: running the same fixture twice produces the same verdict (a flaky classifier destroys trust faster than a wrong-but-consistent one)
- [x] A repeatable way to re-check real-world repos going forward, not just a one-time manual pass — see `reference-repos/`
