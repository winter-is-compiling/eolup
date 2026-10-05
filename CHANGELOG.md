# Changelog

## [Unreleased]

## [0.3.0] — 2026-10-05

**Rollforward is now Eolup** — "continuous EOL upgrade". The name was changed before the first Marketplace listing: it collided with the .NET `RollForward` runtime setting in search, and it is tied to .NET and to accounting and database jargon, while the project is meant to cover more ecosystems. There are no other changes in behaviour, but the rename touches names you may have written down, so this is a minor release.

### Changed

- **Command:** `rollforward` is now `eolup` (`eolup scan <path>`, `eolup remediate <path>`). Release binaries are `eolup-linux-x64`, `eolup-osx-arm64`, `eolup-osx-x64` and `eolup-win-x64`.
- **Config file:** `.eolup.yml`. A `.rollforward.yml` is still read when there is no `.eolup.yml`, so existing repos keep working; the new name wins if both exist.
- **Branches and commits:** the remediation branch is `eolup/upgrade-to-N`, and commits and pull requests are labelled Eolup. A leftover `rollforward/upgrade-to-N` branch from an earlier run isn't recognised as the same upgrade, so a repo with one open could get one extra PR the first time.
- **GitHub Action:** named **Eolup** (`uses: winter-is-compiling/eolup@v0.3.0`); inputs are unchanged. The repository was renamed, and GitHub redirects the old URL, so workflows that reference the old name keep working.
- **For provider authors:** the namespaces, assemblies and types moved from `Rollforward.*` to `Eolup.*` (`RollforwardEngine` is `EolupEngine`, `RollforwardConfig` is `EolupConfig`, and so on). Recompile against the new names.

## [0.2.0] — 2026-10-05

An opt-in way to fix upgrades that fail because of packages left on the old framework, and a fix for a safety promise `remediate` wasn't keeping. **Pre-alpha, .NET only.**

### New

- **`--bump-packages`** (`bumpPackages: true` in `.rollforward.yml`, `bump-packages: true` on the Action). If the framework bump alone breaks the build or the tests, the packages that version with the framework (ASP.NET Core, EF Core, `Microsoft.Extensions.*`, `System.Text.Json`) are moved to the newest stable release on the target major, as a separate commit, and the build and tests run again. The bump is kept only if that passes, and the verdict lists every package that moved so the PR shows exactly what changed. Otherwise the commit is dropped, anything you had uncommitted is left alone, and the verdict says the bump was tried. Off by default. The README's "Package bump" section lists the limits (nuget.org only, no `$(Property)` versions, shared props files only when every project is being bumped).
- **A hint when an upgrade fails.** When the migration breaks the build or the tests, the verdict now names packages that version with the framework and are still on the old major, a common cause of post-upgrade failures (for example `Microsoft.AspNetCore.Mvc.Testing` 8.x on net10.0). It is only a hint: it never changes a verdict, it is only shown when the migration is the likely cause, and it ignores packages with their own version line (such as `Microsoft.AspNetCore.OData`) and entries that already choose a version per target framework.

### Fixed

- If the remediation branch (`rollforward/upgrade-to-N`) already existed, `remediate` failed to create it without saying so and committed the migration onto the branch you were on. A leftover local branch now makes it pick a free name (`-2`, `-3`, …, ignoring case). A branch of that name on a remote (an earlier run's PR is probably still open) stops the run with an explanation instead of opening a duplicate PR. A branch that still can't be created is an error before anything changes.

### For provider authors

- `RemediationOutcome` gained optional trailing members (`FrameworkAlignedPackages`, `PackagesBumped`, `UnhelpfulPackageBumps`), and `DotNetLanguageProvider` has a constructor taking `bumpPackages`. Existing constructor calls still compile; positional deconstruction and already-compiled providers need a rebuild.

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
