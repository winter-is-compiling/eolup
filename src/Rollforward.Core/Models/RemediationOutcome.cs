namespace Rollforward.Core.Models;

/// <summary>
/// The raw, unscored signals a language provider reports after attempting a
/// remediation. ConfidenceScorer turns this into a RemediationResult — kept
/// separate so the scoring rules stay a pure function, independent of any
/// provider's implementation details.
/// </summary>
public sealed record RemediationOutcome(
    bool BuildSucceeded,
    bool TestProjectExists,
    bool? TestsPassed,
    IReadOnlyList<string> ManualActionMarkers,
    string? BranchName,
    CoverageReport? Coverage = null,
    TestComparison? TestComparison = null
);
