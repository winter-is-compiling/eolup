namespace Eolup.Core.Models;

/// <summary>
/// The raw, unscored signals a language provider reports after attempting a
/// remediation. ConfidenceScorer turns this into a RemediationResult — kept
/// separate so the scoring rules stay a pure function, independent of any
/// provider's implementation details.
/// </summary>
/// <param name="TestsPassed">
/// True when tests ran and passed, false when the test run failed, null when no test ran: there is no test
/// project, the build failed, or the test command succeeded but executed nothing (a test project switched
/// off in the solution's build, or one that contains no tests).
/// </param>
/// <param name="Unverifiable">
/// Why the migration could not be verified at all, in the provider's own words: a build or test run was
/// stopped for taking too long, so no answer exists. Null when every step finished. It makes the verdict Blocked.
/// </param>
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
    IReadOnlyList<string>? UnhelpfulPackageBumps = null,
    string? Unverifiable = null
);
