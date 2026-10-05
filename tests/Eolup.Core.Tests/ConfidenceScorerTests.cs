using Eolup.Core.Confidence;
using Eolup.Core.Models;
using Xunit;

namespace Eolup.Core.Tests;

public class ConfidenceScorerTests
{
    [Fact]
    public void BuildFailed_IsBlocked()
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: false, testProjectExists: true, testsPassed: null,
            manualActionMarkers: [], branchName: "b");

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("Build failed"));
    }

    [Fact]
    public void NoTestProject_IsBlocked_EvenIfBuildSucceeded()
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: false, testsPassed: null,
            manualActionMarkers: [], branchName: "b");

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("No test project"));
    }

    [Fact]
    public void TestsFailed_IsNeedsReview_NotBlocked()
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: false,
            manualActionMarkers: [], branchName: "b");

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
    }

    [Fact]
    public void TestCommandThatRanNoTests_IsBlocked_NotHighConfidence()
    {
        // `dotnet test` can succeed without running anything (a test project switched off in the solution
        // build, or one with no tests in it). testsPassed is null then: nothing passed, so nothing vouches
        // for the change, and the verdict must not claim that "existing tests passed".
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: null,
            manualActionMarkers: [], branchName: "b");

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("ran no tests"));
        Assert.DoesNotContain(result.Reasons, r => r.Contains("tests passed"));
    }

    [Fact]
    public void TestCommandThatRanNoTests_IsReportedAsThat_NotAsZeroCoverage()
    {
        // A project with no tests still gets instrumented, so a coverage report with nothing covered can
        // exist. The honest reason is that no test ran, not that "tests passed" and covered no line.
        var coverage = new CoverageReport([new FileCoverage("A.cs", 0, 10)]);

        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: null,
            manualActionMarkers: [], branchName: "b", coverage);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("ran no tests"));
        Assert.DoesNotContain(result.Reasons, r => r.Contains("none of the"));
    }

    [Fact]
    public void ManualActionMarkersPresent_IsNeedsReview()
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: true,
            manualActionMarkers: ["warning CS0618: 'Foo.Bar()' is obsolete"], branchName: "b");

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("manual-action marker"));
    }

    [Fact]
    public void BuildSucceeded_TestsPassed_NoMarkers_IsHighConfidence()
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: true,
            manualActionMarkers: [], branchName: "eolup/upgrade-to-10");

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Equal("eolup/upgrade-to-10", result.BranchName);
    }

    [Fact]
    public void BuildFailed_TakesPrecedenceOver_MissingTestProject()
    {
        // A failed build should be reported as the reason, not masked by an
        // unrelated "no test project" finding.
        var result = ConfidenceScorer.Score(
            buildSucceeded: false, testProjectExists: false, testsPassed: null,
            manualActionMarkers: [], branchName: null);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("Build failed"));
    }
}
