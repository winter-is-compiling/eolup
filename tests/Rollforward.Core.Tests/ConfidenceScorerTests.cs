using Rollforward.Core.Confidence;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

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
            manualActionMarkers: [], branchName: "rollforward/upgrade-to-10");

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Equal("rollforward/upgrade-to-10", result.BranchName);
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
