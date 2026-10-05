# Vision

## The belief this project starts from

The cost of a framework or runtime upgrade should scale with how *risky* the change is — not with how many teams happen to own a service that needs it. Today, most engineering organizations charge the same story-point tax for a trivial, zero-breaking-change bump as they do for a genuinely difficult migration, simply because both get planned, estimated, reviewed, and merged the same way. That tax is what turns a mechanically simple activity into a multi-quarter, cross-team program.

Google's internal answer to this — the Large-Scale Changes system, and the "Churn Rule" that goes with it — is instructive: **the team that wants the change made bears the cost of making it**, not every downstream team that happens to be affected. A sweeping change gets sharded along ownership boundaries, tested automatically, and each owning team is asked only to approve a diff that has already been proven safe. Roughly 10–20% of all changes across Google's entire codebase happen this way.

That model works. It just isn't available to anyone who isn't Google, Meta, or Netflix — because building it requires a dedicated platform engineering investment that most companies, especially companies whose actual business is building machines, devices, or hardware rather than software, will never be able to justify.

Eolup exists to close that gap.

## Who we're building for

Not the companies most developer tooling is built for. We are deliberately *not* optimizing for engineering-first organizations — they either already have the platform engineering resources to solve this internally, or they're already being sold to by well-funded enterprise migration platforms.

We're building for the engineering team inside a company that makes medical devices, industrial equipment, or sensors — a team of 5 to 40 engineers, no dedicated platform function, for whom an unsupported runtime isn't just technical debt, it's a compliance and safety exposure they cannot responsibly carry. Reliability and trust matter more to this audience than developer convenience, and every design decision in this project should be weighed against that.

## What we're optimizing for, in order

1. **Trust** — your source code stays inside your own infrastructure. We compute confidence scores and metadata; we don't become a place where your proprietary code lives.
2. **Honest risk assessment over blind automation** — a migration that can't be safely verified should be flagged as blocked, not silently forced through. Telling you "we can't verify this safely because there's no test coverage" is a feature, not a failure.
3. **Near-zero cost for the genuinely trivial majority** — most services in a fleet don't need a story point's worth of judgment. They need someone to click approve on a diff that's already been proven safe.
4. **Respect for existing tooling and workflows** — we don't ask you to change your CI/CD platform, your VCS, or your review process. We fit into what you already have.

## Why open source

Trust has to be earned, not asserted — especially with an audience whose core professional instinct is skepticism toward anything that touches production systems without full visibility into how it works. The scanning and remediation engine is open source so that anyone can read exactly what it does to their code before they ever run it, and so that the community facing this exact pain — sprint-based planning colliding with mechanically simple migrations — can shape where this goes, not just consume what we ship.

## Where this goes

The long-term goal is simple to state and hard to earn: every engineering team, regardless of size or budget, should have access to the kind of fleet-wide migration tooling that today only a handful of the largest companies in the world can afford to build for themselves.
