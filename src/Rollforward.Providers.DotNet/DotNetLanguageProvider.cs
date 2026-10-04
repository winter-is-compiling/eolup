using System.Text.RegularExpressions;
using Rollforward.Core;
using Rollforward.Core.Config;
using Rollforward.Core.Infrastructure;
using Rollforward.Core.Models;
using Rollforward.Core.Providers;

namespace Rollforward.Providers.DotNet;

/// <summary>
/// The .NET implementation of ILanguageProvider — the only provider that exists
/// in v0. See ARCHITECTURE.md, "Language providers".
///
/// v0 scope: remediation bumps &lt;TargetFramework&gt; directly and does not rewrite
/// obsolete API usage. Wrapping `dotnet-upgradeassistant` was the original plan, but it
/// is officially deprecated, so any deeper fixes will be our own
/// narrow, rule-based ones. "Manual action markers" are approximated by
/// scanning the post-upgrade build output for obsolete-API and package
/// compatibility warnings.
/// </summary>
public sealed partial class DotNetLanguageProvider : ILanguageProvider
{
    public string ProductId => "dotnet";

    public string FormatVersion(string cycle) => CsProjHelper.CycleToTfm(cycle);

    public async Task<VersionDetection> DetectVersionAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateProjectsAsync(projectPath, cancellationToken);
        return DetectFrom(projectPath, evaluation);
    }

    /// <summary>Every project's evaluated TFM (test projects included), plus what couldn't be evaluated.</summary>
    private sealed record ProjectEvaluation(
        IReadOnlyDictionary<string, string> TfmByProject,
        int UnresolvedCount,
        RollforwardUserException? LastUnresolvedError);

    /// <summary>
    /// Evaluates every project, not just the first — found by testing against a real
    /// monorepo (dotnet/eShop) and a constructed case with two services on different
    /// versions under one path: "just take the first one found" silently reported
    /// one service's version for the whole directory, a confident-looking but wrong
    /// answer. A project that can't be evaluated at all (e.g. the SDK pinned in
    /// global.json isn't installed) is left out rather than aborting the whole scan
    /// over one unrelated project. Multi-targeted projects are evaluated like any other
    /// (their value is the whole list — see VersionPlanning).
    /// </summary>
    private static async Task<ProjectEvaluation> EvaluateProjectsAsync(string projectPath, CancellationToken cancellationToken)
    {
        var projects = CsProjHelper.FindProjectFiles(projectPath);
        if (projects.Count == 0)
            throw new RollforwardUserException($"No .csproj found under '{projectPath}'.");

        var resolved = new Dictionary<string, string>();
        RollforwardUserException? lastError = null;
        var unresolved = 0;

        foreach (var project in projects)
        {
            try
            {
                var tfm = await CsProjHelper.ReadTargetFrameworkAsync(project, cancellationToken);
                if (tfm is not null)
                    resolved[project] = tfm;
            }
            catch (RollforwardUserException ex)
            {
                lastError = ex;
                unresolved++;
            }
        }

        return new ProjectEvaluation(resolved, unresolved, lastError);
    }

    private static bool IsTestProject(string projectFile) =>
        Path.GetFileNameWithoutExtension(projectFile).Contains("Test", StringComparison.OrdinalIgnoreCase);

    private static VersionDetection DetectFrom(string projectPath, ProjectEvaluation evaluation)
    {
        // Test projects don't decide what version the repo is on.
        var nonTest = evaluation.TfmByProject
            .Where(kv => !IsTestProject(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (nonTest.Count == 0)
        {
            throw evaluation.LastUnresolvedError ?? new RollforwardUserException(
                $"Could not determine <TargetFramework> for any project under '{projectPath}' " +
                "(checked each project file and any Directory.Build.props/Directory.Packages.props up to the repo root).");
        }

        return VersionPlanning.DetectCurrent(projectPath, nonTest, evaluation.UnresolvedCount);
    }

    public async Task<RemediationOutcome> RemediateAsync(string projectPath, string targetVersion, CancellationToken cancellationToken = default)
    {
        var config = RollforwardConfigLoader.Load(projectPath);

        // Decide what this run changes before touching anything (no branch, no
        // build): one hop, applied to the projects on the repo's current — that is,
        // oldest — version. See VersionPlanning for the rules (multi-targeted projects
        // included). Projects that couldn't be evaluated never appear here, so they are
        // never rewritten.
        var targetTfm = CsProjHelper.CycleToTfm(targetVersion);
        var evaluation = await EvaluateProjectsAsync(projectPath, cancellationToken);
        var currentTfm = DetectFrom(projectPath, evaluation).Version;
        var projectsToBump = VersionPlanning.SelectProjectsToBump(evaluation.TfmByProject, currentTfm, targetTfm);
        if (projectsToBump.Count == 0)
        {
            throw new RollforwardUserException(
                $"Nothing to upgrade: the repo is on {currentTfm} and this step's target is {targetTfm}, " +
                "which isn't a newer version Rollforward can move it to.");
        }

        // A multi-targeted list is rewritten entry by entry in the file that spells it
        // out. If it's assembled from a property Rollforward doesn't follow, stop now —
        // before any branch, build or edit — rather than leave a half-migrated repo.
        var currentVersion = VersionPlanning.TryParse(currentTfm)!;
        var unwritable = projectsToBump
            .Where(b => b.MultiTarget && !CsProjHelper.CanRetargetFrameworks(b.File, currentVersion))
            .Select(b => Path.GetFileNameWithoutExtension(b.File))
            .ToList();
        if (unwritable.Count > 0)
        {
            throw new RollforwardUserException(
                $"Can't rewrite the <TargetFrameworks> of {string.Join(", ", unwritable)}: no {currentTfm} entry is written out " +
                "literally in the project or a Directory.Build.props/Directory.Packages.props above it (the list is probably " +
                "built from an MSBuild property). Nothing was changed. Move that entry by hand, or spell the list out in the project.");
        }

        // Resolve an explicit build target rather than handing `dotnet build` a bare
        // directory — see CsProjHelper.ResolveBuildTarget and MANUAL_TEST_PASS.md,
        // finding 2. This alone fixes the common case (one real solution plus an
        // unrelated project file, e.g. a docker-compose.dcproj); a repo with
        // genuinely multiple competing solutions still needs `solution:` set in
        // .rollforward.yml, and gets a clear error below if it isn't.
        var buildTarget = CsProjHelper.ResolveBuildTarget(projectPath, config.Solution);

        // Verify the project builds *before* touching anything — a pre-existing
        // build failure is a fundamentally different situation from one caused by
        // the migration itself, and should never be silently attempted against.
        var preflightBuild = await ProcessRunner.RunAsync("dotnet", ["build", buildTarget], projectPath, cancellationToken);
        if (!preflightBuild.Succeeded)
        {
            var combinedOutput = preflightBuild.StandardOutput + preflightBuild.StandardError;
            if (combinedOutput.Contains("MSB1011") || combinedOutput.Contains("MSB1050"))
            {
                // MSB1011 is what `dotnet build` reports for a bare/implicit
                // invocation; MSB1050 is what it reports when a directory is passed
                // explicitly as an argument (which is what happens here whenever
                // ResolveBuildTarget's fallback returns the directory itself) — same
                // underlying ambiguity, different code depending on how it's
                // invoked. Found by running scripts/test-against-reference-repos.sh
                // against eShopOnWeb's actual unmodified layout (two .sln files),
                // which only my manual testing had "fixed" by deleting files first.
                throw new RollforwardUserException(
                    $"'{projectPath}' contains more than one project or solution file, so Rollforward can't tell which " +
                    "one to build. Set `solution: <file>.sln` in .rollforward.yml to disambiguate.");
            }

            if (combinedOutput.Contains("MSB1003"))
                throw new RollforwardUserException(CsProjHelper.DescribeNoBuildTargetAtRoot(projectPath));

            throw new RollforwardUserException(
                $"Project at '{projectPath}' does not build in its current state — cannot safely attempt migration. " +
                "Fix the existing build before running `rollforward remediate`.");
        }

        // Baseline markers: warnings that already exist before we touch anything.
        // Real repos almost always carry some pre-existing obsolete-API or
        // package-vulnerability warnings unrelated to any particular migration —
        // found by running Rollforward against eShopOnWeb, which had 13 such
        // warnings on a totally ordinary .NET 8 build. Without this baseline,
        // every one of those would get misattributed to "this migration needs
        // review," which would push nearly every real repo into NeedsReview
        // regardless of whether the migration itself was actually safe —
        // directly undermining the premise that most migrations are trivial.
        var baselineMarkers = ExtractObsoleteWarnings(preflightBuild.StandardOutput)
            .Select(NormalizeForComparison)
            .ToHashSet();

        var testProjectExists = CsProjHelper.AnyTestProjectExists(projectPath);

        // The commit the migration starts from — the "untouched code" a failing test
        // run is compared against (see CompareWithBaselineAsync).
        var baseCommit = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], projectPath, cancellationToken))
            .StandardOutput.Trim();

        var branchName = await CreateRemediationBranchAsync(projectPath, $"rollforward/upgrade-to-{targetVersion}", cancellationToken);

        var changedFiles = new HashSet<string>();
        foreach (var bump in projectsToBump)
        {
            changedFiles.Add(bump.MultiTarget
                ? CsProjHelper.WriteTargetFrameworks(bump.File, currentVersion, targetTfm)
                : CsProjHelper.WriteTargetFramework(bump.File, bump.NewTfm));
        }

        await CommitMigrationAsync(projectPath, changedFiles, targetTfm, cancellationToken);

        var buildResult = await ProcessRunner.RunAsync("dotnet", ["build", buildTarget], projectPath, cancellationToken);
        var manualActionMarkers = buildResult.Succeeded
            ? ExtractObsoleteWarnings(buildResult.StandardOutput)
                .Where(m => !baselineMarkers.Contains(NormalizeForComparison(m)))
                .ToList()
            : [];

        bool? testsPassed = null;
        CoverageReport? coverage = null;
        TestComparison? testComparison = null;
        if (buildResult.Succeeded && testProjectExists)
        {
            // Collect coverage in the same run as the tests, into a temp directory
            // (never the repo — that would dirty the tree the PR is built from). It
            // only works when the test projects reference coverlet.collector (the
            // default xunit/nunit/mstest templates do); without it `dotnet test`
            // just warns and writes nothing, and coverage stays null = "not
            // measured", which the scorer reports rather than hides.
            var run = await RunTestsAsync(buildTarget, projectPath, collectCoverage: true, cancellationToken);
            try
            {
                testsPassed = run.Succeeded;

                // Coverage instrumentation can itself break tests that pass normally:
                // Coverlet injects a helper type into each instrumented assembly, and
                // tests that reflect over their own assemblies (architecture tests
                // are the classic case) then fail with a TypeLoadException. Found by
                // migrating Equinox: its tests pass on the migrated branch when run
                // plainly, but failed under --collect, producing a false NeedsReview.
                // So a failure here is never trusted on its own: confirm with a plain
                // run, which alone decides pass/fail. Coverage is then just "not
                // measured" — a measurement problem must not become a verdict.
                if (!run.Succeeded)
                {
                    using var plain = await RunTestsAsync(buildTarget, projectPath, collectCoverage: false, cancellationToken);
                    testsPassed = plain.Succeeded;

                    // Still failing: find out whether it was already failing before
                    // the migration touched anything.
                    if (!plain.Succeeded)
                        testComparison = await CompareWithBaselineAsync(
                            plain, baseCommit, branchName, buildTarget, projectPath, cancellationToken);
                }
                else
                {
                    var testProjectDirectories = CsProjHelper.FindTestProjectFiles(projectPath)
                        .Select(p => Path.GetDirectoryName(p)!)
                        .ToList();
                    coverage = CoberturaParser.Merge(
                        Directory.GetFiles(run.ResultsDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories),
                        projectPath, testProjectDirectories);
                }
            }
            finally
            {
                run.Dispose();
            }
        }

        return new RemediationOutcome(
            buildResult.Succeeded, testProjectExists, testsPassed, manualActionMarkers, branchName, coverage, testComparison);
    }

    /// <summary>
    /// Creates and switches to the branch the migration is committed on, and returns its name.
    /// The result of `git checkout -b` used to be ignored: when the branch already existed
    /// (an earlier run, or one fetched from the remote) the command failed silently and the
    /// migration was committed onto whatever branch the user was on — breaking the promise that
    /// remediation never touches the current branch. So an existing name, local or on any
    /// remote, is skipped in favour of "-2", "-3", ...; a checkout that still fails is an error.
    /// </summary>
    private static async Task<string> CreateRemediationBranchAsync(
        string projectPath, string baseName, CancellationToken cancellationToken)
    {
        var refs = await ProcessRunner.RunAsync(
            "git", ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], projectPath, cancellationToken);
        var taken = refs.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? r["refs/heads/".Length..]
                : r["refs/remotes/".Length..][(r["refs/remotes/".Length..].IndexOf('/') + 1)..]) // drop "<remote>/"
            .ToHashSet(StringComparer.Ordinal);

        var name = baseName;
        for (var suffix = 2; taken.Contains(name); suffix++)
            name = $"{baseName}-{suffix}";

        var checkout = await ProcessRunner.RunAsync("git", ["checkout", "-b", name], projectPath, cancellationToken);
        if (!checkout.Succeeded)
            throw new RollforwardUserException(
                $"Could not create the branch '{name}' for the migration: {checkout.StandardError.Trim()} Nothing was changed.");

        return name;
    }

    /// <summary>One `dotnet test` run: pass/fail, per-test failures, and where its result files are.</summary>
    private sealed class TestRun(bool succeeded, bool anyResults, IReadOnlySet<string> failed, string resultsDirectory) : IDisposable
    {
        public bool Succeeded { get; } = succeeded;
        public bool AnyResults { get; } = anyResults;
        public IReadOnlySet<string> Failed { get; } = failed;
        public string ResultsDirectory { get; } = resultsDirectory;

        public void Dispose()
        {
            try { Directory.Delete(ResultsDirectory, recursive: true); } catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs the tests, writing TRX result files (and, if asked, coverage) to a fresh
    /// temp directory — never into the repo. The caller disposes the result to
    /// delete that directory.
    /// </summary>
    private static async Task<TestRun> RunTestsAsync(
        string buildTarget, string projectPath, bool collectCoverage, CancellationToken cancellationToken)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "rollforward-tests-" + Guid.NewGuid().ToString("N"));
        List<string> args = ["test", buildTarget, "--logger", "trx", "--results-directory", resultsDirectory];
        if (collectCoverage)
            args.Add("--collect:XPlat Code Coverage");

        var result = await ProcessRunner.RunAsync("dotnet", args, projectPath, cancellationToken);

        var trxFiles = Directory.Exists(resultsDirectory)
            ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            : [];
        var (anyResults, failed) = TrxParser.Read(trxFiles);
        return new TestRun(result.Succeeded, anyResults, failed, resultsDirectory);
    }

    /// <summary>
    /// The tests fail on the migrated code — but were they already failing? Found
    /// across three real repos (Pass #5): one had an integration test needing
    /// infrastructure, one couldn't run its tests at all (runtime not installed), and
    /// all three were reported as "the migration may have broken the tests".
    ///
    /// Only done when the migrated run fails, so a passing migration pays nothing.
    /// Checks out the untouched commit (detached — the remediation branch is left as
    /// it is), runs the tests there, and always returns to the branch.
    /// </summary>
    private static async Task<TestComparison?> CompareWithBaselineAsync(
        TestRun migrated, string baseCommit, string branchName, string buildTarget, string projectPath,
        CancellationToken cancellationToken)
    {
        if (baseCommit.Length == 0)
            return null;

        var checkout = await ProcessRunner.RunAsync("git", ["checkout", "--quiet", "--detach", baseCommit], projectPath, cancellationToken);
        if (!checkout.Succeeded)
            return null; // can't get back to the untouched code: say nothing rather than guess

        TestRun baseline;
        try
        {
            baseline = await RunTestsAsync(buildTarget, projectPath, collectCoverage: false, cancellationToken);
        }
        finally
        {
            var back = await ProcessRunner.RunAsync("git", ["checkout", "--quiet", branchName], projectPath, CancellationToken.None);
            if (!back.Succeeded)
                throw new RollforwardUserException(
                    $"Rollforward checked out the untouched commit to compare test results but couldn't switch back to " +
                    $"'{branchName}': {back.StandardError.Trim()}. The migration is committed on that branch; check it out by hand.");
        }

        using (baseline)
        {
            if (baseline.Succeeded)
                return new TestComparison(BaselinePassed: true, [.. migrated.Failed], [], FailuresIdentified: migrated.AnyResults);

            if (!baseline.AnyResults || !migrated.AnyResults)
                return new TestComparison(BaselinePassed: false, [], [], FailuresIdentified: false);

            return new TestComparison(
                BaselinePassed: false,
                NewFailures: [.. migrated.Failed.Except(baseline.Failed).Order(StringComparer.Ordinal)],
                AlreadyFailing: [.. migrated.Failed.Intersect(baseline.Failed).Order(StringComparer.Ordinal)],
                FailuresIdentified: true);
        }
    }

    /// <summary>
    /// Commits the migration onto the remediation branch. Without a commit the
    /// branch has nothing ahead of the base, so pushing it and running
    /// `gh pr create` opens nothing — found by pushing a remediated fixture to a
    /// local bare remote: the branch arrived with zero commits.
    ///
    /// Stages only the files we rewrote, never `git add -A`: a target repo may not
    /// ignore bin/ and obj/, and build output must never end up in the PR. The
    /// commit is made even if the migration then fails to build or the tests
    /// fail — that's the migration's actual state, on a branch that's ours, and the
    /// verdict (not the commit) decides whether a PR is opened.
    /// </summary>
    private static async Task CommitMigrationAsync(
        string projectPath, IReadOnlyCollection<string> changedFiles, string targetTfm, CancellationToken cancellationToken)
    {
        if (changedFiles.Count == 0) return;

        var add = await ProcessRunner.RunAsync("git", ["add", "--", .. changedFiles], projectPath, cancellationToken);
        if (!add.Succeeded)
            throw new RollforwardUserException($"Could not stage the migration with git: {add.StandardError.Trim()}");

        // Nothing staged means every file already declared the target (a re-run).
        var staged = await ProcessRunner.RunAsync("git", ["diff", "--cached", "--quiet"], projectPath, cancellationToken);
        if (staged.Succeeded) return;

        // Use the user's own git identity when they have one. On a bare CI runner
        // there isn't one, and `git commit` refuses without it — so fall back to a
        // clearly-labelled identity via one-off -c flags (never touching git config).
        var hasIdentity =
            (await ProcessRunner.RunAsync("git", ["config", "user.email"], projectPath, cancellationToken)).Succeeded &&
            (await ProcessRunner.RunAsync("git", ["config", "user.name"], projectPath, cancellationToken)).Succeeded;
        List<string> identity = hasIdentity ? [] : ["-c", "user.name=Rollforward", "-c", "user.email=rollforward@users.noreply.github.com"];

        var commit = await ProcessRunner.RunAsync(
            "git", [.. identity, "commit", "-m", $"Rollforward: upgrade target framework to {targetTfm}"],
            projectPath, cancellationToken);
        if (!commit.Succeeded)
            throw new RollforwardUserException(
                $"Could not commit the migration (a git hook or signing setup may have rejected it): {commit.StandardError.Trim()}{commit.StandardOutput.Trim()}");
    }

    private static IReadOnlyList<string> ExtractObsoleteWarnings(string buildOutput) =>
        buildOutput
            .Split('\n')
            .Where(line => line.Contains("CS0618", StringComparison.Ordinal) || line.Contains("warning NU", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>
    /// Strips TFM tokens (e.g. "net8.0", "net10.0") before comparing baseline vs.
    /// post-migration markers. Found by testing against a real package-compatibility
    /// warning: its message text embeds the *consuming* project's current target
    /// framework ("...instead of the project target framework 'net8.0'"), so the
    /// literal warning text necessarily differs across a version bump even when the
    /// underlying issue is the exact same pre-existing one. Comparing on the raw
    /// text would make baseline-diffing silently useless for this whole warning
    /// class — normalizing out the TFM is what makes the comparison mean anything.
    /// </summary>
    private static string NormalizeForComparison(string marker) =>
        TfmTokenPattern().Replace(marker, "<tfm>");

    [GeneratedRegex(@"net\d+(\.\d+)*")]
    private static partial Regex TfmTokenPattern();
}
