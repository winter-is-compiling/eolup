using Rollforward.Core.Models;

namespace Rollforward.Core.Eol;

/// <summary>
/// Resolves the semantic target values allowed in .rollforward.yml ("next-major",
/// "next-lts") against the real set of known cycles. An explicit version in config
/// is returned as-is — target is never assumed to be "latest". See
/// ARCHITECTURE.md, "Configuration: target version is explicit, never assumed".
/// </summary>
public static class TargetVersionResolver
{
    public const string NextMajor = "next-major";
    public const string NextLts = "next-lts";

    public static string Resolve(IReadOnlyList<EolInfo> cycles, string currentVersion, string targetSpec)
    {
        if (!string.Equals(targetSpec, NextMajor, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetSpec, NextLts, StringComparison.OrdinalIgnoreCase))
        {
            return targetSpec; // explicit version — pass through unchanged
        }

        var currentMajor = MajorOf(EolEvaluator.ExtractVersionNumber(currentVersion) ?? currentVersion);

        var candidates = cycles
            .Select(c => new { Cycle = c, Major = MajorOf(c.Cycle) })
            .Where(c => c.Major.HasValue && c.Major > currentMajor)
            .OrderBy(c => c.Major)
            .ToList();

        if (string.Equals(targetSpec, NextLts, StringComparison.OrdinalIgnoreCase))
        {
            var nextLts = candidates.FirstOrDefault(c => c.Cycle.IsLts);
            if (nextLts is not null) return nextLts.Cycle.Cycle;

            // No LTS ahead of current — fall back to the highest known LTS overall.
            var latestLts = cycles.Where(c => c.IsLts).OrderByDescending(c => MajorOf(c.Cycle)).FirstOrDefault();
            if (latestLts is not null) return latestLts.Cycle;
        }

        var nextMajor = candidates.FirstOrDefault();
        return nextMajor?.Cycle.Cycle ?? currentVersion;
    }

    /// <summary>
    /// The whole journey from the current version to where <paramref name="targetSpec"/>
    /// leads, one hop at a time — each hop is what <see cref="Resolve"/> would pick
    /// from the previous one. Rollforward only ever performs the <em>first</em> hop
    /// per run (small PRs, breaking changes met one hop at a time); this exists so the
    /// user can see where the journey ends. An explicit target is a single hop. Empty
    /// means already at the destination.
    /// </summary>
    public static IReadOnlyList<string> Path(IReadOnlyList<EolInfo> cycles, string currentVersion, string targetSpec)
    {
        var isSemantic =
            string.Equals(targetSpec, NextMajor, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetSpec, NextLts, StringComparison.OrdinalIgnoreCase);
        if (!isSemantic)
            return IsAtOrBeyond(currentVersion, targetSpec) ? [] : [targetSpec];

        var path = new List<string>();
        var current = currentVersion;
        var currentMajor = MajorOf(EolEvaluator.ExtractVersionNumber(current) ?? current);

        // Bounded: each accepted hop strictly increases the major, so this ends on
        // its own; the cap only guards against a malformed cycle list.
        for (var i = 0; i < 32; i++)
        {
            var next = Resolve(cycles, current, targetSpec);
            var nextMajor = MajorOf(next);
            if (nextMajor is null || currentMajor is null || nextMajor <= currentMajor)
                break;

            path.Add(next);
            current = next;
            currentMajor = nextMajor;
        }

        return path;
    }

    /// <summary>
    /// Whether <paramref name="current"/> ("net10.0") has already reached an explicit
    /// target ("10", "10.0", "net10.0"). Unparseable input counts as "not reached", so
    /// the target is still attempted and the provider reports anything odd.
    /// </summary>
    private static bool IsAtOrBeyond(string current, string target)
    {
        static Version? Parse(string value)
        {
            var number = EolEvaluator.ExtractVersionNumber(value);
            if (number is null) return null;
            return Version.TryParse(number.Contains('.') ? number : number + ".0", out var v) ? v : null;
        }

        var (c, t) = (Parse(current), Parse(target));
        return c is not null && t is not null && c >= t;
    }

    private static int? MajorOf(string cycle)
    {
        var leading = cycle.Split('.').FirstOrDefault();
        return int.TryParse(leading, out var major) ? major : null;
    }
}
