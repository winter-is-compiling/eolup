using Rollforward.Core.Confidence;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

/// <summary>
/// One rule decides whether a failing suite is the migration's fault. The scorer uses it to attach the stale-package
/// hint and the provider uses it to decide whether a package bump is worth retrying, so the two can't disagree.
/// </summary>
public class TestComparisonBlameTests
{
    public static TheoryData<TestComparison?, bool> Cases => new()
    {
        { null, true },                                                          // nothing to compare: assume the migration
        { new TestComparison(true, [], [], true), true },                        // passed before, fails now
        { new TestComparison(true, [], [], false), true },
        { new TestComparison(false, ["A.B"], ["C.D"], true), true },             // some tests newly fail
        { new TestComparison(false, [], ["C.D"], true), false },                 // everything failing was already failing
        { new TestComparison(false, [], [], false), false },                     // untouched code gave nothing to compare
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BlamesMigration_MatchesTheRule(TestComparison? comparison, bool expected) =>
        Assert.Equal(expected, TestComparison.BlamesMigration(comparison));

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheScorerAttachesTheHint_ExactlyWhenTheMigrationIsBlamed(TestComparison? comparison, bool blamed)
    {
        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: false, manualActionMarkers: [], branchName: "b",
            testComparison: comparison, frameworkAlignedPackages: ["Microsoft.Extensions.Options 8.0.0 (App/App.csproj)"]);

        Assert.Equal(blamed, result.Reasons.Any(r => r.Contains("still on the old major")));
    }

    [Fact]
    public void AKeptBump_IsStillNamed_WhenThePassingTestsExecuteNothing()
    {
        var coverage = new CoverageReport([new FileCoverage("Greeter.cs", CoveredLines: 0, CoverableLines: 12)]);

        var result = ConfidenceScorer.Score(
            buildSucceeded: true, testProjectExists: true, testsPassed: true, manualActionMarkers: [], branchName: "b",
            coverage: coverage, packagesBumped: ["Microsoft.Extensions.Options 8.0.0 → 10.0.1 (App/App.csproj)"]);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("were also moved to the new framework's major"));
    }
}
