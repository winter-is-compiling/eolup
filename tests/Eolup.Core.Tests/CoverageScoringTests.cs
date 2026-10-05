using Eolup.Core.Confidence;
using Eolup.Core.Models;
using Xunit;

namespace Eolup.Core.Tests;

public class CoverageScoringTests
{
    private static CoverageReport Report(params (string File, int Covered, int Coverable)[] files) =>
        new(files.Select(f => new FileCoverage(f.File, f.Covered, f.Coverable)).ToList());

    private static RemediationResult Score(CoverageReport? coverage, int min = 50, string[]? markers = null) =>
        ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: true,
            manualActionMarkers: markers ?? [], branchName: "b", coverage, min);

    [Fact]
    public void CoverageNotMeasured_KeepsHighConfidence_ButSaysSo()
    {
        var result = Score(coverage: null);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("line coverage was not measured"));
    }

    [Fact]
    public void CoverageAtOrAboveTheMinimum_IsHighConfidence_AndReportsThePercentage()
    {
        var result = Score(Report(("A.cs", 5, 10)), min: 50);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("line coverage 50%"));
    }

    [Fact]
    public void CoverageBelowTheMinimum_IsNeedsReview_NamingTheLeastCoveredFiles()
    {
        var result = Score(Report(("Good.cs", 9, 10), ("Weak.cs", 1, 10), ("Worse.cs", 0, 5), ("Ok.cs", 6, 10)), min: 80);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var reason = Assert.Single(result.Reasons, r => r.Contains("below the 80% minimum"));
        Assert.Contains("Worse.cs (0%), Weak.cs (10%)", reason);
        Assert.DoesNotContain("Good.cs", reason);
    }

    [Fact]
    public void ZeroCoveredLines_IsBlocked_EvenThoughTestsPassed()
    {
        // Tests exist and pass but execute none of the migrated code: same
        // "cannot verify" situation as having no tests at all.
        var result = Score(Report(("A.cs", 0, 10), ("B.cs", 0, 4)));

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("none of the 14 coverable lines"));
    }

    [Fact]
    public void MinimumOfZero_DisablesTheLowCoverageRule_ButNotTheZeroCoverageBlock()
    {
        Assert.Equal(ConfidenceVerdict.HighConfidence, Score(Report(("A.cs", 1, 100)), min: 0).Verdict);
        Assert.Equal(ConfidenceVerdict.Blocked, Score(Report(("A.cs", 0, 100)), min: 0).Verdict);
    }

    [Fact]
    public void LowCoverageAndMarkers_ReportBothReasons_InOneNeedsReview()
    {
        var result = Score(Report(("A.cs", 1, 10)), markers: ["warning CS0618: obsolete"]);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("manual-action marker"));
        Assert.Contains(result.Reasons, r => r.Contains("Line coverage"));
    }

    [Fact]
    public void FailedTests_TakePrecedence_OverCoverage()
    {
        var result = ConfidenceScorer.Score(true, true, false, [], "b", Report(("A.cs", 0, 10)));

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("test suite failed"));
    }

    [Fact]
    public void TheReportTravelsOnTheResult()
    {
        var report = Report(("A.cs", 5, 10));

        Assert.Same(report, Score(report).Coverage);
    }
}
