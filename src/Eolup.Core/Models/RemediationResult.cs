namespace Eolup.Core.Models;

public sealed record RemediationResult(
    ConfidenceVerdict Verdict,
    IReadOnlyList<string> Reasons,
    bool BuildSucceeded,
    bool? TestsPassed,
    bool TestProjectExists,
    IReadOnlyList<string> ManualActionMarkers,
    string? BranchName,
    CoverageReport? Coverage = null,
    TestComparison? TestComparison = null
);
