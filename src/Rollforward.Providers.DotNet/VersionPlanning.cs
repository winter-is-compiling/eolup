using System.Text.RegularExpressions;
using Rollforward.Core;
using Rollforward.Core.Models;

namespace Rollforward.Providers.DotNet;

/// <summary>
/// The pure decisions behind .NET version detection and "which projects does this
/// run bump" — no MSBuild, no filesystem, so every rule is unit-testable.
///
/// The model (agreed with the maintainer): a repo is upgraded one hop per run. When
/// its projects sit on different versions, the OLDEST is "current" and only projects
/// on that version move this run; the rest are already ahead and wait, so the whole
/// repo converges instead of some projects leaping several majors at once. Projects
/// on `netstandard*` are left alone entirely — that's a compatibility label that
/// every modern .NET can consume, not an outdated runtime.
///
/// Multi-targeted projects (`&lt;TargetFrameworks&gt;`) follow the same
/// rules entry by entry: the project's version is its oldest modern .NET entry; a
/// run moves only the entries on the repo's current version, one hop, keeping
/// platform suffixes. `netstandard*` and .NET Framework (net461, net48) — whether a
/// whole project or entries in a list — are never touched: they are compatibility
/// targets, and moving .NET Framework to modern .NET is a different migration.
/// </summary>
internal static partial class VersionPlanning
{
    public static bool IsNetStandard(string tfm) =>
        tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);

    /// <summary>.NET Framework TFMs: "net461", "net48", "net472" — no dot, unlike modern "net8.0".</summary>
    public static bool IsNetFramework(string tfm) => NetFrameworkTfm().IsMatch(tfm);

    /// <summary>
    /// The modern-.NET version of a TFM ("net8.0" -> 8.0, "netcoreapp3.1" -> 3.1,
    /// "net8.0-windows" -> 8.0), or null for anything that isn't one (netstandard,
    /// .NET Framework's "net48", a list, unknown). Null means "can't be ordered".
    /// </summary>
    public static Version? TryParse(string tfm)
    {
        var m = ModernTfm().Match(tfm.Trim());
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The platform suffix of a TFM ("-windows10.0.19041" in "net8.0-windows10.0.19041"), or "".</summary>
    public static string PlatformSuffix(string tfm)
    {
        var dash = tfm.IndexOf('-');
        return dash < 0 ? "" : tfm[dash..];
    }

    /// <summary>The entries of an evaluated TargetFramework(s) value: "net8.0;netstandard2.0" -> both.</summary>
    public static IReadOnlyList<string> Tokens(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>How one project's evaluated TargetFramework(s) value is read.</summary>
    private sealed record ProjectVersion(string File, string Value, bool MultiTarget, Version? Oldest, string? OldestName, bool LeftAlone)
    {
        public string Name => Path.GetFileNameWithoutExtension(File);
    }

    private static ProjectVersion Classify(string file, string value)
    {
        var tokens = Tokens(value);
        var multi = tokens.Count > 1;
        var oldestToken = tokens
            .Select(t => (Token: t, Version: TryParse(t)))
            .Where(t => t.Version is not null)
            .OrderBy(t => t.Version)
            .ThenBy(t => t.Token, StringComparer.Ordinal)
            .Select(t => t.Token)
            .FirstOrDefault();

        // Nothing modern to move: netstandard and/or .NET Framework only, whether as a
        // single target or a list. Rollforward never moves either — netstandard is a
        // compatibility label, and taking a .NET Framework project to modern .NET is a
        // different migration it doesn't do. Real library repos keep such projects on
        // purpose (Dapper.EntityFramework on net461, CliWrap.Signaler on net35), and
        // refusing the whole repo over them (the first version of this rule) helped no one.
        var leftAlone = oldestToken is null && tokens.All(t => IsNetStandard(t) || IsNetFramework(t));

        return new ProjectVersion(
            file, value, multi,
            oldestToken is null ? null : TryParse(oldestToken),
            oldestToken is null ? null : oldestToken[..(oldestToken.Length - PlatformSuffix(oldestToken).Length)],
            leftAlone);
    }

    /// <summary>
    /// Decides the repo's current version from its non-test projects' evaluated
    /// TargetFramework(s) values. <paramref name="unresolvedCount"/> projects couldn't
    /// be evaluated at all; they don't vote, but if they outnumber the ones that did,
    /// the few that did aren't representative and we say so (Dapper's lone docs.csproj,
    /// before multi-targeted projects could be read).
    /// </summary>
    public static VersionDetection DetectCurrent(
        string projectPath, IReadOnlyDictionary<string, string> tfmByProject, int unresolvedCount)
    {
        if (unresolvedCount > tfmByProject.Count && tfmByProject.Count > 0)
        {
            var names = string.Join(", ", tfmByProject.Keys.Select(Path.GetFileNameWithoutExtension));
            var tfms = string.Join(", ", tfmByProject.Values.Distinct());
            throw new RollforwardUserException(
                $"Only {tfmByProject.Count} of {tfmByProject.Count + unresolvedCount} projects under '{projectPath}' could be " +
                $"evaluated ({names}: {tfms}); the rest couldn't be read, so that result wouldn't represent the repo. " +
                "Point Rollforward at a directory whose projects it can evaluate.");
        }

        var projects = tfmByProject.Select(kv => Classify(kv.Key, kv.Value)).ToList();
        var notes = new List<string>();

        foreach (var p in projects.Where(p => p.LeftAlone))
        {
            notes.Add(Tokens(p.Value).All(IsNetStandard)
                ? $"{p.Name} ({p.Value}) is left alone — netstandard runs on every modern .NET, so it isn't upgraded."
                : $"{p.Name} ({p.Value}) is left alone — it targets .NET Framework, and moving a project from .NET Framework " +
                  "to modern .NET is a different migration Rollforward doesn't do.");
        }

        foreach (var p in projects.Where(p => p is { MultiTarget: true, LeftAlone: false, Oldest: not null } &&
                                              Tokens(p.Value).Any(t => IsNetStandard(t) || IsNetFramework(t))))
        {
            notes.Add($"{p.Name} targets several frameworks ({p.Value}); only its modern .NET entries move — " +
                      "netstandard and .NET Framework entries are left alone.");
        }

        var candidates = projects.Where(p => !p.LeftAlone).ToList();
        if (candidates.Count == 0)
        {
            throw new RollforwardUserException(
                $"Every project that could be read under '{projectPath}' targets only netstandard or .NET Framework, " +
                "which Rollforward leaves alone — there's nothing to upgrade. (Moving .NET Framework to modern .NET is a " +
                "different migration Rollforward doesn't do.)");
        }

        // A project Rollforward can't order (an unknown TFM such as "uap10.0") leaves no
        // honest "oldest": refuse rather than guess.
        var unorderable = candidates.Where(p => p.Oldest is null).ToList();
        if (unorderable.Count > 0)
        {
            if (unorderable.Count == candidates.Count &&
                candidates.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                return new VersionDetection(candidates[0].Value, notes); // one, consistent (if unusual) version
            }

            var summary = string.Join(", ", candidates.Select(p => $"{p.Name}={p.Value}"));
            throw new RollforwardUserException(
                $"'{projectPath}' contains projects on target frameworks Rollforward can't order ({summary}). " +
                "Point it at a specific service's directory instead of a shared parent.");
        }

        var oldest = candidates.OrderBy(p => p.Oldest).ThenBy(p => p.OldestName, StringComparer.Ordinal).First();
        var ahead = candidates
            .Where(p => p.Oldest! > oldest.Oldest!)
            .Select(p => $"{p.Name}={p.Value}")
            .ToList();

        if (ahead.Count > 0)
        {
            notes.Add(
                $"Projects are on different versions; {oldest.OldestName} is the oldest, so this run moves only the projects on it. " +
                $"Already ahead and left as they are: {string.Join(", ", ahead)}. Once everything is on the same version, the next run moves the whole repo forward together.");
        }

        return new VersionDetection(oldest.OldestName!, notes);
    }

    /// <summary>One project this run rewrites.</summary>
    /// <param name="File">The project file.</param>
    /// <param name="NewTfm">
    /// What its TargetFramework(s) value becomes — for a multi-targeted project, the
    /// whole new list (informational: the files are rewritten entry by entry, see
    /// <see cref="RetargetList"/>).
    /// </param>
    /// <param name="MultiTarget">Whether it declares &lt;TargetFrameworks&gt; (plural).</param>
    public sealed record ProjectBump(string File, string NewTfm, bool MultiTarget);

    /// <summary>
    /// Which projects this run rewrites, given every project's evaluated TargetFramework(s)
    /// value (test projects included): the ones with an entry on the current version,
    /// and only if the target really is a newer version (never a downgrade). Platform
    /// suffixes are kept ("net8.0-windows" -> "net10.0-windows"); netstandard and .NET
    /// Framework entries are never touched.
    /// </summary>
    public static IReadOnlyList<ProjectBump> SelectProjectsToBump(
        IReadOnlyDictionary<string, string> allTfms, string currentTfm, string targetTfm)
    {
        var current = TryParse(currentTfm);
        var target = TryParse(targetTfm);
        if (current is null || target is null || target <= current)
            return [];

        var bumps = new List<ProjectBump>();
        foreach (var (file, value) in allTfms)
        {
            var tokens = Tokens(value);
            if (tokens.Count == 1)
            {
                if (!IsNetStandard(tokens[0]) && TryParse(tokens[0]) == current)
                    bumps.Add(new ProjectBump(file, targetTfm + PlatformSuffix(tokens[0]), MultiTarget: false));
            }
            else if (tokens.Any(t => TryParse(t) == current))
            {
                bumps.Add(new ProjectBump(file, RetargetList(value, current, targetTfm), MultiTarget: true));
            }
        }

        return bumps;
    }

    /// <summary>
    /// Moves the entries of a TargetFrameworks list that are on <paramref name="current"/>
    /// to <paramref name="targetTfm"/> (keeping each entry's platform suffix), leaves every
    /// other entry — netstandard, .NET Framework, other versions, `$(Property)`
    /// references — exactly as written, and drops an entry the move turned into a
    /// duplicate ("net6.0;net8.0" -> "net8.0"): the end-of-life target is replaced, not
    /// kept alongside. Whitespace around entries is preserved, so the diff stays minimal.
    /// </summary>
    public static string RetargetList(string list, Version current, string targetTfm)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var piece in list.Split(';'))
        {
            var token = piece.Trim();
            var mapped = token.Length > 0 && TryParse(token) == current
                ? piece.Replace(token, targetTfm + PlatformSuffix(token))
                : piece;

            var mappedToken = mapped.Trim();
            if (mappedToken.Length > 0 && !seen.Add(mappedToken))
                continue; // duplicate created by the move

            result.Add(mapped);
        }

        return string.Join(';', result);
    }

    [GeneratedRegex(@"^(?:net|netcoreapp)(\d+)\.(\d+)(?:-.+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ModernTfm();

    [GeneratedRegex(@"^net\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex NetFrameworkTfm();
}
