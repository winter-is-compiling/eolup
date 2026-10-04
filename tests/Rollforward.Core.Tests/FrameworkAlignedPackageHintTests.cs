using Rollforward.Core.Confidence;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

/// <summary>
/// Packages that version with the framework are named as a likely cause when the
/// migration broke the build or tests — and only then.
/// </summary>
public class FrameworkAlignedPackageHintTests
{
    private static readonly IReadOnlyList<string> Stale =
        ["Microsoft.AspNetCore.Mvc.Testing 8.0.11 (tests/Api.Tests/Api.Tests.csproj)"];

    private static RemediationResult Score(
        bool build = true, bool? testsPassed = true, TestComparison? comparison = null, IReadOnlyList<string>? packages = null) =>
        ConfidenceScorer.Score(
            buildSucceeded: build, testProjectExists: true, testsPassed: testsPassed,
            manualActionMarkers: [], branchName: "b", testComparison: comparison, frameworkAlignedPackages: packages);

    [Fact]
    public void FailingTests_NameTheStalePackages_WithoutChangingTheVerdict()
    {
        var result = Score(testsPassed: false, comparison: new TestComparison(true, ["Api.Tests.Due"], [], true), packages: Stale);

        Assert.Equal(ConfidenceVerdict.NeedsReview, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("likely regression") && r.Contains("Api.Tests.Due"));
        Assert.Contains(result.Reasons, r => r.Contains("Microsoft.AspNetCore.Mvc.Testing 8.0.11") && r.Contains("still on the old major"));
    }

    [Fact]
    public void FailingTestsWithNoComparison_AlsoGetTheHint()
    {
        var result = Score(testsPassed: false, packages: Stale);

        Assert.Contains(result.Reasons, r => r.Contains("Microsoft.AspNetCore.Mvc.Testing"));
    }

    [Fact]
    public void FailedBuild_StaysBlocked_AndNamesTheStalePackages()
    {
        var result = Score(build: false, testsPassed: null, packages: Stale);

        Assert.Equal(ConfidenceVerdict.Blocked, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("Microsoft.AspNetCore.Mvc.Testing"));
    }

    [Fact]
    public void FailuresThatWereAlreadyThereBeforeTheMigration_GetNoHint()
    {
        var alreadyFailing = new TestComparison(false, [], ["Api.Tests.Flaky"], true);

        var result = Score(testsPassed: false, comparison: alreadyFailing, packages: Stale);

        Assert.DoesNotContain(result.Reasons, r => r.Contains("still on the old major"));
    }

    [Fact]
    public void PassingRun_IsNeverAnnotated()
    {
        var result = Score(packages: Stale);

        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("still on the old major"));
    }

    [Fact]
    public void NoStalePackages_LeavesReasonsUntouched()
    {
        var withNone = Score(testsPassed: false, packages: []);
        var without = Score(testsPassed: false);

        Assert.Equal(without.Reasons, withNone.Reasons);
    }
}
