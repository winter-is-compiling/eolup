# Contributing

Rollforward's v0 is real and working — `rollforward scan` and `rollforward remediate` for .NET, validated against synthetic fixtures and several real public repos (see [MANUAL_TEST_PASS.md](MANUAL_TEST_PASS.md)). This document describes how to build it, test it, and where contributions are most valuable right now.

## Building and testing locally

```bash
dotnet build
dotnet test
```

That runs the full fast suite (unit tests + fixture-based system tests, ~90 tests). Before opening a PR, also consider running the slower, non-gating real-world check:

```bash
scripts/test-against-reference-repos.sh
```

See [TESTING.md](TESTING.md) for what each layer actually covers and why they're kept separate.

## Where contributions are most valuable right now

- **New language providers** — implementing the `ILanguageProvider` interface (see [ARCHITECTURE.md](ARCHITECTURE.md#language-providers)) for an ecosystem beyond .NET. Java and Node/Angular are the next planned targets (see [ROADMAP.md](ROADMAP.md)) — Angular in particular would validate the chained/stepwise migration design for real, since it can't skip major versions the way .NET usually can.
- **New CI adapters** — thin wrappers around the CLI for a platform not yet covered (Azure DevOps, Bitbucket, Jenkins, GitLab CI). Look at [action.yml](action.yml) for the pattern: the CLI does all the real work, the adapter just maps that platform's syntax to CLI arguments.
- **Reference repos** — see [reference-repos/README.md](reference-repos/README.md). If you find a real public repo that trips up Rollforward in a new way, that's exactly the kind of finding that's driven most of the real bug fixes so far — please open an issue with the repo, the commit, and what happened, even if you don't have time to fix it yourself.

## Reporting issues

If something in the scan or remediation logic produces a wrong or misleading confidence verdict, that's the most important class of bug this project can have — worse than a crash, since it erodes the trust the whole tool depends on. Please open an issue with:
- The repo and commit (or a minimal repro) that triggered it
- What Rollforward reported vs. what you expected
- Whether it failed loud (a clear error) or silently gave a wrong-looking answer — the latter is especially important to flag

For anything security-sensitive, see [SECURITY.md](SECURITY.md) instead of a public issue.

## Code of conduct

Be direct, be kind, assume good faith. This project exists to take a real, well-documented pain point seriously — treat contributors and issue reporters the way you'd want to be treated raising a real problem.
