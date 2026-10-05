# Manual Test Passes

> This log was written when the project was called Rollforward (renamed Eolup in v0.3.0). It is left as it was, so names and commands below are the old ones.

Running Rollforward against real public repos, not just synthetic fixtures — per the strategy in [TESTING.md](TESTING.md), layer 3. Each pass is a deliberate choice of repos shaped differently from whatever's already been tested, to keep surfacing new blind spots rather than re-confirming known-good cases.

## Pass #1 — eShopOnWeb

Per the v0 checklist in [ROADMAP.md](ROADMAP.md): run Rollforward against a real public .NET app and use human judgment to find gaps the fixture suite doesn't cover. Target: [dotnet-architecture/eShopOnWeb](https://github.com/dotnet-architecture/eShopOnWeb), a real Microsoft reference app on .NET 8 with ~10 projects and a genuine test suite — chosen specifically because it's more structurally complex than anything in `/fixtures`.

### Finding 1 — TFM detection failed entirely on this repo (fixed)

eShopOnWeb declares `<TargetFramework>` **once**, centrally, in `Directory.Packages.props` — not in any individual `.csproj`. This is a common, legitimate pattern in larger/longer-lived codebases (avoids repeating the same property in every project), and it's exactly the kind of codebase Rollforward's target users run. The original `CsProjHelper.ReadTargetFramework`, which parsed a `.csproj`'s XML directly, found nothing.

**Fix**: detection now asks MSBuild itself to evaluate the property (`dotnet msbuild <path> -getProperty:TargetFramework`) instead of re-implementing MSBuild's import/inheritance rules by hand. Writing has the equivalent problem — fixed by walking up from the project towards the repo root (bounded by the first `.git` found) to locate whichever file actually declares `<TargetFramework>`, and updating that one. Also made idempotent, since multiple projects can now resolve to the same shared file.

### Finding 2 — Ambiguous build target on a real repo layout (fixed in a follow-up session)

eShopOnWeb keeps a second solution file (`Everything.sln`) and a `docker-compose.dcproj` alongside the main `.sln` at the repo root. Plain `dotnet build <directory>` refuses to guess which one to build (`MSB1011`). This isn't a bug in the repo — both are legitimate, common patterns (an "everything included" solution variant; a Docker Compose orchestration project) — it's a real gap in Rollforward's assumption that a directory always resolves to exactly one build target.

**Originally not fixed in v0** (improved only the error message, tracked as a v1 item). **Fixed shortly after**: `CsProjHelper.ResolveBuildTarget` now resolves an explicit target — an explicit `solution:` in `.rollforward.yml` always wins; otherwise, if exactly one `.sln`/`.slnx` exists at the root, use it directly (this alone covers the common "one real solution plus one unrelated project file" case, since Rollforward stops asking `dotnet build` to auto-discover among *every* project-like file); otherwise fall back to the bare directory so the existing clear error still fires for genuinely ambiguous cases (more than one real solution) rather than silently guessing. Validated with a new fixture, `fixture-multi-root-projects`, built specifically to mirror this exact real-world pattern.

**A second sub-bug found once this became a repeatable check** (`reference-repos/`, see TESTING.md layer 4): eShopOnWeb's *actual* unmodified state has two real competing solutions, not one — so the fallback path (bare directory) is the one that fires, not the auto-resolved single-`.sln` path. Passing the directory *explicitly* as a build-target argument (which the fallback does) makes `dotnet build` emit a different error code, `MSB1050`, than a bare/implicit invocation does (`MSB1011`) — same underlying ambiguity, different code depending on how it's invoked. The error-detection check only recognized `MSB1011`, so this genuinely-ambiguous case fell through to a generic "doesn't build" message instead of the specific, actionable one. Fixed by checking for both codes. This was found by actually running the new `scripts/test-against-reference-repos.sh` against eShopOnWeb's real, unmodified clone — the original manual pass had only ever exercised this repo after already deleting the conflicting files by hand, which never exercised this exact code path.

### Finding 3 — Pre-existing warnings were being misattributed to the migration (fixed, and this was the important one)

With the ambiguity manually worked around, eShopOnWeb's ordinary .NET 8 build already had **13 warnings** unrelated to any migration — `SYSLIB0051` (an obsolete serialization constructor nobody touched), a handful of `xUnit2013` analyzer suggestions, and several `NU1902`/`NU1903` NuGet vulnerability advisories. The original marker-detection logic scanned only the *post-migration* build output, so every one of these would have been reported as a reason this migration "needs review" — despite having nothing to do with it.

This is a real problem for the product's central premise: if ordinary pre-existing noise routinely gets attributed to migrations, nearly every real repo lands in NeedsReview regardless of whether the actual version bump was safe, which defeats the entire point of confidence scoring.

**Fix**: `RemediateAsync` now captures markers from the pre-flight build (already run for the build-sanity check) as a baseline, and only reports markers present in the post-migration build that *aren't* in that baseline.

**A second, subtler bug surfaced while fixing this**: some warning messages embed the project's current target framework directly in their text (NU1701: `"...instead of the project target framework 'net8.0'"`). That means the exact same underlying pre-existing issue produces literally different text before and after a version bump, so naive exact-string comparison would never recognize it as "already existed" — silently defeating the whole baseline-diffing fix for this warning class. Fixed by normalizing out TFM tokens (`net\d+(\.\d+)*`) before comparing.

**Fixture consequences**: this required redesigning two fixtures to actually exercise the fix correctly rather than accidentally relying on the bug:
- `fixture-needs-review` now uses TFM-conditional file inclusion so its `[Obsolete]` call is genuinely absent at net8.0 and genuinely present only after the bump to net10.0 — a real "new" marker, not a marker that happened to always be there.
- `fixture-blocked-package` was renamed to `fixture-preexisting-warning` and its expected verdict changed from NeedsReview to **HighConfidence** — its `NU1701` warning is identical before and after (the referenced package is equally incompatible with both), so it now specifically proves the baseline-diffing behavior rather than testing something it was accidentally getting right for the wrong reason.

### Finding 4 (positive) — the safety-first design held up on genuine real-world complexity

With both blocking issues worked around, actually bumping eShopOnWeb to .NET 10 hits a real, hard compile error: `CS0433: The type 'Program' exists in both 'PublicApi' and 'Web'` — a genuine breaking change surfaced by the version bump, in `PublicApiIntegrationTests`. Running Rollforward's own unmodified CLI against this repo correctly reported:

```
Verdict: Blocked
Reasons:
  - Build failed after remediation — cannot verify the change is safe.
```

This is the outcome that matters most from this whole pass: on a real repo with a real breaking change, Rollforward did not report false confidence. It failed safe.

### Net effect

Three real bugs found, all three now fixed: MSBuild-based detection, baseline-diffing with TFM normalization, and explicit build-target resolution (the third was initially scoped and deferred to v1 rather than patched with a fragile heuristic, then properly implemented in a follow-up pass once the right design was clear). Full test suite (23 tests) green after all fixes, including a fifth fixture (`fixture-multi-root-projects`) added specifically to cover the third fix. Manual-pass checklist items in ROADMAP.md are complete.

---

## Pass #2 — famous OSS .NET libraries

eShopOnWeb is one real repo, and it's shaped like a web app. Deliberately went broader: cloned four well-known public .NET repos — [Dapper](https://github.com/DapperLib/Dapper), [CliWrap](https://github.com/Tyrrrz/CliWrap), [Polly](https://github.com/App-vNext/Polly), and [ShareX](https://github.com/ShareX/ShareX) — chosen because three of them are published *libraries* (a completely different shape than anything tested so far: eShopOnWeb and every fixture are single-target apps) and one is a real desktop application closer to Rollforward's actual target shape.

### Finding 1 — multi-targeted projects aren't supported at all (documented here; **now supported**)

Dapper, CliWrap, and Polly's actual library projects all declare `<TargetFrameworks>` (plural — e.g. Dapper: `net461;netstandard2.0;net8.0;net10.0`), not a single `<TargetFramework>`. This is the **default, expected shape for any published NuGet library** (it needs to support whatever frameworks its consumers use), which makes it a large real-world gap — arguably larger in raw prevalence than anything found in pass #1, though it matters less for Rollforward's actual target customer (internal, single-target services) than for someone pointing this at a public library.

Originally, hitting this produced a confusing, inaccurate error: `ReadTargetFrameworkAsync` asks MSBuild to evaluate the singular `TargetFramework` property, which comes back **empty** for a multi-targeted project (MSBuild only populates it once a specific TFM is selected for an inner build) — so Rollforward reported "could not determine `<TargetFramework>`", which reads as "this project has no version info" when the real story is "this project has several, and Rollforward doesn't yet support that."

**Fixed the error message, not the capability**: when the singular property comes back empty, `CsProjHelper.ReadTargetFrameworkAsync` now also checks the plural `TargetFrameworks` property, and if that has a value, throws a specific, honest error naming the actual frameworks and stating plainly that multi-targeting isn't supported yet. Actually supporting remediation of a multi-targeted project is real, non-trivial design work (which TFM in the list is "the one that's aging out"? do you touch just one entry or coordinate several? does a NuGet-published library even want the same trivial-bump treatment as an internal service?) — deliberately scoped out rather than rushed. Tracked in ROADMAP.md.

### Finding 2 — a traversal/meta-build project got picked as "the project to scan" (fixed)

Dapper's repo root has a `Build.csproj` using the `Microsoft.Build.Traversal` SDK — a project whose entire job is referencing every other project (`benchmarks/**/*.csproj`, `Dapper*/*.csproj`, `tests/**/*.csproj`) rather than containing any compilable code of its own. It has no `<TargetFramework>` by nature. Rollforward's "pick the first non-test `.csproj` found" heuristic picked this up as if it were a real candidate, producing the same confusing "could not determine" error — for entirely the wrong reason (this isn't a project with a version at all, not a project Rollforward should ever be looking at).

**Fixed**: `CsProjHelper.FindProjectFiles` now excludes any `.csproj` whose SDK is `Microsoft.Build.Traversal` at the source, so it's filtered out everywhere a project list gets built — detection, the "does a test project exist" check, and the TFM-rewrite loop during remediation. After this fix, Dapper's error correctly names the real library project (`Dapper.csproj`) and its real problem (multi-targeting, finding 1) instead of a nonexistent one.

### Finding 3 — a pinned SDK version that isn't installed produced a misleading error (fixed)

Polly's `global.json` pins an exact SDK patch version (`10.0.401`) that wasn't installed on the test machine (which had `10.0.400` and `9.0.201`). This makes `dotnet msbuild` fail to even start — a completely different problem from "no TargetFramework declared" — but since both failure modes made `ReadTargetFrameworkAsync`'s calls come back empty/unsuccessful, they produced the *identical* generic error message, hiding a purely environmental, easily-fixable cause (install the pinned SDK) behind a message that implied something wrong with the project itself.

**Fixed**: `CsProjHelper.ThrowIfSdkResolutionFailure` checks the MSBuild failure output for the telltale "Requested SDK version" text and, if found, throws a specific error naming the real cause and including the actual SDK-resolution details from `dotnet` itself, rather than falling through to the generic message.

### Finding 4 (positive) — two things already worked correctly that hadn't been tested

- **Windows-suffixed TFMs** (ShareX targets `net10.0-windows10.0.22621.0` and `net10.0-windows`) resolved correctly on the first try — `EolEvaluator`'s regex-based version extraction naturally stops at the first non-numeric character, so it correctly pulled `10.0` and matched the right EOL cycle without any special-casing needed.
- **Multi-solution-file ambiguity** (ShareX also keeps two real `.sln` files at its root, like eShopOnWeb originally did) produced the exact same, already-correct disambiguation error as pass #1 — confirming that fix generalizes rather than being an eShopOnWeb-specific patch.

### Finding 5 — a monorepo would have silently reported the wrong version for most of its services (fixed — the most serious finding of this pass)

Went looking specifically for the one dimension nothing had stress-tested yet: a real monorepo with several independently-versioned services, to check whether Rollforward's "pick the first non-test, non-traversal `.csproj` found" heuristic would fail loud (like every other gap found so far) or silently give a wrong answer. Tried [eShopOnContainers](https://github.com/dotnet-architecture/eShopOnContainers) first — archived, moved to [dotnet/eShop](https://github.com/dotnet/eShop) — but that repo's own ~28 projects all happen to agree on `net10.0` (mobile client aside), so it didn't actually expose anything by itself.

Constructed a minimal repro instead: two tiny services under one shared directory, one on `net8.0`, one on `net10.0`. Result: `rollforward scan` reported `net8.0` for the whole directory — **silently**, with no error, no warning, nothing to indicate a second service existed on a completely different version. This is meaningfully worse than every other gap found in either pass: everything else has failed with a clear, loud error. This one would have handed back a confident-looking, plausible, wrong answer.

**Fixed**: `DetectVersionAsync` now evaluates *every* non-test, non-traversal candidate project under the given path (not just the first), and only proceeds if they all agree. Projects that can't be evaluated at all (e.g. they multi-target — finding 1) are excluded from the comparison rather than aborting the whole scan, since real repos legitimately mix single- and multi-target projects (eShop's own single-target services alongside its multi-target MAUI client). If the resolvable candidates disagree, Rollforward now refuses with a clear error naming each project and its version, rather than picking one arbitrarily.

Captured as a permanent fixture, `fixture-version-mismatch` (two services, `net8.0` and `net10.0`, under one directory) — the real repos tested didn't happen to expose this on their own, so a constructed case was the only reliable way to lock in the fix. `dotnet/eShop` was also added to `reference-repos/` as the positive counterpart: many real projects, correctly agreeing, should never trigger the new check as a false positive.

### Net effect

Three real bugs fixed (traversal-project exclusion, SDK-resolution-failure messaging, silent version-mismatch on multi-project paths — the most serious of the three), one real capability gap properly identified, scoped, and documented rather than rushed (multi-targeting support), and two behaviors confirmed already-correct under new real-world conditions they'd never been tested against (Windows-suffixed TFMs, multi-solution ambiguity generalizing). One new fixture (`fixture-version-mismatch`) and two new `reference-repos/` entries (Dapper, eShop) added as permanent regression coverage.

---

## Pass #3 — closing the last known blind spot before open-source launch

Every test in Pass #1 and Pass #2 — real repos and constructed fixtures alike — started from a project on `net8.0`. That's not because .NET 8 is special, it's just what kept getting reached for. This left `TargetVersionResolver`'s "skip non-LTS, resolve to the next real LTS" logic, and `EolEvaluator`'s status classification, completely unexercised from any other starting point. As part of a deliberate pre-launch check (not a bug report — a gap identified by inspection), tested against real repos on `net9.0` specifically.

**Result: clean.** [`PhotinoBlazorNet9Template`](https://github.com/NathanJKW/Photino.Blazor.net9-template) (a real, single-project, single-TFM Photino+Blazor desktop app template, 58 stars) — `scan` correctly resolved `net9.0` → target `10` (skipping non-LTS `9`), pulled the real EOL date live from endoflife.date, and `remediate` ran the full pipeline (preflight build → branch → TFM rewrite → post-migration build → test check) cleanly through to **Blocked** ("no test project found") — the correct, safe outcome for a repo with no tests, not a bug. A second repo ([FritzAndFriends/SharpSite](https://github.com/FritzAndFriends/SharpSite) at a historical all-net9.0 commit) hit a **true-positive** pre-existing build failure (a real `NU1902` vulnerability-as-error in the repo's own baseline, independently confirmed by building it directly) — exactly the "fail closed on a pre-existing problem" behavior the preflight check is designed for.

One minor, pre-existing cosmetic inconsistency surfaced (not a regression from this pass — it would show up identically on the already-well-tested net8→10 path too): `scan` prints `Current: net9.0` (a TFM string) next to `Target: 10` (a bare cycle number, since `TargetVersionResolver` operates on endoflife.date's cycle-naming, not TFM-naming). Fixed afterwards with a small "format this cycle for display" hook on `ILanguageProvider` (`FormatVersion`), rather than pushing .NET-specific formatting into the language-agnostic CLI layer — so `scan` now prints `Target: net10.0`.

`PhotinoBlazorNet9Template` added to `reference-repos/` — it happens to close two items that were both still open on that corpus's "candidates worth considering" list at once (a non-LTS starting version, and a repo with no test project at all).

### Net effect

No bugs found — this was the first "clean" pass since testing against real repos began, which is itself useful signal: the core `.NET` provider has now been validated against a complex single app, four published libraries, a 28-project monorepo, and two non-LTS-starting-point repos, and the only unresolved issues are the already-tracked, already-documented ones (multi-target support, the cosmetic Current/Target formatting nit).

## Pass #4 — the pull-request flow, end to end

**Why**: every earlier pass stopped at the verdict. The step that turns a `HighConfidence` verdict into a PR (`git push` + `gh pr create`) had only ever run against a repo with no remote, so its success path had never been exercised at all.

**Setup**: a throwaway *private* repo created for the test (a minimal .NET 8 app, one xunit test, `coverlet.collector`, a `.gitignore` for `bin/`/`obj/`). Never a repo we don't own. Fresh `git clone`, then the real CLI: `scan`, then `remediate`.

**Finding (before the fix)**: pushing a remediated fixture to a local bare remote showed the pushed branch was **0 commits ahead of the base**. `remediate` edited the project files but never committed them, so on GitHub `gh pr create` would have had nothing to open a PR for. The headline feature did not work. No fixture test looked at git state after remediation, which is how it survived every earlier pass.

**Fix**: the migration is now committed on its branch — staging only the files Rollforward rewrote (never `git add -A`, so build output can't leak in), and falling back to a `Rollforward` commit identity via one-off `-c` flags when git has none (a bare CI runner), without touching git config. Regression test: `Remediation_CommitsTheMigrationOntoItsBranch_TouchingOnlyProjectFiles`.

**Result after the fix** (verified on GitHub, not just from the CLI's output):
- `scan`: `net8.0` → target `net10.0`, `ApproachingEol`, exit 0.
- `remediate`: `HighConfidence` (line coverage 100%), PR opened against `master`.
- The PR has exactly one commit, two changed files (both `.csproj`), `+2 −2`, the account's noreply identity, and no `bin/`/`obj/` content.

**Known cosmetic nit (not fixed)**: the PR body states the verdict reason twice (the intro sentence and the bulleted reason say nearly the same thing).

## Pass #5 — migration outcome survey (9 real repos)

**Why**: a spike on wrapping `dotnet-upgradeassistant` needed evidence on *why* real migrations land NeedsReview/Blocked before building any automated fixer. Method: screen public business-style .NET repos (Web APIs, clean architecture, CQRS) for a single-target, older-TFM layout with a test project; clone each at a pinned commit into a temp folder, **remove `origin` and assert no remote remains before running anything**; run `scan` and `remediate` locally; keep the log; delete the clone. Nothing was pushed and no PR was possible. (Nine repos survived the screen out of ~70 candidates — most active repos are already on net9/net10 or multi-target.)

| Repo (pinned) | From | Outcome | Real cause |
|---|---|---|---|
| Equinox (`fde9a95`) | net9 | NeedsReview (tests failed) | **Rollforward's own bug**: coverage instrumentation broke a reflection-based architecture test. Tests pass on the migrated branch when run plainly. Fixed (81a466f). |
| EventualShop (`d63e424`) | net8 | NeedsReview (tests failed) | An integration test **already fails on the untouched code** (needs infrastructure). Not a migration regression (since fixed). |
| run-aspnetcore (`4b02c65`) | net5 | NeedsReview (tests failed) | Tests can't run before or after: the net5/net6 runtime isn't installed. Also targeted `net6.0`, itself long EOL (since fixed). |
| TaskoMask (`32c1697`) | net8 | Refused at detection | A `net6.0` build project among ~36 `net8.0` projects (since fixed). |
| iayti/CleanArchitecture (`199363f`) | net6 | Refused at detection | `netstandard2.1` Domain library among `net6.0` projects (since fixed). |
| PeakLimsApi (`3794660`) | net9 | Refused at detection | `netstandard2.1` SharedKernel among `net9.0` projects (since fixed). |
| adnc (`58c015a`) | net8 | "Does not build" (misleading) | Six solutions, none at the root: `MSB1003`, reported as a build failure (since fixed). |
| clean-architecture-manga (`68b1d5a`) | net7 | "Does not build" | The .NET 10 SDK's newer analyzers turn a `CA2017` into an error on net7-era code; the repo builds with its own SDK. Environmental; also targeted `net8.0`, ~51 days from EOL (since fixed). |
| dev-store (`8e88a0c`) | net9 | `scan` error | `global.json` pins SDK 9.0.302, not installed here. The known, correctly-reported environmental case. |

**What this says**
- **No migration in this sample failed because of anything the migration itself changed.** Every non-success was Rollforward's own gap, a wrong attribution, or the environment. So the evidence does *not* support building a code-fixer next: the tool isn't yet reaching the point where fixer-worthy failures show up.
- **The biggest blocker is detection, not migration**: 3 of 9 repos were refused before any change was attempted, all by projects that legitimately differ.
- The "tests failed" verdicts (3 of 3) were all wrong or unattributable. One was a real bug in the coverage work, now fixed and pinned by `fixture-coverage-hostile-test`.

**Caveats, honestly**: n = 9, one machine (Windows; SDKs 8/9/10 installed), popular-ish public repos rather than internal services, and none reached a passing baseline followed by a genuine migration break — so this says nothing yet about how *often* real API breaks occur. Logs were kept in the session scratchpad, not committed.

### Pass #5 follow-up — the same repos after one-hop-per-run (ade3607) and the hang fix

| Repo | Before | After |
|---|---|---|
| iayti/CleanArchitecture (net6 + `netstandard2.1` Domain) | Refused at detection | **HighConfidence**: one hop net6 → net8, the netstandard library untouched, noted in `scan`. (Coverage reported "not measured".) |
| PeakLimsApi (net9 + `netstandard2.1` SharedKernel) | Refused at detection | Gets past detection; now stops on the genuine next issue — several solution files, asks for `solution:` (the correct message this time). |
| TaskoMask (`net6.0` build project among ~36 `net8.0`) | Refused at detection | Oldest-first: `Upgrade path: net6.0 -> net8.0 -> net10.0`, only the net6 project moves. **NeedsReview: tests failed** — unattributable at the time (it couldn't tell a migration regression from tests that already fail, e.g. needing a database). |
| run-aspnetcore / manga | `Target: net6.0` / `net8.0` with no context | Same target, now with `Upgrade path: net5.0 -> net6.0 -> net8.0 -> net10.0`. |

**A second real bug, found by this re-run**: TaskoMask made the CLI hang for 20+ minutes with no child process running. `ProcessRunner`'s 5-minute timeout only guarded the wait for the process to *exit*; once it had exited, reading its output waited for the pipes to close with no limit, and a leftover background process can hold them open indefinitely (the same class as the earlier CI hang — disabling MSBuild node reuse closed only one cause). Fixed: output is buffered chunk by chunk, and after exit the pipes get a bounded grace period (30s) before the run carries on with what it captured; our builds also set `UseSharedCompilation=false` so they don't leave compiler servers behind. Regression test `ReturnsPromptlyWithCapturedOutput_WhenABackgroundChildKeepsThePipeOpen` reproduced the hang exactly (19.3s wait) before the fix.

### Pass #5 follow-up 2 — attributing test failures and nested solutions

| Repo | Before | After |
|---|---|---|
| EventualShop | NeedsReview: "test suite failed … needs human judgment" | NeedsReview: **no test broke because of the migration**; the one failing integration test (`CartDetailsShouldBeProjectedWhenCartCreated`) was already failing on the untouched code |
| TaskoMask | NeedsReview: "test suite failed …" | NeedsReview: **no test broke because of the migration**; all 39 failures (integration tests needing infrastructure) were already failing before it |
| adnc | "Does not build in its current state" (wrong) | "No solution or project file at the root … 6 solution files below it (src/Adnc.sln, …). Set `solution:`" |

How the before/after test comparison works: only when the migrated tests fail, Rollforward checks out the untouched commit (detached), runs the same tests, returns to the migration branch, and compares per-test outcomes from the TRX result files. Passing migrations pay nothing extra. Both real repos were left on the migration branch afterwards.

## Pass #6 — multi-targeted projects against the libraries that used to be refused

Same method as Pass #5 (local clones, `origin` removed and asserted before anything runs, clones deleted afterwards).

| Repo | Before multi-target support | After |
|---|---|---|
| Dapper (`8becae8`) | Refused: "Only 1 of 8 projects could be evaluated" | `net5.0` (Dapper.Rainbow's oldest entry) → `net6.0`, path to `net10.0` shown; builds; **NeedsReview: no test broke because of the migration** — 742 tests need a SQL Server and fail identically on the untouched code |
| CliWrap (`804ad88`) | Refused (multi-target) | `net6.0` entry → `net8.0`, the list's `netstandard2.0;netstandard2.1;net7.0;net10.0` entries untouched; builds; **NeedsReview: no test broke** — 4 cancellation tests already failing on Windows |
| dotnet/eShop (`b4a4087`) | net10.0 (MAUI client skipped as unreadable) | net10.0 with the MAUI client now read — platform-suffixed entries recognised as the same version, no spurious "different versions" note; nothing to upgrade |

**Finding, fixed during the pass**: the first version of the rule refused any *single-target* .NET Framework project as "can't order", on the theory that it's an app awaiting a Framework → modern migration. Both libraries disproved that at once: `Dapper.EntityFramework` (`net461`) and `CliWrap.Signaler` (`net35`) are deliberate Framework components of modern libraries, and refusing the whole repo over them helped no one. They are now left alone with a note, exactly like netstandard; only a TFM Rollforward doesn't recognise still refuses.

**Worth knowing**: in both libraries, the oldest modern entry sits in one sub-library (Dapper.Rainbow still lists `net5.0`), so one-hop-per-run starts there — a correct but slow path to `net10.0` for a repo whose main library is already on it. That's the agreed model working as designed; automatic chaining is what would shorten it.

**Update**: automatic chaining is now available as an opt-in (`chain: true` / `--chain`), which is what shortens the slow one-hop-per-run path above. Checked with the real CLI against `fixture-chain-stops` and a local bare remote standing in for `origin`: step 1 (net6.0 → net8.0) HighConfidence, step 2 (net8.0 → net10.0) stopped on a broken test, and only the step-1 branch was pushed.
