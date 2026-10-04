namespace Rollforward.Core.Models;

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
public sealed record RemediationOutcome(
    bool BuildSucceeded,
    bool TestProjectExists,
    bool? TestsPassed,
    IReadOnlyList<string> ManualActionMarkers,
    string? BranchName,
    CoverageReport? Coverage = null,
    TestComparison? TestComparison = null,
    IReadOnlyList<string>? FrameworkAlignedPackages = null
);
