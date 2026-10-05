using System.Globalization;
using Eolup.Core.Models;

namespace Eolup.Core.Confidence;

/// <summary>
/// The decision table for turning raw remediation signals into a ConfidenceVerdict.
/// Deliberately a deterministic rule set, not a model score — see TESTING.md and
/// ARCHITECTURE.md for why explainability matters more than sophistication here.
///
/// Test coverage is judged in two tiers. A test project must exist (else Blocked).
/// Then, when the provider could measure per-file line coverage: zero covered
/// lines is Blocked (the tests pass but exercise nothing), and coverage below the
/// configured minimum caps the verdict at NeedsReview. When coverage could not be
/// measured the verdict is unchanged, but says so — an unmeasured repo is not
/// silently presented as a verified one.
/// </summary>
public static class ConfidenceScorer
{
    public const int DefaultMinCoveragePercent = 50;
    private const int LeastCoveredFilesToName = 3;

    public static RemediationResult Score(
        bool buildSucceeded,
        bool testProjectExists,
        bool? testsPassed,
        IReadOnlyList<string> manualActionMarkers,
        string? branchName,
        CoverageReport? coverage = null,
        int minCoveragePercent = DefaultMinCoveragePercent,
        TestComparison? testComparison = null,
        IReadOnlyList<string>? frameworkAlignedPackages = null,
        IReadOnlyList<string>? packagesBumped = null,
        IReadOnlyList<string>? unhelpfulPackageBumps = null)
    {
        RemediationResult Result(ConfidenceVerdict verdict, List<string> reasons) =>
            new(verdict, reasons, buildSucceeded, testsPassed, testProjectExists, manualActionMarkers, branchName, coverage, testComparison);

        var packages = new PackageSignals(frameworkAlignedPackages, unhelpfulPackageBumps);

        if (!buildSucceeded)
            return Result(ConfidenceVerdict.Blocked,
                WithPackageHint(["Build failed after remediation — cannot verify the change is safe."], packages));

        if (!testProjectExists)
            return Result(ConfidenceVerdict.Blocked,
                ["No test project found in the solution — cannot verify the change is safe."]);

        if (testsPassed == false)
            return ScoreFailedTests(testComparison, packages, Result);

        if (coverage is { CoveredLines: 0 })
            return Result(ConfidenceVerdict.Blocked, AddBumpedPackages(
                [$"Tests passed, but they execute none of the {coverage.CoverableLines} coverable lines of the migrated code — cannot verify the change is safe."],
                packagesBumped));

        var reasons = new List<string>();

        if (manualActionMarkers.Count > 0)
        {
            reasons.Add($"{manualActionMarkers.Count} manual-action marker(s) left by the migration tool — needs human judgment.");
            reasons.AddRange(manualActionMarkers);
        }

        if (coverage is not null && coverage.Percent < minCoveragePercent)
        {
            var leastCovered = coverage.Files
                .Where(f => f.Percent < minCoveragePercent)
                .OrderBy(f => f.Percent)
                .ThenBy(f => f.File, StringComparer.Ordinal)
                .Take(LeastCoveredFilesToName)
                .Select(f => $"{f.File} ({Pct(f.Percent)})");
            reasons.Add(
                $"Line coverage of the migrated code is {Pct(coverage.Percent)}, below the {minCoveragePercent}% minimum — " +
                $"the tests may not catch a regression. Least covered: {string.Join(", ", leastCovered)}.");
        }

        if (reasons.Count > 0)
        {
            AddBumpedPackages(reasons, packagesBumped);
            return Result(ConfidenceVerdict.NeedsReview, reasons);
        }

        var coverageNote = coverage is null
            ? "line coverage was not measured"
            : $"line coverage {Pct(coverage.Percent)}";
        return Result(ConfidenceVerdict.HighConfidence, AddBumpedPackages(
            [$"Build succeeded, existing tests passed ({coverageNote}), no manual-action markers — safe to auto-approve."],
            packagesBumped));
    }

    /// <summary>
    /// The opt-in package bump was kept because it made the migration pass. The verdict is judged as usual —
    /// the same build and tests approved it — but the PR must say what else moved besides the framework.
    /// </summary>
    private static List<string> AddBumpedPackages(List<string> reasons, IReadOnlyList<string>? bumped)
    {
        if (bumped is { Count: > 0 })
            reasons.Add(
                "The framework bump alone broke the build or tests, so these framework-aligned packages were also moved to the new " +
                $"framework's major (its own commit; review it like any dependency update): {Names(bumped)}.");
        return reasons;
    }

    /// <summary>What the package detection and the opt-in bump found, as the scoring rules need it.</summary>
    private sealed record PackageSignals(IReadOnlyList<string>? Stale, IReadOnlyList<string>? BumpDidNotHelp);

    private const int FailedTestsToName = 5;

    /// <summary>
    /// A target-framework bump leaves packages that version with the framework on the old
    /// major (found by a net8.0 → net10.0 demo whose Mvc.Testing 8.0.x broke on net10).
    /// Named as a hint when the build or tests failed, never as a reason on its own.
    /// </summary>
    private static List<string> WithPackageHint(List<string> reasons, PackageSignals packages)
    {
        // The opt-in bump already tried these; say so, so nobody repeats the experiment by hand.
        if (packages.BumpDidNotHelp is { Count: > 0 })
            reasons.Add(
                "Eolup tried moving these framework-aligned packages to the new framework's major and re-running, " +
                $"but that did not fix the failure, so the change was dropped and the branch holds only the framework bump: {Names(packages.BumpDidNotHelp)}.");

        if (packages.Stale is { Count: > 0 })
            reasons.Add(
                "These packages usually version with the framework and are still on the old major — a common cause of " +
                $"post-upgrade failures, worth checking whether they need bumping to match the new framework: {Names(packages.Stale)}.");
        return reasons;
    }

    /// <summary>
    /// The tests failed after the migration. Whether that says anything about the
    /// migration depends on whether they passed before it (found across
    /// three real repos whose suites were already broken on the untouched code).
    /// The stale-package hint rides along exactly when <see cref="TestComparison.BlamesMigration"/> says the
    /// migration is the likely cause — the same rule the provider uses to decide whether a package bump is
    /// worth retrying — so there is one place that decides it.
    /// </summary>
    private static RemediationResult ScoreFailedTests(
        TestComparison? comparison, PackageSignals packages,
        Func<ConfidenceVerdict, List<string>, RemediationResult> scored)
    {
        var blamesMigration = TestComparison.BlamesMigration(comparison);
        RemediationResult result(ConfidenceVerdict verdict, List<string> reasons) =>
            scored(verdict, blamesMigration ? WithPackageHint(reasons, packages) : reasons);

        if (comparison is null)
            return result(ConfidenceVerdict.NeedsReview,
                ["Existing test suite failed after remediation — needs human judgment on whether this is a real regression."]);

        if (comparison.BaselinePassed)
            return result(ConfidenceVerdict.NeedsReview,
                [comparison.FailuresIdentified && comparison.NewFailures.Count > 0
                    ? $"The test suite passed before the migration and fails after it — a likely regression. Newly failing: {Names(comparison.NewFailures)}."
                    : "The test suite passed before the migration and fails after it — a likely regression."]);

        if (!comparison.FailuresIdentified)
            return result(ConfidenceVerdict.Blocked,
                ["The test suite doesn't pass even on the untouched code, and it produced no per-test results to compare, " +
                 "so it can't show whether the migration broke anything — cannot verify the change is safe."]);

        if (comparison.NewFailures.Count > 0)
            return result(ConfidenceVerdict.NeedsReview,
                [$"{comparison.NewFailures.Count} test(s) that passed before the migration now fail — a likely regression: {Names(comparison.NewFailures)}.",
                 $"{comparison.AlreadyFailing.Count} other failing test(s) were already failing before the migration: {Names(comparison.AlreadyFailing)}."]);

        return result(ConfidenceVerdict.NeedsReview,
            [$"No test broke because of the migration: all {comparison.AlreadyFailing.Count} failing test(s) were already failing on the untouched code " +
             $"({Names(comparison.AlreadyFailing)}). Every other test passed, but those can't vouch for this change — worth a look."]);
    }

    private static string Names(IReadOnlyList<string> tests) =>
        tests.Count <= FailedTestsToName
            ? string.Join(", ", tests)
            : string.Join(", ", tests.Take(FailedTestsToName)) + $" and {tests.Count - FailedTestsToName} more";

    private static string Pct(double percent) => percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
