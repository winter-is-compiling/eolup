# Rollforward

[![CI](https://github.com/winter-is-compiling/rollforward/actions/workflows/ci.yml/badge.svg)](https://github.com/winter-is-compiling/rollforward/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

**Stop paying a full sprint's worth of story points for a one-line framework bump.**

Rollforward is a CLI and GitHub Action that upgrades end-of-life .NET target frameworks (for example `net8.0` → `net10.0`) and opens a pull request only when your own tests prove the upgrade is safe.

It scans a .NET project for outdated, end-of-life framework versions, tells you exactly how risky upgrading actually is, and — for the low-risk majority — opens a pre-validated, ready-to-merge pull request. The hard cases get routed to a human with a clear explanation of what needs judgment.

> **Status: early-stage, pre-alpha, .NET only.** Works today, tested against real projects — but v0 scans one repo per invocation (no fleet-wide dashboard yet) and only bumps `<TargetFramework>` directly (it does not rewrite obsolete API usage). See [ROADMAP.md](ROADMAP.md) for what's built vs. planned, and [VISION.md](VISION.md) for the reasoning behind this project.

## Quick start

**As a GitHub Action** (the recommended way to run this in CI):

```yaml
- uses: winter-is-compiling/rollforward@v0.1.0   # pinned to a release: uses its pre-built binary
  with:
    command: scan        # or: remediate
    path: .              # optional — defaults to the workflow's own checkout
    fail-on: needs-review  # remediate only: none (default) | blocked | needs-review
    chain: true            # remediate only: keep going hop by hop while each is HighConfidence (default: false)
```

See [action.yml](action.yml) — it's a thin composite action. Pinned to a release tag it downloads that release's pre-built binary (checksum-verified); pinned to a branch or commit it builds the CLI from source. It's dogfooded against this repo's own fixtures in [`.github/workflows/self-test-action.yml`](.github/workflows/self-test-action.yml).

**Directly via the CLI** (for local use, or any CI system without an adapter yet) — download the binary for your platform from the [latest release](https://github.com/winter-is-compiling/rollforward/releases/latest), or run it from source:

```bash
rollforward scan <path>
rollforward remediate <path> [--fail-on none|blocked|needs-review] [--chain]

# from source:
dotnet run --project src/Rollforward.Cli -- scan <path>
dotnet run --project src/Rollforward.Cli -- remediate <path> [--fail-on none|blocked|needs-review] [--chain]
```

Exit codes: `0` success, `1` an error Rollforward could explain (bad path, project doesn't build, ...), `2` the verdict tripped `--fail-on`. By default a verdict never fails the run — whether "needs review" or "blocked" should turn your pipeline red is your policy, not ours.

### What `scan` looks like

```
Project:        ./src/MyService
Current:        net8.0
Target:         net10.0
Status:         ApproachingEol
EOL date:       10-11-2026
Days until EOL: 52
```

### What `remediate` looks like

```
Verdict: HighConfidence
Branch:  rollforward/upgrade-to-10
Reasons:
  - Build succeeded, existing tests passed (line coverage 100%), no manual-action markers — safe to auto-approve.
```

`Verdict` is one of `HighConfidence` (a PR is opened automatically via the `gh` CLI), `NeedsReview` (no PR is opened; the reasons say exactly what to check, and the migration is committed on the local branch for you to inspect), or `Blocked` (no PR either — e.g. no test project, tests that pass without executing any of your code, or the migration itself broke the build).

**Coverage.** While your tests run, Rollforward measures per-file line coverage of the code the migration recompiles (using [Coverlet](https://github.com/coverlet-coverage/coverlet), which the default xunit/NUnit/MSTest templates already reference as `coverlet.collector`). Tests that execute none of it → `Blocked`; coverage below `minCoverage` (default 50%) → `NeedsReview`, naming the least-covered files. If your test projects don't reference `coverlet.collector`, coverage can't be measured: the verdict is unchanged and says so explicitly.

> **Safety, in plain terms**: your source code never leaves your machine or CI runner — nothing is uploaded anywhere. `remediate` always works on a new branch, never your current one. Even on `HighConfidence`, nothing merges automatically — a PR is opened for you to review like any other. Full reasoning in [SECURITY.md](SECURITY.md).

## See it in action

[`rollforward-demo-fleetops`](https://github.com/winter-is-compiling/rollforward-demo-fleetops) is a small multi-project solution (domain, application, Azure adapters, minimal API; xUnit, Moq, Azure SDKs) on `net8.0`. Running `rollforward remediate` on it opened [this pull request](https://github.com/winter-is-compiling/rollforward-demo-fleetops/pull/1):

```
Verdict: HighConfidence
Branch:  rollforward/upgrade-to-10
Reasons:
  - Build succeeded, existing tests passed (line coverage 82.7%), no manual-action markers — safe to auto-approve.
```

The safety net matters as much as the happy path. While building this demo, an earlier version of its API tests pinned `Microsoft.AspNetCore.Mvc.Testing` to 8.0.x. After the bump, two tests failed on net10, so Rollforward returned `NeedsReview` and named the failing tests instead of opening a PR. The demo now makes that package follow the target framework, and the gap is tracked in [#1](https://github.com/winter-is-compiling/rollforward/issues/1).

## How it compares

|  | Rollforward | [dotnet-bumper](https://github.com/martincostello/dotnet-bumper) | [GitHub Copilot `@upgrade`](https://learn.microsoft.com/en-us/dotnet/core/porting/github-copilot-upgrade/how-to-upgrade-with-github-copilot) |
|---|---|---|---|
| Form | CLI + GitHub Action | .NET global tool | Agent in Visual Studio, VS Code, Copilot CLI |
| Upgrades `TargetFramework` | yes, one hop at a time (`--chain` to continue) | yes | yes |
| Also updates packages, `global.json`, Dockerfiles | not yet ([#1](https://github.com/winter-is-compiling/rollforward/issues/1)) | yes | assessed and planned as part of its workflow |
| Runs your tests | always; build + tests + coverage decide the verdict | optional (`--test`) | part of its guided or automatic workflow |
| Opens a PR | automatically, only on `HighConfidence` | not part of the tool as documented | works on a branch you choose up front |
| Hard cases | `NeedsReview` / `Blocked`, with reasons | best-effort, review the changes | interactive, you steer |

Rollforward's bet is narrow on purpose: do the mechanical bump, let your tests decide, and stay quiet unless the result is safe. If you want an interactive, AI-assisted migration, or package and Dockerfile updates today, the tools above fit better. This table reflects each project's public docs at the time of writing; corrections are welcome.

## Configuration

Optional `.rollforward.yml` at the root of the project you're scanning:

```yaml
target: next-lts      # or: an explicit version like "19", or: next-major
                       # defaults to next-lts if this file is absent

solution: MyApp.sln    # optional (a path relative to the repo, e.g. src/MyApp.sln) — only needed if the repo has more than one
                        # project/solution file at its root and Rollforward
                        # can't tell which one to build on its own

chain: true            # optional — keep upgrading hop by hop (net6 -> net8 -> net10)
                        # while each hop is HighConfidence; default: one hop per run

minCoverage: 50        # optional — line-coverage percentage of the migrated code
                        # below which a verdict is capped at NeedsReview (default 50;
                        # 0 turns that rule off — zero coverage is still Blocked)
```

## Requirements

- .NET SDK (whatever version(s) your project needs — Rollforward shells out to the real `dotnet build`/`dotnet test`, it doesn't reimplement them)
- `git`
- `gh` CLI, authenticated, if you want `remediate` to open pull requests automatically on a high-confidence verdict (without it, the branch is still created locally — you just won't get the PR)

## Why this exists, briefly

Framework/runtime version bumps are usually mechanically trivial, but sprint-based planning charges a full story point per service anyway — turning a simple upgrade into a 60–70 point, multi-team program. This is a solved problem inside a handful of companies large enough to have built their own fix for it (Google's internal "Rosie"/Large-Scale-Changes system); Rollforward exists for teams — manufacturing, medical devices, industrial equipment — that will never have the headcount to build that themselves, but where staying off end-of-life software is a real compliance and safety exposure, not just tech debt. Full reasoning in [VISION.md](VISION.md).

## Design principles

- **CI/CD-agnostic** — one core CLI, thin adapters per CI system (GitHub Actions today; Azure DevOps, Bitbucket, Jenkins planned).
- **Extensible by design** — starts with .NET, built around a language-provider interface so other ecosystems can be added without a redesign.
- **Confidence over blind automation** — auto-merge is never assumed; auto-*confidence* is the thing being computed.
- **Open by default** — the scanning and remediation engine is open source. See [LICENSE](LICENSE).

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) — how the CLI, adapters, and confidence scoring actually work
- [TESTING.md](TESTING.md) — the testing strategy, and how to run the fixture/reference-repo suites yourself
- [ROADMAP.md](ROADMAP.md) — what's built, what's next
- [VISION.md](VISION.md) — why this project exists and what it's optimizing for
- [CONTRIBUTING.md](CONTRIBUTING.md) — how to get involved
- [SECURITY.md](SECURITY.md) — the safety model, and how to report a vulnerability

## License

Apache License 2.0 — see [LICENSE](LICENSE).
