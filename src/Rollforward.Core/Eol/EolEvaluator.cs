using System.Text.RegularExpressions;
using Rollforward.Core.Models;

namespace Rollforward.Core.Eol;

/// <summary>
/// Turns a raw list of EolInfo cycles plus a detected current version into the
/// status/date fields reported by `rollforward scan`.
/// </summary>
public static partial class EolEvaluator
{
    private const int ApproachingEolThresholdDays = 90;

    public static (EolStatus Status, DateOnly? EolDate, int? DaysUntilEol) Evaluate(
        IReadOnlyList<EolInfo> cycles,
        string currentVersion,
        DateOnly today)
    {
        var match = FindCycle(cycles, currentVersion);
        if (match is null)
            return (EolStatus.Unknown, null, null);

        if (match.EolDate is null)
            return (EolStatus.Current, null, null);

        var daysUntilEol = match.EolDate.Value.DayNumber - today.DayNumber;

        var status = daysUntilEol < 0
            ? EolStatus.PastEol
            : daysUntilEol <= ApproachingEolThresholdDays
                ? EolStatus.ApproachingEol
                : EolStatus.Current;

        return (status, match.EolDate, daysUntilEol);
    }

    /// <summary>
    /// Matches a detected version (e.g. "net8.0", "8.0.4") against an endoflife.date
    /// cycle (e.g. "8.0", "8"), by comparing the leading major(.minor) number rather
    /// than requiring an exact string match — cycles and TFMs aren't always spelled
    /// the same way.
    /// </summary>
    internal static EolInfo? FindCycle(IReadOnlyList<EolInfo> cycles, string version)
    {
        var normalized = ExtractVersionNumber(version);
        if (normalized is null) return null;

        return cycles.FirstOrDefault(c => c.Cycle == normalized)
            ?? cycles.FirstOrDefault(c => ExtractMajor(c.Cycle) == ExtractMajor(normalized));
    }

    internal static string? ExtractVersionNumber(string version)
    {
        var match = VersionNumberRegex().Match(version);
        return match.Success ? match.Value : null;
    }

    private static string? ExtractMajor(string version) =>
        version.Split('.').FirstOrDefault();

    [GeneratedRegex(@"\d+(\.\d+)*")]
    private static partial Regex VersionNumberRegex();
}
