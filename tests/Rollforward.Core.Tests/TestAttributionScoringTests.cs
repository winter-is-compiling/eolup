using Rollforward.Core.Confidence;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

/// <summary>
/// A failing suite after a migration is judged against the same suite on
/// the untouched code, so pre-existing failures aren't blamed on the migration.
/// </summary>
public class TestAttributionScoringTests
{
    private static RemediationResult ScoreFailedTests(TestComparison? comparison) =>
        ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: false,
            manualActionMarkers: [], branchName: "b", testComparison: comparison);

    [Fact]
    public void NoComparison_KeepsTheGenericReason()
    {
        var result = ScoreFailedTests(null);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("needs human judgment"));
    }

    [Fact]
    public void PassedBefore_FailsAfter_IsALikelyRegression_NamingTheTests()
    {
        var result = ScoreFailedTests(new TestComparison(true, ["Api.Tests.Orders_Total"], [], true));

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var reason = Assert.Single(result.Reasons);
        Assert.Contains("passed before the migration and fails after it", reason);
        Assert.Contains("Api.Tests.Orders_Total", reason);
    }

    [Fact]
    public void AllFailuresPreExisting_SaysTheMigrationBrokeNothing()
    {
        var result = ScoreFailedTests(new TestComparison(false, [], ["Api.Tests.NeedsDatabase"], true));

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        var reason = Assert.Single(result.Reasons);
        Assert.Contains("No test broke because of the migration", reason);
        Assert.Contains("Api.Tests.NeedsDatabase", reason);
    }

    [Fact]
    public void NewFailuresOnTopOfPreExistingOnes_AreCalledOutSeparately()
    {
        var result = ScoreFailedTests(new TestComparison(false, ["New.Break"], ["Old.Break"], true));

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("passed before the migration now fail") && r.Contains("New.Break"));
        Assert.Contains(result.Reasons, r => r.Contains("already failing") && r.Contains("Old.Break"));
    }

    [Fact]
    public void SuiteBrokenBeforeAndNoPerTestResults_IsBlocked()
    {
        // run-aspnetcore's shape: the tests can't even start on the untouched code
        // (runtime not installed), so there is nothing to verify the change with.
        var result = ScoreFailedTests(new TestComparison(false, [], [], FailuresIdentified: false));

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("doesn't pass even on the untouched code"));
    }

    [Fact]
    public void LongListsAreTruncated()
    {
        var many = Enumerable.Range(1, 8).Select(i => $"T{i}").ToList();

        var reason = Assert.Single(ScoreFailedTests(new TestComparison(false, [], many, true)).Reasons);

        Assert.Contains("T5 and 3 more", reason);
        Assert.DoesNotContain("T6", reason);
    }

    [Fact]
    public void TheComparisonTravelsOnTheResult()
    {
        var comparison = new TestComparison(false, [], ["Old.Break"], true);

        Assert.Same(comparison, ScoreFailedTests(comparison).TestComparison);
    }
}
