using System.Text.RegularExpressions;
using Eolup.Core;
using Eolup.Core.Config;
using Eolup.Core.Infrastructure;
using Eolup.Core.Models;
using Eolup.Core.Providers;

namespace Eolup.Providers.DotNet;

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
    private readonly DotNetProviderOptions _options;
    private IPackageVersionSource? _versionSource;

    public DotNetLanguageProvider() : this(new DotNetProviderOptions())
    {
    }

    /// <param name="bumpPackages">
    /// Opt in to the package bump: when the framework bump alone breaks the build or tests, move the
    /// stale framework-aligned packages to the target major and re-run (`bumpPackages: true` in
    /// .eolup.yml does the same). Off by default.
    /// </param>
    public DotNetLanguageProvider(bool bumpPackages) : this(new DotNetProviderOptions { BumpPackages = bumpPackages })
    {
    }

    public DotNetLanguageProvider(DotNetProviderOptions options)
    {
        _options = options;
    }

    internal DotNetLanguageProvider(bool bumpPackages, IPackageVersionSource versionSource) : this(bumpPackages)
    {
        _versionSource = versionSource;
    }

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
        EolupUserException? LastUnresolvedError);

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
            throw new EolupUserException($"No .csproj found under '{projectPath}'.");

        var resolved = new Dictionary<string, string>();
        EolupUserException? lastError = null;
        var unresolved = 0;

        foreach (var project in projects)
        {
            try
            {
                var tfm = await CsProjHelper.ReadTargetFrameworkAsync(project, cancellationToken);
                if (tfm is not null)
                    resolved[project] = tfm;
            }
            catch (EolupUserException ex)
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
            throw evaluation.LastUnresolvedError ?? new EolupUserException(
                $"Could not determine <TargetFramework> for any project under '{projectPath}' " +
                "(checked each project file and any Directory.Build.props/Directory.Packages.props up to the repo root).");
        }

        return VersionPlanning.DetectCurrent(projectPath, nonTest, evaluation.UnresolvedCount);
    }

    /// <summary>The longest a repo's own build, and its test run, may take: what the caller asked for, else the repo's config, else the default.</summary>
    private sealed record Timeouts(TimeSpan Build, TimeSpan Test);

    public async Task<RemediationOutcome> RemediateAsync(string projectPath, string targetVersion, CancellationToken cancellationToken = default)
    {
        var config = EolupConfigLoader.Load(projectPath);
        var timeouts = new Timeouts(
            _options.BuildTimeout ?? TimeSpan.FromMinutes(config.BuildTimeoutMinutes),
            _options.TestTimeout ?? TimeSpan.FromMinutes(config.TestTimeoutMinutes));

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
            throw new EolupUserException(
                $"Nothing to upgrade: the repo is on {currentTfm} and this step's target is {targetTfm}, " +
                "which isn't a newer version Eolup can move it to.");
        }

        // Every project this run moves needs a declaration Eolup can rewrite: a singular
        // <TargetFramework>, or a plural <TargetFrameworks> (a list is rewritten entry by entry,
        // and a one-entry list stays a list), spelled out in the project or a shared props file.
        // A framework set through an import, or assembled from a property Eolup doesn't follow,
        // can't be rewritten. Stop now — before the preflight build, any branch or any edit —
        // rather than crash halfway and leave a half-migrated repo.
        var currentVersion = VersionPlanning.TryParse(currentTfm)!;
        var unwritable = projectsToBump
            .Where(b => !CsProjHelper.CanRewriteTargetFramework(b, currentVersion))
            .Select(b => Path.GetFileNameWithoutExtension(b.File))
            .ToList();
        if (unwritable.Count > 0)
        {
            throw new EolupUserException(
                $"Can't rewrite the target framework of {string.Join(", ", unwritable)}: no {currentTfm} entry is written out " +
                "literally as <TargetFramework> or <TargetFrameworks> in the project or a Directory.Build.props/" +
                "Directory.Packages.props above it (it is probably set through an import, or built from an MSBuild property). " +
                "Nothing was changed. Move that entry by hand, or spell it out in the project.");
        }

        // Resolve an explicit build target rather than handing `dotnet build` a bare
        // directory — see CsProjHelper.ResolveBuildTarget and MANUAL_TEST_PASS.md,
        // finding 2. This alone fixes the common case (one real solution plus an
        // unrelated project file, e.g. a docker-compose.dcproj); a repo with
        // genuinely multiple competing solutions still needs `solution:` set in
        // .eolup.yml, and gets a clear error below if it isn't.
        var buildTarget = CsProjHelper.ResolveBuildTarget(projectPath, config.Solution);

        // Verify the project builds *before* touching anything — a pre-existing
        // build failure is a fundamentally different situation from one caused by
        // the migration itself, and should never be silently attempted against.
        ProcessResult preflightBuild;
        try
        {
            preflightBuild = await ProcessRunner.RunAsync("dotnet", ["build", buildTarget], projectPath, timeouts.Build, cancellationToken);
        }
        catch (ProcessTimeoutException timeout)
        {
            // Nothing has been touched yet, so this is an error with a way forward, not a verdict.
            throw new EolupUserException(
                $"The build before the migration ('{timeout.Command}') did not finish within " +
                $"{ProcessTimeoutException.Describe(timeout.Timeout)} and was stopped. Nothing was changed. " +
                "Give it more time with `buildTimeoutMinutes` in .eolup.yml, `--build-timeout`, or the Action's `build-timeout` input.",
                timeout);
        }

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
                throw new EolupUserException(
                    $"'{projectPath}' contains more than one project or solution file, so Eolup can't tell which " +
                    "one to build. Set `solution: <file>.sln` in .eolup.yml to disambiguate.");
            }

            if (combinedOutput.Contains("MSB1003"))
                throw new EolupUserException(CsProjHelper.DescribeNoBuildTargetAtRoot(projectPath));

            throw new EolupUserException(
                $"Project at '{projectPath}' does not build in its current state — cannot safely attempt migration. " +
                "Fix the existing build before running `eolup remediate`.");
        }

        // Baseline markers: warnings that already exist before we touch anything.
        // Real repos almost always carry some pre-existing obsolete-API or
        // package-vulnerability warnings unrelated to any particular migration —
        // found by running Eolup against eShopOnWeb, which had 13 such
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

        var branchName = await CreateRemediationBranchAsync(projectPath, $"eolup/upgrade-to-{targetVersion}", cancellationToken);

        var changedFiles = new HashSet<string>();
        foreach (var bump in projectsToBump)
            changedFiles.Add(CsProjHelper.RewriteTargetFramework(bump, currentVersion, targetTfm));

        await CommitMigrationAsync(projectPath, changedFiles, targetTfm, cancellationToken);

        var assessment = await AssessAsync(
            buildTarget, projectPath, testProjectExists, baselineMarkers, baseCommit, branchName, compareWithBaseline: true,
            timeouts, cancellationToken);

        // Only looked for when something already failed: it explains a failure, it never creates one.
        // And only in the projects this run moved — a project left on an older line on purpose
        // (or already on a newer one) isn't what broke.
        IReadOnlyList<string> frameworkAlignedPackages = [];
        IReadOnlyList<string> packagesBumped = [];
        IReadOnlyList<string> bumpDidNotHelp = [];
        if (assessment.Failed)
        {
            var stale = FrameworkAlignedPackages.FindStale(projectPath, projectsToBump.Select(b => b.File), currentVersion.Major);

            // Opt-in, and only when the migration is the likely culprit: a suite that was already failing
            // on the untouched code can't be fixed by moving packages, so don't spend a retry on it.
            IReadOnlyList<(StalePackage Package, string NewVersion)> edited = [];
            var migrationBrokeIt = !assessment.Build.Succeeded || TestComparison.BlamesMigration(assessment.TestComparison);
            if ((_options.BumpPackages || config.BumpPackages) && migrationBrokeIt)
            {
                // Shared props files reach every project, so they are only edited when every project is being moved.
                var bumpable = FrameworkAlignedPackages.LimitToBumpedProjects(
                    stale, CsProjHelper.FindProjectFiles(projectPath), projectsToBump.Select(b => b.File));

                // Where the migration stands before any package is touched; a failed bump goes back to here.
                var tfmCommit = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], projectPath, cancellationToken))
                    .StandardOutput.Trim();

                edited = await BumpPackagesAsync(bumpable, VersionPlanning.TryParse(targetTfm)!.Major, projectPath, cancellationToken);

                if (edited.Count > 0)
                {
                    // Bumping is judged by exactly the same build and tests as the framework bump. Kept only if
                    // it makes the migration pass; otherwise our own commit is dropped, so the branch holds just
                    // the framework change and nothing speculative. The baseline comparison isn't repeated: the
                    // untouched code's result can't have changed, and a dropped retry's comparison is discarded.
                    var retry = await AssessAsync(
                        buildTarget, projectPath, testProjectExists, baselineMarkers, baseCommit, branchName, compareWithBaseline: false,
                        timeouts, cancellationToken);
                    if (!retry.Failed)
                    {
                        assessment = retry;
                        packagesBumped = edited.Select(DescribeBump).ToList();
                    }
                    else
                    {
                        // --keep, not --hard: anything the user had uncommitted in the working tree when they ran
                        // Eolup came along onto this branch, and must survive; it only drops our own commit.
                        var drop = await ProcessRunner.RunAsync("git", ["reset", "--keep", tfmCommit], projectPath, cancellationToken);
                        if (!drop.Succeeded)
                            throw new EolupUserException(
                                $"Bumping the framework-aligned packages didn't fix the failure, and Eolup couldn't drop its own commit " +
                                $"again: {drop.StandardError.Trim()} The branch '{branchName}' still holds it; reset it to {tfmCommit} by hand.");
                        bumpDidNotHelp = edited.Select(DescribeBump).ToList();
                    }
                }
            }

            var editedPackages = edited.Select(e => e.Package).ToHashSet();
            frameworkAlignedPackages = stale.Where(p => !editedPackages.Contains(p)).Select(p => p.Describe()).ToList();
        }

        return new RemediationOutcome(
            assessment.Build.Succeeded, testProjectExists, assessment.TestsPassed, assessment.ManualActionMarkers, branchName,
            assessment.Coverage, assessment.TestComparison, frameworkAlignedPackages, packagesBumped, bumpDidNotHelp,
            assessment.Unverifiable);
    }

    private static string DescribeBump((StalePackage Package, string NewVersion) bump) =>
        $"{bump.Package.Id} {bump.Package.Version} → {bump.NewVersion} ({bump.Package.RelativePath})";

    /// <summary>
    /// Moves the stale framework-aligned packages to the newest stable release on the target
    /// framework's major and commits that as its own commit, returning what was changed (empty when
    /// nothing could be: no stale package, none resolvable on nuget.org, or no entry that could be edited).
    /// All of them move together — bumping just one often trips NuGet's downgrade check (NU1605)
    /// because the newer package pulls in newer siblings than the ones still pinned.
    /// </summary>
    private async Task<IReadOnlyList<(StalePackage Package, string NewVersion)>> BumpPackagesAsync(
        IReadOnlyList<StalePackage> stale, int targetMajor, string projectPath, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
            return [];

        var source = _versionSource ??= new NuGetOrgVersionSource();
        var resolved = await Task.WhenAll(stale.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
            (Id: id, Version: await PackageVersionResolver.LatestStableOnMajorAsync(source, id, targetMajor, cancellationToken))));
        var versions = resolved
            .Where(r => r.Version is not null)
            .ToDictionary(r => r.Id, r => r.Version!, StringComparer.OrdinalIgnoreCase);
        if (versions.Count == 0)
            return [];

        var result = PackageVersionEditor.Apply(stale, versions);
        if (result.ChangedFiles.Count == 0)
            return [];

        try
        {
            await CommitFilesAsync(projectPath, result.ChangedFiles, $"Eolup: align framework packages to {targetMajor}.x", cancellationToken);
        }
        catch (EolupUserException)
        {
            // An optional retry must never fail the run: a commit hook, signing setup or git itself refused it.
            // Put the files back as the framework-bump commit left them and carry on with the verdict already earned.
            await ProcessRunner.RunAsync("git", ["checkout", "HEAD", "--", .. result.ChangedFiles], projectPath, cancellationToken);
            return [];
        }

        var notEdited = result.NotEdited.ToHashSet();
        return stale.Where(p => !notEdited.Contains(p)).Select(p => (p, versions[p.Id])).ToList();
    }

    /// <summary>What one build-and-test pass of the migrated code showed.</summary>
    /// <param name="Unverifiable">
    /// Set when a step was stopped for taking too long: no answer exists, so nothing here can vouch for the change.
    /// </param>
    private sealed record Assessment(
        ProcessResult Build, IReadOnlyList<string> ManualActionMarkers, bool? TestsPassed, CoverageReport? Coverage, TestComparison? TestComparison,
        string? Unverifiable = null)
    {
        // A step that was stopped is neither a failure to explain nor one that moving packages could fix: there is no answer at all.
        public bool Failed => Unverifiable is null && (!Build.Succeeded || TestsPassed == false);
    }

    /// <summary>
    /// Why a build or test step that was stopped for taking too long can't vouch for the change, and how to give
    /// it more time. A slow but healthy run is indistinguishable from a hung one, so it is reported as exactly that
    /// ("did not finish"), never as a pass or a failure.
    /// </summary>
    private static string Stopped(string step, ProcessTimeoutException timeout, string yamlKey, string flag, string actionInput) =>
        $"The {step} did not finish within {ProcessTimeoutException.Describe(timeout.Timeout)} ('{timeout.Command}' was stopped), " +
        $"so the change can't be verified. Give it more time with `{yamlKey}` in .eolup.yml, `{flag}`, or the Action's `{actionInput}` input.";

    private static string BuildStopped(ProcessTimeoutException timeout) =>
        Stopped("build", timeout, "buildTimeoutMinutes", "--build-timeout", "build-timeout");

    private static string TestsStopped(ProcessTimeoutException timeout) =>
        Stopped("test run", timeout, "testTimeoutMinutes", "--test-timeout", "test-timeout");

    private static async Task<Assessment> AssessAsync(
        string buildTarget, string projectPath, bool testProjectExists, HashSet<string> baselineMarkers,
        string baseCommit, string branchName, bool compareWithBaseline, Timeouts timeouts, CancellationToken cancellationToken)
    {
        ProcessResult buildResult;
        try
        {
            buildResult = await ProcessRunner.RunAsync("dotnet", ["build", buildTarget], projectPath, timeouts.Build, cancellationToken);
        }
        catch (ProcessTimeoutException timeout)
        {
            return new Assessment(new ProcessResult(-1, timeout.PartialOutput, ""), [], null, null, null, BuildStopped(timeout));
        }

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
            TestRun run;
            try
            {
                run = await RunTestsAsync(buildTarget, projectPath, collectCoverage: true, timeouts.Test, cancellationToken);
            }
            catch (ProcessTimeoutException timeout)
            {
                return new Assessment(buildResult, manualActionMarkers, null, null, null, TestsStopped(timeout));
            }

            try
            {
                testsPassed = Outcome(run);

                // Coverage instrumentation can itself break tests that pass normally:
                // Coverlet injects a helper type into each instrumented assembly, and
                // tests that reflect over their own assemblies (architecture tests
                // are the classic case) then fail with a TypeLoadException. Found by
                // migrating Equinox: its tests pass on the migrated branch when run
                // plainly, but failed under --collect, producing a false NeedsReview.
                // So a failure here is never trusted on its own: confirm with a plain
                // run, which alone decides pass/fail. Coverage is then just "not
                // measured" — a measurement problem must not become a verdict.
                if (testsPassed == false)
                {
                    using var plain = await RunTestsAsync(buildTarget, projectPath, collectCoverage: false, timeouts.Test, cancellationToken);
                    testsPassed = Outcome(plain);

                    // Still failing: find out whether it was already failing before
                    // the migration touched anything.
                    if (testsPassed == false && compareWithBaseline)
                        testComparison = await CompareWithBaselineAsync(
                            plain, baseCommit, branchName, buildTarget, projectPath, timeouts, cancellationToken);
                }
                else if (testsPassed == true)
                {
                    var testProjectDirectories = CsProjHelper.FindTestProjectFiles(projectPath)
                        .Select(p => Path.GetDirectoryName(p)!)
                        .ToList();
                    coverage = CoberturaParser.Merge(CoverageFiles(run), projectPath, testProjectDirectories);
                }
            }
            catch (ProcessTimeoutException timeout)
            {
                // The confirming run without coverage ran out of time: still no answer.
                return new Assessment(buildResult, manualActionMarkers, null, null, null, TestsStopped(timeout));
            }
            finally
            {
                run.Dispose();
            }
        }

        return new Assessment(buildResult, manualActionMarkers, testsPassed, coverage, testComparison);
    }

    /// <summary>
    /// Creates and switches to the branch the migration is committed on, and returns its name.
    /// The result of `git checkout -b` used to be ignored: when the branch already existed
    /// (an earlier run, or one fetched from the remote) the command failed silently and the
    /// migration was committed onto whatever branch the user was on — breaking the promise that
    /// remediation never touches the current branch.
    ///
    /// A leftover *local* branch is skipped in favour of "-2", "-3", ... (compared ignoring case,
    /// because on Windows and macOS `Foo` and `foo` are the same ref). A name that already exists
    /// on a *remote* is different: it almost always means an earlier run's pull request is still
    /// open, and quietly using "-2" would push a second branch and open a duplicate PR on every
    /// scheduled run. That stops the run with an explanation instead, before anything changes.
    /// A checkout that still fails is an error too.
    /// </summary>
    private static async Task<string> CreateRemediationBranchAsync(
        string projectPath, string baseName, CancellationToken cancellationToken)
    {
        var refs = await ProcessRunner.RunAsync(
            "git", ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], projectPath, cancellationToken);
        var allRefs = refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Matching on the trailing "/<name>" keeps this right for remote names that contain a slash.
        var onRemote = allRefs.FirstOrDefault(r =>
            r.StartsWith("refs/remotes/", StringComparison.Ordinal) &&
            r.EndsWith("/" + baseName, StringComparison.OrdinalIgnoreCase));
        if (onRemote is not null)
            throw new EolupUserException(
                $"The branch '{baseName}' already exists on a remote ('{onRemote["refs/remotes/".Length..]}') — most likely an earlier " +
                "Eolup run's pull request is still open. Merge or close it and delete that branch, then run again. Nothing was changed.");

        var local = allRefs
            .Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(r => r["refs/heads/".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = baseName;
        for (var suffix = 2; local.Contains(name); suffix++)
            name = $"{baseName}-{suffix}";

        var checkout = await ProcessRunner.RunAsync("git", ["checkout", "-b", name], projectPath, cancellationToken);
        if (!checkout.Succeeded)
            throw new EolupUserException(
                $"Could not create the branch '{name}' for the migration: {checkout.StandardError.Trim()} Nothing was changed.");

        return name;
    }

    /// <summary>
    /// How one `dotnet test` run turned out: true when it ran tests and they passed, false when it failed,
    /// and null when it succeeded but ran no test at all — a test project switched off in the solution's
    /// build, or one that contains no tests — which says nothing about the migration. "Ran a test" means
    /// the TRX files hold a per-test result: the TRX logger writes one for every test that ran, passes included.
    /// </summary>
    private static bool? Outcome(TestRun run) => !run.Succeeded ? false : run.AnyResults ? true : (bool?)null;

    /// <summary>
    /// The coverage reports a run left behind. The results directory only exists once something wrote into
    /// it: a run that executed nothing never creates it, and enumerating a missing directory throws.
    /// </summary>
    private static string[] CoverageFiles(TestRun run) =>
        Directory.Exists(run.ResultsDirectory)
            ? Directory.GetFiles(run.ResultsDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
            : [];

    /// <summary>One `dotnet test` run: pass/fail, per-test failures, and where its result files are.</summary>
    private sealed class TestRun(bool succeeded, bool anyResults, IReadOnlySet<string> failed, string resultsDirectory) : IDisposable
    {
        public bool Succeeded { get; } = succeeded;
        public bool AnyResults { get; } = anyResults;
        public IReadOnlySet<string> Failed { get; } = failed;
        public string ResultsDirectory { get; } = resultsDirectory;

        public void Dispose() => DeleteQuietly(ResultsDirectory);

        /// <summary>Best effort: a leftover temp folder must never fail a run.</summary>
        public static void DeleteQuietly(string directory)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs the tests, writing TRX result files (and, if asked, coverage) to a fresh
    /// temp directory — never into the repo. The caller disposes the result to
    /// delete that directory.
    /// </summary>
    private static async Task<TestRun> RunTestsAsync(
        string buildTarget, string projectPath, bool collectCoverage, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "eolup-tests-" + Guid.NewGuid().ToString("N"));
        List<string> args = ["test", buildTarget, "--logger", "trx", "--results-directory", resultsDirectory];
        if (collectCoverage)
            args.Add("--collect:XPlat Code Coverage");

        try
        {
            var result = await ProcessRunner.RunAsync("dotnet", args, projectPath, timeout, cancellationToken);

            var trxFiles = Directory.Exists(resultsDirectory)
                ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
                : [];
            var (anyResults, failed) = TrxParser.Read(trxFiles);
            return new TestRun(result.Succeeded, anyResults, failed, resultsDirectory);
        }
        catch
        {
            // A timed-out or cancelled run never returns a TestRun, so nothing else would delete
            // whatever the TRX logger had already written there.
            TestRun.DeleteQuietly(resultsDirectory);
            throw;
        }
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
        Timeouts timeouts, CancellationToken cancellationToken)
    {
        if (baseCommit.Length == 0)
            return null;

        var checkout = await ProcessRunner.RunAsync("git", ["checkout", "--quiet", "--detach", baseCommit], projectPath, cancellationToken);
        if (!checkout.Succeeded)
            return null; // can't get back to the untouched code: say nothing rather than guess

        TestRun baseline;
        try
        {
            baseline = await RunTestsAsync(buildTarget, projectPath, collectCoverage: false, timeouts.Test, cancellationToken);
        }
        catch (ProcessTimeoutException)
        {
            // The untouched code's suite didn't finish in time either, so there is nothing to compare against
            // (the finally below still puts the branch back).
            return null;
        }
        finally
        {
            var back = await ProcessRunner.RunAsync("git", ["checkout", "--quiet", branchName], projectPath, CancellationToken.None);
            if (!back.Succeeded)
                throw new EolupUserException(
                    $"Eolup checked out the untouched commit to compare test results but couldn't switch back to " +
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
    private static Task CommitMigrationAsync(
        string projectPath, IReadOnlyCollection<string> changedFiles, string targetTfm, CancellationToken cancellationToken) =>
        CommitFilesAsync(projectPath, changedFiles, $"Eolup: upgrade target framework to {targetTfm}", cancellationToken);

    /// <summary>Stages exactly <paramref name="changedFiles"/> and commits them with <paramref name="message"/>; see <see cref="CommitMigrationAsync"/> for why.</summary>
    private static async Task CommitFilesAsync(
        string projectPath, IReadOnlyCollection<string> changedFiles, string message, CancellationToken cancellationToken)
    {
        if (changedFiles.Count == 0) return;

        var add = await ProcessRunner.RunAsync("git", ["add", "--", .. changedFiles], projectPath, cancellationToken);
        if (!add.Succeeded)
            throw new EolupUserException($"Could not stage the migration with git: {add.StandardError.Trim()}");

        // Nothing staged means every file already declared the target (a re-run).
        var staged = await ProcessRunner.RunAsync("git", ["diff", "--cached", "--quiet"], projectPath, cancellationToken);
        if (staged.Succeeded) return;

        // Use the user's own git identity when they have one. On a bare CI runner
        // there isn't one, and `git commit` refuses without it — so fall back to a
        // clearly-labelled identity via one-off -c flags (never touching git config).
        var hasIdentity =
            (await ProcessRunner.RunAsync("git", ["config", "user.email"], projectPath, cancellationToken)).Succeeded &&
            (await ProcessRunner.RunAsync("git", ["config", "user.name"], projectPath, cancellationToken)).Succeeded;
        List<string> identity = hasIdentity ? [] : ["-c", "user.name=Eolup", "-c", "user.email=eolup@users.noreply.github.com"];

        var commit = await ProcessRunner.RunAsync(
            "git", [.. identity, "commit", "-m", message],
            projectPath, cancellationToken);
        if (!commit.Succeeded)
            throw new EolupUserException(
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
