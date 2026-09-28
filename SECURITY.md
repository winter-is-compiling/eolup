# Security Policy

Rollforward shells out to `git`, `dotnet`, and (optionally) `gh` against your own repository, and can create branches and open pull requests. Taking that seriously:

- Your source code never leaves your machine or CI runner — nothing is uploaded to any Rollforward-operated service. See [ARCHITECTURE.md](ARCHITECTURE.md).
- `remediate` always works on a new branch, never your current one, and only opens a PR on a `HighConfidence` verdict — nothing merges without your review.
- The GitHub Action adapter ([action.yml](action.yml)) only uses ambient `gh`/GitHub Actions authentication already present in your workflow; it never embeds or requests credentials of its own.

## Reporting a vulnerability

If you find a security issue — something that could let Rollforward modify, exfiltrate, or execute against a repo in a way you didn't intend, or a credential-handling problem — please **do not open a public issue**. Instead, report it privately via GitHub's [private vulnerability reporting](https://github.com/winter-is-compiling/rollforward/security/advisories/new) on this repo, or open a regular issue asking for a private channel if that's unavailable.

Please include:
- What you found and why it's a security concern (not just a correctness bug — see [CONTRIBUTING.md](CONTRIBUTING.md) for those)
- Steps to reproduce, if possible
- The version/commit you tested against

We'll acknowledge reports as quickly as we can given this is currently a small, early-stage project — but security reports get priority over everything else on the roadmap.

## Scope

This applies to the Rollforward CLI, the GitHub Action adapter, and this repository's own CI. It does not cover the third-party services Rollforward depends on (endoflife.date, GitHub itself, the .NET SDK) — please report issues with those directly to their maintainers.
