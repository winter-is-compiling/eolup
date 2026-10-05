using Rollforward.Core.Confidence;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

/// <summary>
/// The opt-in package bump is judged by the same build and tests as everything else: a kept bump never
/// changes the verdict, but the reasons (and so the PR) always say what moved besides the framework, and a
/// dropped bump says it was tried.
/// </summary>
public class PackageBumpScoringTests
{
    private static readonly IReadOnlyList<string> Bumped =
        ["Microsoft.AspNetCore.Mvc.Testing 8.0.11 → 10.0.12 (tests/Api.Tests/Api.Tests.csproj)"];

    private static RemediationResult Score(
        bool build = true, bool? testsPassed = true, TestComparison? comparison = null, CoverageReport? coverage = null,
        IReadOnlyList<string>? stale = null, IReadOnlyList<string>? bumped = null, IReadOnlyList<string>? unhelpful = null,
        IReadOnlyList<string>? markers = null) =>
        ConfidenceScorer.Score(
            buildSucceeded: build, testProjectExists: true, testsPassed: testsPassed,
            manualActionMarkers: markers ?? [], branchName: "b", coverage: coverage, testComparison: comparison,
            frameworkAlignedPackages: stale, packagesBumped: bumped, unhelpfulPackageBumps: unhelpful);

    [Fact]
    public void AKeptBump_StaysHighConfidence_ButIsNamedInTheReasons()
    {
        var result = Score(bumped: Bumped);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Contains(result.Reasons, r => r.StartsWith("Build succeeded, existing tests passed"));
        Assert.Contains(result.Reasons, r =>
            r.Contains("were also moved to the new framework's major") && r.Contains("Microsoft.AspNetCore.Mvc.Testing 8.0.11 → 10.0.12"));
    }

    [Fact]
    public void AKeptBump_DoesNotHideOtherReasonsForReview()
    {
        var result = Score(bumped: Bumped, markers: ["warning CS0618: 'Foo' is obsolete"]);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("manual-action marker"));
        Assert.Contains(result.Reasons, r => r.Contains("were also moved to the new framework's major"));
    }

    [Fact]
    public void NoBump_LeavesTheReasonsAsTheyWere()
    {
        var result = Score(bumped: []);

        var reason = Assert.Single(result.Reasons);
        Assert.StartsWith("Build succeeded, existing tests passed", reason);
    }

    [Fact]
    public void ADroppedBump_IsSaidToHaveBeenTried_WhenTestsStillFail()
    {
        var result = Score(
            testsPassed: false, comparison: new TestComparison(true, ["Api.Tests.Due"], [], true), unhelpful: Bumped);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r =>
            r.Contains("did not fix the failure") && r.Contains("branch holds only the framework bump") &&
            r.Contains("Microsoft.AspNetCore.Mvc.Testing 8.0.11 → 10.0.12"));
    }

    [Fact]
    public void ADroppedBump_IsSaidToHaveBeenTried_WhenTheBuildStillFails()
    {
        var result = Score(build: false, testsPassed: null, unhelpful: Bumped);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("did not fix the failure"));
    }

    [Fact]
    public void PackagesThatCouldNotBeBumpedAreStillListedAsTheHint_NextToTheDroppedOnes()
    {
        var notResolved = new[] { "Microsoft.Extensions.Options 8.0.0 (src/App/App.csproj)" };

        var result = Score(testsPassed: false, unhelpful: Bumped, stale: notResolved);

        Assert.Contains(result.Reasons, r => r.Contains("did not fix the failure"));
        Assert.Contains(result.Reasons, r => r.Contains("still on the old major") && r.Contains("Microsoft.Extensions.Options 8.0.0"));
    }

    [Fact]
    public void FailuresThatPredateTheMigration_NeverGetABumpNote()
    {
        var alreadyFailing = new TestComparison(false, [], ["Api.Tests.Flaky"], true);

        var result = Score(testsPassed: false, comparison: alreadyFailing, unhelpful: Bumped);

        Assert.DoesNotContain(result.Reasons, r => r.Contains("did not fix the failure"));
    }
}
