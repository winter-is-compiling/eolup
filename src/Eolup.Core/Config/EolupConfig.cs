using Eolup.Core.Eol;

namespace Eolup.Core.Config;

/// <summary>
/// The schema for a repo's .eolup.yml — see ARCHITECTURE.md,
/// "Configuration: target version is explicit, never assumed".
/// </summary>
public sealed class EolupConfig
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
    /// When the framework bump alone breaks the build or the tests, move the packages that version
    /// with the framework (ASP.NET Core, EF Core, Microsoft.Extensions.*) to the target major and
    /// re-run; the bump is kept only if that makes the migration pass. Off by default: nothing but the
    /// target framework changes unless this is on. YAML key: `bumpPackages`. The CLI's
    /// `--bump-packages` flag turns it on too.
    /// </summary>
    public bool BumpPackages { get; set; }

    /// <summary>
    /// Minimum line coverage (percent, of the non-test source the migration recompiles)
    /// below which a verdict is capped at NeedsReview. Zero coverage is always Blocked.
    /// Only applies when coverage could be measured. YAML key: `minCoverage`.
    /// </summary>
    public int MinCoverage { get; set; } = 50;

    /// <summary>
    /// How long a restore or build may run before Eolup stops it. A repo's own build is the slowest thing Eolup
    /// runs (large solutions take tens of minutes, more on a busy CI runner), and a limit that is too tight
    /// turns a slow build into "can't verify". YAML key: `buildTimeoutMinutes`. The CLI's `--build-timeout` overrides it.
    /// </summary>
    public int BuildTimeoutMinutes { get; set; } = DefaultBuildTimeoutMinutes;

    /// <summary>
    /// How long the test run may take. A suite that exceeds it is reported as Blocked ("did not finish"), never as
    /// passed or failed. YAML key: `testTimeoutMinutes`. The CLI's `--test-timeout` overrides it.
    /// </summary>
    public int TestTimeoutMinutes { get; set; } = DefaultTestTimeoutMinutes;

    public const int DefaultBuildTimeoutMinutes = 30;
    public const int DefaultTestTimeoutMinutes = 60;

    public static EolupConfig Default => new();
}
