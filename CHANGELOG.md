# Changelog

## [Unreleased]

- Fixed: if the remediation branch (`rollforward/upgrade-to-N`) already existed, `remediate` failed to create it without saying so and committed the migration onto the branch you were on. A leftover local branch now makes it pick a free name (`-2`, `-3`, …, ignoring case). A branch of that name on a remote (an earlier run's PR is probably still open) stops the run with an explanation instead of opening a duplicate PR. A branch that still can't be created is an error before anything changes.
- When the migration breaks the build or the tests, the verdict now names packages that version with the framework (ASP.NET Core, EF Core, `Microsoft.Extensions.*`, `System.Text.Json`) and are still on the old framework's major version — a common cause of post-upgrade failures, such as `Microsoft.AspNetCore.Mvc.Testing` 8.0.x on net10.0. It is a hint on a failure, never a verdict on its own: only the projects that were bumped are looked at, packages with their own version line (for example `Microsoft.AspNetCore.OData`, `Microsoft.Extensions.Http.Resilience`) and entries that already choose a version under a `Condition` are left out, and anything unreadable is skipped rather than failing the run.
- New, opt-in: `--bump-packages` (`bumpPackages: true` in `.rollforward.yml`, `bump-packages: true` on the Action). If the framework bump alone breaks the build or tests, the framework-tied packages above are moved to the newest stable release on the target major (looked up on nuget.org) as a separate commit, and the build and tests run again. The bump is kept only if that passes, and the verdict lists every package that moved; otherwise the commit is dropped and the verdict says it was tried. Off by default. See the README's "Package bump" section for the limits.
- For provider authors: `RemediationOutcome` gained optional trailing members (`FrameworkAlignedPackages`, `PackagesBumped`, `UnhelpfulPackageBumps`), and `DotNetLanguageProvider` has a constructor taking `bumpPackages`. Existing constructor calls still compile; positional deconstruction and already-compiled providers need a rebuild.

## [0.1.0] — 2026-09-28

First release. **Pre-alpha, .NET only** — useful today on real repos, but expect rough edges.

### What it does

- **`rollforward scan <path>`** — reads every project's target framework(s) through MSBuild, checks them against [endoflife.date](https://endoflife.date), and shows the status, the next target, and the whole upgrade path (`net6.0 -> net8.0 -> net10.0`).
- **`rollforward remediate <path>`** — on a new branch, moves the repo one hop forward, commits it, builds, runs your own tests with line coverage, and gives a deterministic verdict with reasons: **HighConfidence** (a PR is opened via `gh`), **NeedsReview** or **Blocked** (no PR; the reasons say what to check).
  - `--chain` (or `chain: true`) keeps going hop by hop while each hop is HighConfidence; the PR covers those hops, one commit each.
  - `--fail-on blocked|needs-review` turns a verdict into exit code 2 for CI.
- **GitHub Action** (`action.yml`) — `scan`/`remediate` with `fail-on` and `chain` inputs. Pinned to a release, it downloads that release's pre-built binary instead of compiling Rollforward on every run.
- **Pre-built binaries** for Linux x64, Windows x64 and macOS (arm64, x64) — self-contained, no .NET runtime needed to run Rollforward itself.

### Handled, because real repos needed it

- Target frameworks declared in `Directory.Build.props` / `Directory.Packages.props`; multi-targeted projects (`<TargetFrameworks>`), conditional lists and platform suffixes.
- Repos with projects on different versions (the oldest moves first) and `netstandard` / .NET Framework projects (left alone, with a note).
- Several solutions at the root or in subfolders (`solution:` in `.rollforward.yml`).
- Build warnings and test failures that were already there before the migration are reported as such, not blamed on it.
- Tests that pass without executing the migrated code are *Blocked*, not trusted.

### Known limitations

- It changes **target frameworks only**: it doesn't update package references or rewrite obsolete API usage. Where that is needed, the build or the warnings say so, and the verdict is NeedsReview or Blocked.
- One repo per run; there is no fleet dashboard yet.
- Test projects are recognised by name (it contains "Test"). Coverage needs `coverlet.collector` (the default test templates include it) and is line coverage only.
- Your project's .NET SDKs must be installed where Rollforward runs; a `global.json` pin that isn't installed is reported, not worked around.
- A `<TargetFrameworks>` list built from a custom MSBuild property is refused rather than guessed at.
- GitHub Actions is the only CI adapter; elsewhere, run the CLI directly.

Tested against a fixture suite and real public repos (eShopOnWeb, eShop, Dapper, CliWrap and others) — see [MANUAL_TEST_PASS.md](MANUAL_TEST_PASS.md) and [reference-repos](reference-repos/README.md).
