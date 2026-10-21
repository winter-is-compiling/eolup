namespace Eolup.Core.Models;

/// <summary>One upgrade hop of a remediation run, e.g. net6.0 -> net8.0, and how it scored.</summary>
public sealed record RemediationHop(string From, string To, RemediationResult Result);

/// <summary>
/// Every hop one `remediate` run attempted. Without chaining that is exactly one.
/// With chaining the run carries on while each hop is HighConfidence and
/// stops at the first that isn't, so the hops before <see cref="Final"/> are all
/// HighConfidence and each builds on the previous one's branch.
/// </summary>
/// <param name="StartedOn">
/// Where the user's checkout was (a branch, or a commit when detached) before the run, so the output can say how to go
/// back: a completed run leaves the checkout on the last migration branch. Null when the provider didn't capture it.
/// </param>
public sealed record ChainedRemediation(IReadOnlyList<RemediationHop> Hops, string? StartedOn = null)
{
    /// <summary>The last hop attempted — the one whose verdict ended the run.</summary>
    public RemediationHop Final => Hops[^1];

    /// <summary>
    /// The furthest hop that is safe to open a PR for: its branch carries every
    /// HighConfidence hop's commit and nothing else. Null when even the first hop
    /// wasn't HighConfidence.
    /// </summary>
    public RemediationHop? LastHighConfidence =>
        Hops.LastOrDefault(h => h.Result.Verdict == ConfidenceVerdict.HighConfidence);
}
