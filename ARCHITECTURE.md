# Architecture

## Overview

Eolup splits cleanly along a trust boundary: anything that touches your source code runs inside your own infrastructure; anything hosted by us only ever sees metadata.

```
┌─────────────────────────────────────────┐        ┌───────────────────────────────────┐
│  CUSTOMER'S OWN CI/CD, ANY PLATFORM       │        │  HOSTED PORTAL (metadata only)     │
│  (source code never leaves here)          │        │                                     │
│                                            │        │                                     │
│  eolup-cli                           │        │  API + DB                          │
│   ├─ scan      (every build, read-only)   │──────► │   - service inventory              │
│   ├─ remediate (on-demand)                │  JSON  │   - confidence scores              │
│   └─ language providers (.NET first)      │  only  │   - EOL countdown                  │
│                                            │        │                                     │
│  CI adapters (thin wrappers around the CLI)│       │  Dashboard                         │
│   - GitHub Action                         │        │   - fleet-wide compliance view     │
│   - Azure Pipelines task                  │        │   - per-team migration queue       │
│   - Bitbucket Pipe / Jenkins step         │        │   - compliance report export       │
└─────────────────────────────────────────┘        └───────────────────────────────────┘
                    │
                    ▼
       endoflife.date API (external, community-maintained version + EOL data)
```

## Repository split (open-core)

- **`eolup-cli`** (this repo, open source, Apache 2.0) — the scanning and remediation engine, the language provider interface, and the CI adapters. Anyone can run this standalone with no account, no signup, and no data ever leaving their machine.
- **`eolup-portal`** (private) — the hosted dashboard, multi-tenant API, notifications, billing, and compliance report generation. This is the commercial layer built on top of the open engine.

## Why a CLI-first design, not a CI-native tool

Sonar and Dependency-Track both follow this pattern for a reason: writing the real logic once as a standalone CLI, then wrapping it in thin per-platform adapters, means adding a new CI system is a few hours of argument-mapping — not a reimplementation. The CLI knows nothing about which CI invoked it; it just needs a repo path, a target config, and (for reporting) a portal endpoint and token.

## The two-pipeline model

1. **Scan pipeline** — runs on every normal build. Detects the current version, checks it against the EOL calendar, reports status. Cheap, read-only, non-blocking by default.
2. **Remediation pipeline** — runs only on demand (triggered from the portal or via `eolup remediate` directly). Pulls the repo into an isolated branch, runs the transformation, runs the project's own existing test suite in a sandboxed container, computes a confidence score, and opens a PR (or doesn't, depending on the verdict).

Keeping these separate means the expensive work (actually running a transformation + full test suite) only happens when someone has decided they want the fix — not on every commit.

## Confidence scoring (v0: deterministic, explainable)

v0 confidence is a rule-based verdict, not a model — this matters for an audience that needs to trust and audit what the tool decided, not just accept a black-box score:

- **High confidence** → build succeeded, existing test suite passed with adequate line coverage of the migrated code, no known-risky API surface touched.
- **Needs review** → build succeeded but touched a deprecated/ambiguous API, a package has no direct replacement, the tests failed (with the failing tests compared against the same suite on the untouched code, so a failure that was already there is reported as such rather than blamed on the migration), or line coverage of the migrated code is below `minCoverage`.
- **Blocked** → cannot be safely verified (no test project, or tests that execute none of the migrated code, the build fails, or a suite that fails even on the untouched code without producing per-test results to compare). This is reported as a risk finding, not silently skipped.

**How coverage is measured.** A target-framework bump edits no source files, so the "changed files" are effectively every non-test source file in the bumped projects. `dotnet test` runs with Coverlet's collector, the resulting Cobertura reports are merged per file (a line counts as covered if any test project hit it), and test projects, generated `obj/` code and anything outside the repo are excluded. If the collector isn't referenced there is no report; the verdict is left as it was and the reason says coverage was not measured.

More sophisticated scoring (e.g. LLM-assisted diff review as an additional signal) is a later-stage enhancement, not part of the initial trust model.

## Language providers

The core CLI is language-agnostic; only one interface needs a per-ecosystem implementation:

```
ProductId         — which endoflife.date product this ecosystem's versions belong to ("dotnet")
DetectVersion()   — what version is this project currently on?
FormatVersion()   — render an endoflife.date cycle in this ecosystem's own notation ("10" -> "net10.0")
Remediate()       — transform toward the target, run the project's own tests, report raw signals
```

EOL lookup is deliberately *not* on this interface: it's generic (Core queries endoflife.date using `ProductId`), so a new ecosystem only has to say what its product is called and how to change its own files.

**.NET is the only implementation in v0.** Its current remediation approach is deliberately minimal — it bumps `<TargetFramework>` directly and infers "manual action markers" from build-warning diffing, and does not rewrite obsolete API usage. Wrapping `dotnet-upgradeassistant` was the original plan, but Microsoft has officially deprecated it in favour of a paid, AI-based Copilot agent that runs in the IDE; neither fits a deterministic, explainable, runs-anywhere CLI (see ROADMAP.md). Deeper automated fixes, if built, will be our own narrow, rule-based ones. Additional providers (Java, Node/Angular, Python, ...) are added by implementing this same interface — no change to the orchestration core.

## Configuration: target version is explicit, never assumed

Two axes are tracked independently:

- **Urgency** — how close the *currently detected* version is to its EOL date. This is always computed live against the EOL calendar.
- **Target** — where the team actually wants to land. This is never assumed to be "latest." It's explicit, per-repo configuration:

```yaml
# .eolup.yml
target: "19"        # an explicit version, or:
# target: next-major
# target: next-lts  # meaningful where the ecosystem has an LTS/STS split (e.g. .NET)

# solution: MyApp.sln  # optional — only needed if automatic build-target
                        # resolution can't disambiguate (see below)
```

Org-level defaults live in the portal; a repo's own `.eolup.yml` overrides them.

## Build-target resolution (.NET)

Plain `dotnet build <directory>` fails with `MSB1011` whenever a repo's root has more than one project/solution file — a real, fairly common pattern (found via the eShopOnWeb manual test pass, which keeps a main `.sln` alongside an unrelated `docker-compose.dcproj`). Rather than handing `dotnet build` a bare directory, `CsProjHelper.ResolveBuildTarget` resolves an explicit target itself:

1. If `.eolup.yml` sets `solution:`, use that — always wins.
2. Else, if exactly one `.sln`/`.slnx` exists at the root, use it. This alone fixes the common case above, since Eolup is no longer asking `dotnet build` to auto-discover among *every* project-like file itself — just the one solution that actually matters.
3. Else, if exactly one `.csproj` exists at the root (no solution file at all), use that.
4. Else, if there is nothing buildable at the root at all and exactly one `.sln`/`.slnx` exists in a subfolder (`src/App.sln` is a common layout), use it. With several nested solutions (found via adnc, which has six), `dotnet build` reports `MSB1003`; Eolup turns that into a message listing the solutions it found and pointing at `solution:`, which accepts a path relative to the repo.
5. Otherwise — genuinely multiple competing solutions — fall back to the bare directory, so the existing `MSB1011` error still fires with a message pointing at the `solution:` config fix, rather than silently guessing which one is "the right one."

## Upgrades happen one hop per run

A repo is never jumped several versions in one go. Each run performs **one hop** — from where the repo is to the next target — and opens one small PR; after that merges, the next run does the next hop:

```
Run 1: net3.1 -> net6.0   (PR 1, reviewed and merged)
Run 2: net6.0 -> net8.0   (PR 2)
Run 3: net8.0 -> net10.0  (PR 3)
```

The reasoning: breaking changes are met one hop at a time, so each PR is small, more likely to build and pass cleanly (a real chance at a high-confidence verdict), and when a hop *does* need review it is obvious which one. `scan` shows the whole journey (`Upgrade path: net6.0 -> net8.0 -> net10.0`) so it is clear where the steps end; `Target:` is only this run's hop. Ecosystems whose tooling only supports single-major hops (Angular's `ng update`) fit this model naturally.

**Mixed versions in one repo.** Real repos routinely have projects on different versions. Eolup treats the **oldest** as the repo's current version and moves only the projects on it this run; the rest are already ahead and wait, so the repo converges instead of some projects leaping several majors. Projects targeting `netstandard*` are never touched and never count: it is a compatibility label that runs on every modern .NET, not an outdated runtime, and retargeting it would stop older consumers referencing the library. .NET Framework projects (`net461`, `net35`) are left alone the same way, with a note: moving .NET Framework to modern .NET is a different migration, and real library repos keep such projects on purpose (Dapper.EntityFramework, CliWrap.Signaler). Only a target framework Eolup doesn't recognise makes it refuse rather than guess.

**Multi-targeted projects (`<TargetFrameworks>`).** The same rules apply entry by entry. A project's version is its oldest modern .NET entry; a run moves only the entries on the repo's current version, one hop, keeping platform suffixes (`net8.0-android` → `net10.0-android`); `netstandard*` and .NET Framework entries (`net461`, `net48`) are never touched, since a library listing them is deliberately serving older consumers. The end-of-life entry is **replaced, not kept alongside**: `net6.0;net8.0;netstandard2.0` → `net8.0;netstandard2.0`. Every `<TargetFrameworks>` element in the declaring file is rewritten, conditional ones included (MAUI-style `$(TargetFrameworks);net8.0-windows…` appends), with property references passed through untouched. If the list is assembled from a property Eolup doesn't follow (`<TargetFrameworks>$(LibraryTargets)</TargetFrameworks>`), it stops before changing anything and says so. For a published library, dropping a target framework is a consumer-visible change: it shows as a one-line diff in the PR, and the reviewer should treat it as such.

**Automatic chaining (opt-in)** — `chain: true` in `.eolup.yml`, `--chain` on the CLI, or `chain: 'true'` on the GitHub Action. The run carries on to the next hop while each hop is **HighConfidence**, re-scanning in between so each hop starts from what the previous one produced, and stops at the first hop that isn't — it never builds further on a hop that needs a human. Every hop is its own commit on a branch stacked on the previous hop's, so the PR is opened for the **last HighConfidence hop** and contains exactly those steps, one commit each; the hop that stopped the run is left as a local branch for inspection and described in the PR body. `--fail-on` judges that last attempted hop. Off by default: one hop per run stays the agreed baseline.

## External data dependency

Eolup does not maintain its own database of framework versions or EOL dates. It queries [endoflife.date](https://endoflife.date)'s API at scan time, which already covers .NET, Angular, Java, Python, Node, and 200+ other products. This is a deliberate choice to avoid an unbounded, ever-growing maintenance burden that has nothing to do with Eolup's actual value.
