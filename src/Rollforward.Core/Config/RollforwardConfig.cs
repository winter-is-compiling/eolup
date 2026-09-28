using Rollforward.Core.Eol;

namespace Rollforward.Core.Config;

/// <summary>
/// The schema for a repo's .rollforward.yml — see ARCHITECTURE.md,
/// "Configuration: target version is explicit, never assumed".
/// </summary>
public sealed class RollforwardConfig
{
    /// <summary>An explicit version (e.g. "19"), or one of "next-major" / "next-lts".</summary>
    public string Target { get; set; } = TargetVersionResolver.NextLts;

    /// <summary>
    /// Explicit build target (a .sln/.slnx or .csproj filename, relative to the repo
    /// root) to use when the repo can't be unambiguously resolved automatically —
    /// e.g. it has more than one solution file at its root. Optional: most repos
    /// never need this. See ARCHITECTURE.md and MANUAL_TEST_PASS.md, Pass #1 finding 2.
    /// </summary>
    public string? Solution { get; set; }

    /// <summary>
    /// Carry on to the next upgrade hop within one run while each hop is HighConfidence,
    /// stopping at the first that isn't. Off by default: one hop per run, one
    /// small PR each. YAML key: `chain`. The CLI's `--chain` flag turns it on too.
    /// </summary>
    public bool Chain { get; set; }

    /// <summary>
    /// Minimum line coverage (percent, of the non-test source the migration recompiles)
    /// below which a verdict is capped at NeedsReview. Zero coverage is always Blocked.
    /// Only applies when coverage could be measured. YAML key: `minCoverage`.
    /// </summary>
    public int MinCoverage { get; set; } = 50;

    public static RollforwardConfig Default => new();
}
