namespace Rollforward.Core.Models;

/// <summary>
/// The outcome of running remediation against a project. This is the core trust
/// signal Rollforward reports — see ARCHITECTURE.md for the reasoning behind
/// keeping this a deterministic, explainable rule set rather than a model score.
/// </summary>
public enum ConfidenceVerdict
{
    /// <summary>Build succeeded, tests passed, no manual-action markers left by the provider. Safe to auto-open a PR.</summary>
    HighConfidence,

    /// <summary>Build succeeded but the provider left one or more manual-action markers that need human judgment.</summary>
    NeedsReview,

    /// <summary>Cannot be safely verified (e.g. no test project exists) or the build itself failed.</summary>
    Blocked
}
