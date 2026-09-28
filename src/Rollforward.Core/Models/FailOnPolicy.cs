namespace Rollforward.Core.Models;

/// <summary>
/// Which remediation verdicts should turn a CI step red. Whether a Blocked or
/// NeedsReview result is a failure is the adopting team's policy, not ours, so
/// the default is <see cref="None"/> — Rollforward reports, the team decides.
/// </summary>
public enum FailOnPolicy
{
    /// <summary>Never fail on a verdict (the default). Errors still fail.</summary>
    None,

    /// <summary>Fail only when the verdict is Blocked.</summary>
    Blocked,

    /// <summary>Fail when the verdict is NeedsReview or Blocked — anything short of HighConfidence.</summary>
    NeedsReview,
}

public static class FailOnPolicyExtensions
{
    /// <summary>Accepted spellings, matching the CLI flag and the Action input.</summary>
    public static readonly IReadOnlyList<string> AcceptedValues = ["none", "blocked", "needs-review"];

    public static FailOnPolicy Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => FailOnPolicy.None,
        "blocked" => FailOnPolicy.Blocked,
        "needs-review" => FailOnPolicy.NeedsReview,
        _ => throw new RollforwardUserException(
            $"Unknown fail-on value '{value}'. Use one of: {string.Join(", ", AcceptedValues)}."),
    };

    public static bool ShouldFail(this FailOnPolicy policy, ConfidenceVerdict verdict) => policy switch
    {
        FailOnPolicy.Blocked => verdict == ConfidenceVerdict.Blocked,
        FailOnPolicy.NeedsReview => verdict != ConfidenceVerdict.HighConfidence,
        _ => false,
    };
}
