# Roadmap

This is a living document. Scope is deliberately narrow at first — the goal of v0 is to prove the confidence-scoring loop actually works before investing in breadth.

## v0 — prove the loop, .NET only

v0 is done when every box below is checked — not when it "mostly works." See [TESTING.md](TESTING.md) for how each of these should actually be verified.

**Core CLI**
- [x] `eolup scan <path>` — detects current TFM, queries endoflife.date, reports current / approaching / past-EOL status
- [x] `eolup scan` respects `.eolup.yml` if present (explicit version / `next-major` / `next-lts`), falls back to `next-lts` if absent
- [x] `eolup remediate <path>` — creates an isolated branch, bumps the TFM, runs the project's own existing test suite. **v0 simplification**: this bumps `<TargetFramework>` directly rather than wrapping the full `dotnet-upgradeassistant` tool; "manual action markers" are approximated from build-output warnings (CS0618 obsolete-API, NU170x package-compatibility) rather than Upgrade Assistant's own markers. Wrapping the real tool is tracked for v1.
- [x] Confidence verdict computed and reported as one of: high-confidence / needs-review / blocked, with a human-readable reason in every case
- [x] On high-confidence, a PR is actually opened via `gh` CLI (falls back to a clear message if no remote is configured or `gh` isn't authenticated — never crashes)
- [x] Clear, non-crashing error handling when the project doesn't build at all before any transformation is attempted (a distinct check from a build failure caused by the migration itself)
- [x] `--help` and command structure verified clear via manual smoke test

**Testing infrastructure (built alongside the CLI, not after)**
- [x] Unit tests covering the full confidence-rule decision table (17 tests, including a build-failure-takes-precedence case and a tests-failed-is-needs-review case not originally spelled out)
- [x] Six fixture repos built: trivial / needs-review (TFM-conditional `[Obsolete]` call) / preexisting-warning (real `EntityFramework 6.1.3` triggering `NU1701` identically before and after) / multi-root-projects (one real `.sln` plus one unrelated project file at the root) / version-mismatch (two services on different TFMs under one directory) / no-tests
- [x] Automated harness copies each fixture to a fresh temp dir, `git init`s it, and runs `remediate` against it, asserting the expected verdict (6 tests, all passing) — **not yet wired into an actual CI pipeline for this repo**, since no GitHub Action adapter exists yet (see below)
- [x] Determinism check: running the same fixture twice yields the same verdict

**Manual test pass — done, see [MANUAL_TEST_PASS.md](MANUAL_TEST_PASS.md)**
- [x] Ran against a real public app (eShopOnWeb) — found and fixed three real bugs (TFM detection failed on centrally-declared `<TargetFramework>`; pre-existing build warnings were being misattributed to the migration; ambiguous build target when a repo has multiple project/solution files at its root)
- [x] Found cases the original fixtures didn't cover and turned each into a new fixture: `fixture-preexisting-warning` (false "needs review" from pre-existing noise), `fixture-multi-root-projects` (build-target ambiguity), plus redesigned `fixture-needs-review` to test a genuinely migration-introduced marker instead of one that was always present
- [x] **Pass #2**: ran against four well-known OSS libraries (Dapper, CliWrap, Polly, ShareX) plus a real monorepo (dotnet/eShop), deliberately chosen to be shaped differently (published multi-target libraries and a many-service monorepo, not single-target apps) — found and fixed three more real bugs (a traversal/meta-build project getting picked as "the project to scan"; a pinned SDK version producing a misleading error; **a directory containing multiple independently-versioned projects silently reporting just one of them** — the most serious finding across both passes, since it's the only one that failed silently instead of loud), confirmed two behaviors already worked correctly under new conditions (Windows-suffixed TFMs; multi-solution ambiguity generalizing beyond eShopOnWeb), and identified multi-target project support as a real, scoped-out gap (see below)
- [x] **Pass #3** (pre-launch): closed the last known blind spot — every prior test started from `net8.0`. Tested two real repos already on `net9.0`; both worked correctly (`next-lts` resolution correctly skips non-LTS versions; a true-positive pre-existing build failure was correctly caught rather than papered over). First fully clean pass — no bugs found. Surfaced one minor, pre-existing cosmetic nit (scan output mixes a TFM string and a bare cycle number between `Current`/`Target`), tracked below, not fixed yet since a proper fix needs a small `ILanguageProvider` display hook, not a quick patch.

**Not in v0 (deliberately deferred to v1)**
- Any hosted dashboard or portal UI
- Automated code fixes beyond the TFM bump (v0 does a direct TFM bump). Wrapping `dotnet-upgradeassistant` is no longer the plan: it is officially deprecated.

## v1 — make it usable by someone other than the author

- [x] ~~Accept an explicit solution/project path~~ — done: `CsProjHelper.ResolveBuildTarget` auto-resolves the common case (exactly one `.sln`/`.slnx` at the root, regardless of other stray project files sitting alongside it — see `fixture-multi-root-projects`), and an explicit `solution:` field in `.eolup.yml` covers the genuinely-ambiguous case (more than one real solution file). Landed earlier than planned once the manual test pass showed how common the "one real solution + one unrelated project file" pattern actually is.
- [x] ~~CI for this repo itself~~ — `.github/workflows/ci.yml` runs the full test suite on every push/PR, on both Windows and Linux (closing the "cross-platform checks" gap noted in TESTING.md). Wasn't on the original roadmap explicitly, but a repo with 23 tests and no automated check running them was a real gap.
- [x] ~~Real per-file test coverage instead of "does a test project exist"~~ — done: tests run under Coverlet's collector, Cobertura reports are merged per file; zero covered lines → Blocked, below `minCoverage` (default 50) → NeedsReview naming the least-covered files, collector absent → verdict unchanged but says coverage wasn't measured. **Known limits**: line coverage only (no branch coverage), and it measures the whole bumped projects rather than just the lines a specific compiler warning points at.
- [x] ~~GitHub Action adapter~~ — `action.yml` (composite action) at the repo root, dogfooded against `fixture-trivial` via `.github/workflows/self-test-action.yml`. ~~Pre-built binary~~ done: a `v*` tag builds self-contained single-file binaries (Linux x64, Windows x64, macOS arm64/x64) as release assets with SHA256SUMS; pinned to a release, the Action downloads and checksum-verifies one instead of compiling Eolup, falling back to a source build otherwise. ~~Failing the calling workflow on a verdict~~ is done as an opt-in `fail-on` input (default `none`, since whether a verdict is a failure is the team's policy) — CLI exit code 2.
- [ ] Azure DevOps Pipelines adapter
- [ ] Notification integration (Slack)
- [x] ~~Chained/stepwise remediation~~ — done: opt-in `chain: true` / `--chain` / Action `chain` input; hops continue while each is HighConfidence and stop at the first that isn't; the PR covers the high-confidence hops, one commit each. Default remains one hop per run.
- [ ] Hosted portal: multi-repo dashboard, EOL countdown view
- [x] ~~Multi-targeted project support (`<TargetFrameworks>`, plural)~~ — done: a project's version is its oldest modern entry; only entries on the repo's current version move, one hop (the end-of-life entry is replaced, not kept alongside); netstandard and .NET Framework entries are never touched; conditional lists and platform suffixes are handled; a list built from a property Eolup doesn't follow is refused before anything changes. See ARCHITECTURE.md, "Upgrades happen one hop per run".
- [x] ~~Minor:~~ (fixed — `ILanguageProvider.FormatVersion` renders the target in the ecosystem's own notation, so scan now prints `Target: net10.0`) `scan` output prints `Current` as a TFM string (e.g. `net9.0`) but `Target` as a bare cycle number (e.g. `10`) — found via Pass #3. Cosmetic only, not a correctness bug. Proper fix needs a small "format this cycle for display" hook on `ILanguageProvider` rather than pushing .NET-specific formatting into the language-agnostic CLI layer.

## v2 — broaden CI coverage

- [ ] Bitbucket Pipe
- [ ] Jenkins pipeline step
- [ ] Compliance report export (PDF, audit-ready)

## v3 — broaden language coverage

- [ ] Node/Angular provider (validates the chained-migration path for real)
- [ ] Java provider
- [ ] Python provider

## Ongoing

- Community-contributed language providers and CI adapters, once the core interface has proven stable on .NET.
