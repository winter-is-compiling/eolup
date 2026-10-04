using Rollforward.Core;
using Rollforward.Core.Models;
using Rollforward.Providers.DotNet;
using Xunit;

namespace Rollforward.Fixtures.Tests;

/// <summary>
/// The automated system-test suite described in TESTING.md: each fixture has an
/// expected verdict, and this suite fails immediately if a change to the
/// confidence-scoring or remediation logic silently shifts that verdict.
///
/// The engine runs against a recorded snapshot of endoflife.date (see
/// RecordedEolClient), so these need no network for version data; the real
/// dotnet/git/MSBuild still run. The live API is covered by one smoke test.
/// </summary>
public class FixtureVerdictTests
{
    private static RollforwardEngine CreateEngine(bool bumpPackages = false) =>
        new(new RecordedEolClient(), new DotNetLanguageProvider(bumpPackages));

    [Fact]
    public async Task Trivial_YieldsHighConfidence()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.True(result.BuildSucceeded);
        Assert.True(result.TestsPassed);
        Assert.Empty(result.ManualActionMarkers);
    }

    [Fact]
    public async Task NeedsReview_DueToObsoleteApi_YieldsNeedsReview()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-needs-review");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.True(result.TestsPassed);
        Assert.Contains(result.ManualActionMarkers, m => m.Contains("CS0618"));
        // The uncovered file that carries the obsolete call is called out too.
        Assert.Contains(result.Reasons, r => r.Contains("NewApiUsage.cs (0%)"));
    }

    [Fact]
    public async Task PreExistingPackageWarning_DoesNotCauseNeedsReview()
    {
        // Found via the manual test pass against eShopOnWeb: a real repo's
        // baseline build already carried 13 warnings unrelated to any migration.
        // This fixture proves Rollforward's baseline-diffing correctly ignores a
        // warning (NU1701, from a package that's equally incompatible before and
        // after) that already existed rather than misattributing it to this
        // migration. See DotNetLanguageProvider.RemediateAsync.
        using var fixture = FixtureHarness.CopyToTemp("fixture-preexisting-warning");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Empty(result.ManualActionMarkers);
    }

    [Fact]
    public async Task MultipleRootProjectFiles_StillResolvesAndYieldsHighConfidence()
    {
        // Found via the manual test pass against eShopOnWeb: a repo with one real
        // solution plus one unrelated project file at its root (there, a
        // docker-compose.dcproj) makes plain `dotnet build <directory>` fail with
        // MSB1011, even though there's really only one solution that matters. This
        // proves CsProjHelper.ResolveBuildTarget's automatic single-.sln detection
        // avoids that entirely, without needing a `solution:` override in
        // .rollforward.yml. See MANUAL_TEST_PASS.md, Pass #1 finding 2.
        using var fixture = FixtureHarness.CopyToTemp("fixture-multi-root-projects");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
    }

    [Fact]
    public async Task ProjectsOnDifferentVersions_OnlyTheOldestIsBumped_OneHopPerRun()
    {
        // History: found via dotnet/eShop and then a constructed repro — pointing
        // Rollforward at a directory of independently-versioned services used to
        // silently report just one of them (the worst failure mode of any pass; see
        // MANUAL_TEST_PASS.md, Pass #2), and afterwards refused outright. Real repos
        // routinely mix versions (Pass #5: 3 of 9 refused), so the rule is now: the
        // oldest version is "current", and only projects on it move this run. The
        // others are already ahead and wait; the next run moves everything together.
        using var fixture = FixtureHarness.CopyToTemp("fixture-version-mismatch");
        var path = fixture.Path;

        var scan = await CreateEngine().ScanAsync(path);
        Assert.Equal("net8.0", scan.CurrentVersion);
        Assert.Contains(scan.Notes, n => n.Contains("ServiceB=net10.0"));

        await CreateEngine().RemediateAsync(path);

        var changed = await ChangedFilesInMigrationCommit(path);
        Assert.Equal(["ServiceA/ServiceA.csproj"], changed);
    }

    [Fact]
    public async Task NetStandardLibrary_IsNeitherARefusalNorUpgraded()
    {
        // Found in Pass #5 (iayti/CleanArchitecture, PeakLimsApi): a netstandard
        // shared library beside modern projects made detection refuse the repo. It
        // is compatible with every modern .NET, so it is left exactly as it is.
        using var fixture = FixtureHarness.CopyToTemp("fixture-netstandard-library");
        var path = fixture.Path;

        var scan = await CreateEngine().ScanAsync(path);
        Assert.Equal("net8.0", scan.CurrentVersion);
        Assert.Contains(scan.Notes, n => n.Contains("SampleLib (netstandard2.1) is left alone"));

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        var changed = await ChangedFilesInMigrationCommit(path);
        Assert.Equal(["src/SampleApp.Tests/SampleApp.Tests.csproj", "src/SampleApp/SampleApp.csproj"], changed);
        Assert.Contains("netstandard2.1", await File.ReadAllTextAsync(Path.Combine(path, "src", "SampleLib", "SampleLib.csproj")));
    }

    private static async Task<List<string>> ChangedFilesInMigrationCommit(string repo) =>
        (await Git(repo, "diff", "--name-only", "HEAD~1", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).Order().ToList();

    [Fact]
    public async Task TestsPassButExerciseNoApplicationCode_YieldsBlocked()
    {
        // The v0 proxy ("a test project exists") called this High. Real per-file
        // coverage sees that the passing tests never touch SampleApp,
        // so they say nothing about whether the migration is safe.
        using var fixture = FixtureHarness.CopyToTemp("fixture-zero-coverage");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.True(result.TestsPassed);
        Assert.Equal(0, result.Coverage!.CoveredLines);
        Assert.Contains(result.Reasons, r => r.Contains("none of the"));
    }

    [Fact]
    public async Task TestProjectWithoutACoverageCollector_StaysHighConfidence_ButSaysCoverageWasNotMeasured()
    {
        // Plenty of real repos don't reference coverlet.collector. Coverage can't
        // be measured there, which must not regress them to Blocked/NeedsReview —
        // but the verdict must not pretend it was measured either.
        using var fixture = FixtureHarness.CopyToTemp("fixture-no-coverage-collector");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Null(result.Coverage);
        Assert.Contains(result.Reasons, r => r.Contains("line coverage was not measured"));
    }

    [Fact]
    public async Task Remediation_CommitsTheMigrationOntoItsBranch_TouchingOnlyProjectFiles()
    {
        // Found while checking the PR flow against a local bare remote: the
        // migration was left uncommitted, so the pushed branch had zero commits
        // ahead of the base and `gh pr create` had nothing to open a PR for. The
        // fixture is not gitignoring bin/obj, so this also proves build output
        // is never swept into the commit.
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        var ahead = await Git(path, "rev-list", "--count", "HEAD~1..HEAD");
        Assert.Equal("1", ahead);
        var files = (await Git(path, "diff", "--name-only", "HEAD~1", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).Order().ToList();
        Assert.Equal(["src/SampleApp.Tests/SampleApp.Tests.csproj", "src/SampleApp/SampleApp.csproj"], files);
        Assert.Contains("net10.0", await Git(path, "show", "HEAD:src/SampleApp/SampleApp.csproj"));
    }

    private static async Task<string> Git(string workingDirectory, params string[] args)
    {
        var result = await Rollforward.Core.Infrastructure.ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    [Fact]
    public async Task TestsThatOnlyFailUnderCoverageInstrumentation_AreNotReportedAsAMigrationFailure()
    {
        // Real bug, found by migrating Equinox: tests that pass normally but break
        // when Coverlet instruments the assembly made remediate report "test suite
        // failed" (NeedsReview) although the migration broke nothing. A failing
        // coverage run must be confirmed with a plain run before it can be a verdict.
        using var fixture = FixtureHarness.CopyToTemp("fixture-coverage-hostile-test");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.True(result.TestsPassed);
        Assert.Null(result.Coverage); // instrumentation didn't work here, so coverage is honestly "not measured"
        Assert.Contains(result.Reasons, r => r.Contains("line coverage was not measured"));
    }

    [Fact]
    public async Task TestsAlreadyFailingBeforeTheMigration_AreNotBlamedOnIt()
    {
        // Found across three real repos (Pass #5): a suite that was
        // already broken on the untouched code was reported as "the migration may
        // have broken the tests". The failing run is now compared with the same
        // suite on the untouched commit.
        using var fixture = FixtureHarness.CopyToTemp("fixture-preexisting-test-failure");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var comparison = Assert.IsType<TestComparison>(result.TestComparison);
        Assert.False(comparison.BaselinePassed);
        Assert.Empty(comparison.NewFailures);
        Assert.Contains(comparison.AlreadyFailing, t => t.EndsWith("Checkout_NeedsADatabase"));
        Assert.Contains(result.Reasons, r => r.Contains("No test broke because of the migration"));
        await AssertStillOnTheMigrationBranch(path);
    }

    [Fact]
    public async Task ATestTheMigrationBreaks_IsNamedAsALikelyRegression()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-migration-breaks-test");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var comparison = Assert.IsType<TestComparison>(result.TestComparison);
        Assert.True(comparison.BaselinePassed);
        Assert.Contains(comparison.NewFailures, t => t.EndsWith("Runtime_IsDotNet8"));
        Assert.Contains(result.Reasons, r => r.Contains("passed before the migration and fails after it") && r.Contains("Runtime_IsDotNet8"));
        await AssertStillOnTheMigrationBranch(path);
    }

    /// <summary>Checking the untouched code out must leave the repo exactly where the migration left it.</summary>
    private static async Task AssertStillOnTheMigrationBranch(string repo)
    {
        Assert.Equal("rollforward/upgrade-to-10.0", await Git(repo, "branch", "--show-current"));
        Assert.Contains("net10.0", await Git(repo, "show", "HEAD:src/SampleApp/SampleApp.csproj"));
    }

    [Fact]
    public async Task MultiTargetedLibrary_OnlyItsModernEntryMoves()
    {
        // <TargetFrameworks> used to be refused outright (Dapper, Polly,
        // CliWrap). The library's modern entry now moves one hop like any project,
        // and its netstandard entry is left exactly as written.
        using var fixture = FixtureHarness.CopyToTemp("fixture-multi-target-library");
        var path = fixture.Path;

        var scan = await CreateEngine().ScanAsync(path);
        Assert.Equal("net8.0", scan.CurrentVersion);
        Assert.Contains(scan.Notes, n => n.Contains("SampleLib targets several frameworks"));

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Contains("src/SampleLib/SampleLib.csproj", await ChangedFilesInMigrationCommit(path));
        Assert.Contains("<TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>",
            await File.ReadAllTextAsync(Path.Combine(path, "src", "SampleLib", "SampleLib.csproj")));
    }

    [Fact]
    public async Task Chain_TakesEachHighConfidenceHop_AndStopsAtTheFirstThatIsNot()
    {
        // Chaining, end to end on real dotnet/git: net6.0 -> net8.0 is clean, and
        // net8.0 -> net10.0 breaks a test. The chain must do the first hop, stop at
        // the second, and leave a PR-able branch holding only the first hop's commit.
        using var fixture = FixtureHarness.CopyToTemp("fixture-chain-stops");
        var path = fixture.Path;

        var run = await CreateEngine().RemediateChainAsync(path);

        Assert.Equal([("net6.0", "net8.0"), ("net8.0", "net10.0")], run.Hops.Select(h => (h.From, h.To)));
        Assert.Equal(ConfidenceVerdict.HighConfidence, run.Hops[0].Result.Verdict);
        Assert.Equal(ConfidenceVerdict.NeedsReview, run.Final.Result.Verdict);
        Assert.Contains(run.Final.Result.TestComparison!.NewFailures, t => t.EndsWith("Runtime_IsOlderThanDotNet10"));

        var publishable = run.LastHighConfidence!.Result.BranchName!;
        Assert.Equal("rollforward/upgrade-to-8.0", publishable);
        Assert.Contains("net8.0", await Git(path, "show", $"{publishable}:src/SampleApp/SampleApp.csproj"));
        // The stopped hop's branch builds on the published one by exactly its own commit.
        Assert.Equal("1", await Git(path, "rev-list", "--count", $"{publishable}..rollforward/upgrade-to-10.0"));
    }

    [Fact]
    public async Task ExistingRemediationBranch_IsNeverReused_AndTheCurrentBranchStaysUntouched()
    {
        // `git checkout -b` used to fail silently when rollforward/upgrade-to-10.0 already
        // existed (an earlier run, a fetched remote branch), and the migration was then
        // committed onto whatever branch the user was on.
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;
        var userBranch = await Git(path, "rev-parse", "--abbrev-ref", "HEAD");
        var before = await Git(path, "rev-parse", "HEAD");
        await Git(path, "branch", "rollforward/upgrade-to-10.0");

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal("rollforward/upgrade-to-10.0-2", result.BranchName);
        Assert.Equal(before, await Git(path, "rev-parse", userBranch));
        Assert.Equal(before, await Git(path, "rev-parse", "rollforward/upgrade-to-10.0"));
        Assert.Equal("1", await Git(path, "rev-list", "--count", $"{before}..{result.BranchName}"));
    }

    [Fact]
    public async Task RemediationBranch_KeepsCountingPastEveryLeftoverLocalBranch()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;
        await Git(path, "branch", "rollforward/upgrade-to-10.0");
        await Git(path, "branch", "rollforward/upgrade-to-10.0-2");

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal("rollforward/upgrade-to-10.0-3", result.BranchName);
    }

    [Fact]
    public async Task RemediationBranch_NameCollisionIgnoresCase()
    {
        // On Windows and macOS `Rollforward/...` and `rollforward/...` are the same ref, so a
        // differently-cased leftover must count as taken rather than make `checkout -b` fail.
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;
        await Git(path, "branch", "Rollforward/Upgrade-To-10.0");

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal("rollforward/upgrade-to-10.0-2", result.BranchName);
    }

    [Fact]
    public async Task BranchAlreadyOnARemote_StopsTheRun_InsteadOfOpeningADuplicate()
    {
        // An earlier run's PR is still open: its branch is on the remote. Suffixing "-2" would
        // push a second branch and open a second PR for the same upgrade on every run.
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;
        var userBranch = await Git(path, "rev-parse", "--abbrev-ref", "HEAD");
        var before = await Git(path, "rev-parse", "HEAD");
        await Git(path, "update-ref", "refs/remotes/origin/rollforward/upgrade-to-10.0", "HEAD");

        var error = await Assert.ThrowsAsync<RollforwardUserException>(() => CreateEngine().RemediateAsync(path));

        Assert.Contains("already exists on a remote", error.Message);
        Assert.Equal(before, await Git(path, "rev-parse", userBranch));
        Assert.Equal(userBranch, await Git(path, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("", await Git(path, "branch", "--list", "rollforward/*"));
    }

    [Fact]
    public async Task BranchThatCannotBeCreated_IsAnError_NotASilentCommitOnTheCurrentBranch()
    {
        // A branch literally named "rollforward" blocks "rollforward/upgrade-to-10.0" (a ref can't be
        // both a file and a directory), so `checkout -b` fails — and used to be ignored.
        using var fixture = FixtureHarness.CopyToTemp("fixture-trivial");
        var path = fixture.Path;
        var userBranch = await Git(path, "rev-parse", "--abbrev-ref", "HEAD");
        var before = await Git(path, "rev-parse", "HEAD");
        await Git(path, "branch", "rollforward");

        var error = await Assert.ThrowsAsync<RollforwardUserException>(() => CreateEngine().RemediateAsync(path));

        Assert.Contains("Could not create the branch", error.Message);
        Assert.Equal(before, await Git(path, "rev-parse", userBranch));
        Assert.Equal(userBranch, await Git(path, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public async Task StaleFrameworkPackage_IsNamedWhenTheUpgradeBreaksATest()
    {
        // End to end on real dotnet/git: SampleApp references Microsoft.Extensions.Options 8.0.0,
        // the bump to net10.0 leaves it there, and a test breaks. The verdict stays NeedsReview
        // for the right reason (the broken test) and also names the package as a likely cause.
        using var fixture = FixtureHarness.CopyToTemp("fixture-stale-framework-package");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.TestComparison!.NewFailures, t => t.EndsWith("Runtime_IsOlderThanDotNet10"));
        Assert.Contains(result.Reasons, r =>
            r.Contains("still on the old major") &&
            r.Contains("Microsoft.Extensions.Options 8.0.0 (src/SampleApp/SampleApp.csproj)"));
    }

    [Fact]
    public async Task BumpPackages_FixesAnUpgradeThatTheStalePackageBroke()
    {
        // The real failure, on real dotnet/NuGet/git: after the bump to net10.0 the in-memory test server
        // from Microsoft.AspNetCore.Mvc.Testing 8.x answers HTTP 500. With the opt-in on, Rollforward moves
        // that package to 10.x as its own commit, the retry passes, and the verdict says what else moved.
        using var fixture = FixtureHarness.CopyToTemp("fixture-stale-package-fixed-by-bump");
        var path = fixture.Path;
        var before = await Git(path, "rev-parse", "HEAD");

        var result = await CreateEngine(bumpPackages: true).RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.True(result.TestsPassed);
        Assert.Contains(result.Reasons, r =>
            r.Contains("were also moved to the new framework's major") &&
            r.Contains("Microsoft.AspNetCore.Mvc.Testing 8.0.11 → 10.") &&
            r.Contains("src/WebApp.Tests/WebApp.Tests.csproj"));
        Assert.Equal(
            ["Rollforward: align framework packages to 10.x", "Rollforward: upgrade target framework to net10.0"],
            (await Git(path, "log", "--format=%s", $"{before}..{result.BranchName}")).Split('\n', StringSplitOptions.TrimEntries));
        var project = await Git(path, "show", $"{result.BranchName}:src/WebApp.Tests/WebApp.Tests.csproj");
        Assert.Matches("Microsoft.AspNetCore.Mvc.Testing\" Version=\"10\\.\\d+\\.\\d+\"", project);
    }

    [Fact]
    public async Task WithoutTheOptIn_OnlyTheFrameworkMoves_AndTheStalePackageIsNamed()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-stale-package-fixed-by-bump");
        var path = fixture.Path;
        var before = await Git(path, "rev-parse", "HEAD");

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("still on the old major") && r.Contains("Microsoft.AspNetCore.Mvc.Testing 8.0.11"));
        Assert.DoesNotContain(result.Reasons, r => r.Contains("moved to the new framework's major"));
        Assert.Equal("1", await Git(path, "rev-list", "--count", $"{before}..{result.BranchName}"));
        Assert.Contains("Mvc.Testing\" Version=\"8.0.11\"", await Git(path, "show", $"{result.BranchName}:src/WebApp.Tests/WebApp.Tests.csproj"));
    }

    [Fact]
    public async Task BumpPackages_ThatDoesNotHelp_IsDropped_LeavingOnlyTheFrameworkBump()
    {
        // This fixture's failing test has nothing to do with the package, so moving it can't fix anything.
        // The speculative commit must go, and the verdict must say it was tried.
        using var fixture = FixtureHarness.CopyToTemp("fixture-stale-framework-package");
        var path = fixture.Path;
        var before = await Git(path, "rev-parse", "HEAD");

        var result = await CreateEngine(bumpPackages: true).RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("did not fix the failure") && r.Contains("Microsoft.Extensions.Options"));
        Assert.Equal(["Rollforward: upgrade target framework to net10.0"],
            (await Git(path, "log", "--format=%s", $"{before}..{result.BranchName}")).Split('\n', StringSplitOptions.TrimEntries));
        Assert.Contains("Options\" Version=\"8.0.0\"", await Git(path, "show", $"{result.BranchName}:src/SampleApp/SampleApp.csproj"));
        Assert.Equal("", await Git(path, "status", "--porcelain", "--untracked-files=no"));
    }

    [Fact]
    public async Task NoTestProject_YieldsBlocked()
    {
        using var fixture = FixtureHarness.CopyToTemp("fixture-no-tests");
        var path = fixture.Path;

        var result = await CreateEngine().RemediateAsync(path);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.False(result.TestProjectExists);
        Assert.Contains(result.Reasons, r => r.Contains("No test project"));
    }

    [Fact]
    public async Task SameFixture_ProducesSameVerdict_OnRepeatedRuns()
    {
        // Determinism check from TESTING.md — a flaky classifier destroys trust
        // faster than a wrong-but-consistent one.
        using var fixture1 = FixtureHarness.CopyToTemp("fixture-trivial");
        var path1 = fixture1.Path;
        using var fixture2 = FixtureHarness.CopyToTemp("fixture-trivial");
        var path2 = fixture2.Path;

        var result1 = await CreateEngine().RemediateAsync(path1);
        var result2 = await CreateEngine().RemediateAsync(path2);

        Assert.Equal(result1.Verdict, result2.Verdict);
    }
}
