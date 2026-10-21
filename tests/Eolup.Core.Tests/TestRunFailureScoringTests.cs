using Eolup.Core.Confidence;
using Eolup.Core.Models;
using Xunit;

namespace Eolup.Core.Tests;

/// <summary>
/// A test project whose host crashes reports nothing, so the suite fails with no failing test to name.
/// The reason must say which project could not run, never "0 failing test(s)".
/// </summary>
public class TestRunFailureScoringTests
{
    private const string Crash = "The active test run was aborted. Reason: Test host process crashed";

    private static RemediationResult Score(TestComparison comparison) =>
        ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: false,
            manualActionMarkers: [], branchName: "b", testComparison: comparison);

    private static void AssertNoEmptyListWording(string reason)
    {
        Assert.DoesNotContain("0 failing", reason);
        Assert.DoesNotContain("()", reason);
        Assert.DoesNotContain("Every other test passed", reason);
    }

    [Fact]
    public void CrashingEitherWay_NamesTheProject_AndIsNeedsReview()
    {
        var result = Score(new TestComparison(false, [], [], true, NotRun: ["CrashTests"], NotRunBefore: ["CrashTests"], CrashText: Crash));

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var reason = Assert.Single(result.Reasons);
        Assert.Contains("CrashTests", reason);
        Assert.Contains("on the untouched code either", reason);
        Assert.Contains("Test host process crashed", reason);
        Assert.Contains("The tests that did run passed", reason);
        AssertNoEmptyListWording(reason);
    }

    [Fact]
    public void CrashingOnlyAfterTheMigration_IsALikelyRegression()
    {
        var result = Score(new TestComparison(false, [], [], true, NotRun: ["CrashTests"], NotRunBefore: [], CrashText: Crash));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains("though it did before", reason);
        Assert.Contains("likely regression", reason);
    }

    [Fact]
    public void BaselinePassed_ButAProjectNowCrashes_NamesItInTheRegression()
    {
        var result = Score(new TestComparison(true, [], [], true, NotRun: ["CrashTests"], CrashText: Crash));

        var reason = Assert.Single(result.Reasons);
        Assert.Contains("passed before the migration and fails after it", reason);
        Assert.Contains("CrashTests", reason);
        Assert.DoesNotContain("The tests that did run passed", reason);
    }

    [Fact]
    public void ProjectsThatCouldNotRunAreListedBesideRealFailures()
    {
        var result = Score(new TestComparison(false, ["New.Break"], [], true, NotRun: ["CrashTests"], NotRunBefore: ["CrashTests"]));

        Assert.Equal(2, result.Reasons.Count);
        Assert.Contains(result.Reasons, r => r.Contains("New.Break"));
        Assert.Contains(result.Reasons, r => r.Contains("CrashTests"));
        Assert.DoesNotContain(result.Reasons, r => r.Contains("0 other failing"));
    }

    [Fact]
    public void FailingWithNothingNamed_StillAvoidsTheEmptyListWording()
    {
        // The console output named no project (an older or localized SDK): say what is known, not "0 failing test(s)".
        var result = Score(new TestComparison(false, [], [], true, NotRun: [], NotRunBefore: []));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains("without reporting any failing test", reason);
        AssertNoEmptyListWording(reason);
    }

    [Fact]
    public void NothingRanAtAll_StaysBlocked()
    {
        var result = Score(new TestComparison(false, [], [], FailuresIdentified: false, NotRun: ["CrashTests"]));

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
    }
}
