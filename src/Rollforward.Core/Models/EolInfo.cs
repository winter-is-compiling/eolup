namespace Rollforward.Core.Models;

/// <summary>
/// Version/support-window data for a single release cycle, sourced from an
/// external EOL calendar (endoflife.date in v0). Rollforward never maintains
/// this data itself — see ARCHITECTURE.md, "External data dependency".
/// </summary>
public sealed record EolInfo(
    string Cycle,
    DateOnly? ReleaseDate,
    DateOnly? EolDate,
    bool IsLts,
    bool IsLatest
);
