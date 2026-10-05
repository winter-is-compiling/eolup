namespace Eolup.Core.Models;

/// <summary>Line coverage of one source file, as measured while the project's own tests ran.</summary>
public sealed record FileCoverage(string File, int CoveredLines, int CoverableLines)
{
    public double Percent => CoverableLines == 0 ? 100 : 100.0 * CoveredLines / CoverableLines;
}

/// <summary>
/// Per-file line coverage of the code a migration is recompiled against — for a
/// target-framework bump that is every non-test source file in the bumped projects.
/// Files with no coverable lines are omitted by whoever builds the report.
/// </summary>
public sealed record CoverageReport(IReadOnlyList<FileCoverage> Files)
{
    public int CoveredLines => Files.Sum(f => f.CoveredLines);
    public int CoverableLines => Files.Sum(f => f.CoverableLines);

    /// <summary>Overall line coverage, 0–100. An empty report counts as 0: nothing was exercised.</summary>
    public double Percent => CoverableLines == 0 ? 0 : 100.0 * CoveredLines / CoverableLines;
}
