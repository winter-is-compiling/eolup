namespace Eolup.Core.Models;

/// <summary>
/// The raw, unscored signals a language provider reports after attempting a
/// remediation. ConfidenceScorer turns this into a RemediationResult — kept
/// separate so the scoring rules stay a pure function, independent of any
/// provider's implementation details.
/// </summary>
/// <param name="FrameworkAlignedPackages">
/// Packages still on the old framework's major version ("Id Version (file)"), reported only
/// when the build or tests failed — a likely cause, never a verdict on its own.
/// </param>
/// <param name="PackagesBumped">
/// Opt-in package bump that was kept: "Id old → new (file)" for each package moved to the target
/// framework's major, which is what made the build and tests pass. Reported so a reviewer sees exactly what changed.
/// </param>
/// <param name="UnhelpfulPackageBumps">
/// Packages that were bumped to try to fix a failure, but the retry still failed, so the bump was dropped.
/// </param>
public sealed record RemediationOutcome(
    bool BuildSucceeded,
    bool TestProjectExists,
    bool? TestsPassed,
    IReadOnlyList<string> ManualActionMarkers,
    string? BranchName,
    CoverageReport? Coverage = null,
    TestComparison? TestComparison = null,
    IReadOnlyList<string>? FrameworkAlignedPackages = null,
    IReadOnlyList<string>? PackagesBumped = null,
    IReadOnlyList<string>? UnhelpfulPackageBumps = null
);
