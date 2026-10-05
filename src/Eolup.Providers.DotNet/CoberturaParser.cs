using System.Xml.Linq;
using Eolup.Core.Models;

namespace Eolup.Providers.DotNet;

/// <summary>
/// Turns the Cobertura XML that coverlet writes during `dotnet test` into a
/// per-file <see cref="CoverageReport"/>. A solution with several test projects
/// produces several reports covering overlapping source, so lines are merged by
/// (file, line) — a line counts as covered if any report hit it.
/// </summary>
internal static class CoberturaParser
{
    /// <param name="reportFiles">Paths of the coverage.cobertura.xml files to merge.</param>
    /// <param name="projectRoot">Only files under this directory are reported; paths are shown relative to it.</param>
    /// <param name="excludedDirectories">Directories whose files are not "the migrated code" (the test projects themselves).</param>
    /// <returns>The merged report, or null if no coverable line was found (nothing to judge by).</returns>
    public static CoverageReport? Merge(
        IEnumerable<string> reportFiles, string projectRoot, IReadOnlyCollection<string> excludedDirectories)
    {
        var root = Path.GetFullPath(projectRoot);
        var excluded = excludedDirectories.Select(d => EnsureTrailingSeparator(Path.GetFullPath(d))).ToList();

        // file -> line number -> hit at least once
        var files = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);

        foreach (var reportFile in reportFiles)
        {
            var document = XDocument.Load(reportFile);
            var sources = document.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();

            foreach (var cls in document.Descendants("class"))
            {
                var fileName = (string?)cls.Attribute("filename");
                if (string.IsNullOrEmpty(fileName)) continue;

                var fullPath = ResolvePath(fileName, sources);
                if (!IsMigratedSource(fullPath, root, excluded)) continue;

                if (!files.TryGetValue(fullPath, out var lines))
                    files[fullPath] = lines = [];

                foreach (var line in cls.Descendants("line"))
                {
                    if (!int.TryParse((string?)line.Attribute("number"), out var number)) continue;
                    var hit = int.TryParse((string?)line.Attribute("hits"), out var hits) && hits > 0;
                    lines[number] = lines.GetValueOrDefault(number) || hit;
                }
            }
        }

        var perFile = files
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => new FileCoverage(
                Path.GetRelativePath(root, kv.Key).Replace('\\', '/'),
                kv.Value.Count(l => l.Value),
                kv.Value.Count))
            .OrderBy(f => f.File, StringComparer.Ordinal)
            .ToList();

        return perFile.Count == 0 ? null : new CoverageReport(perFile);
    }

    private static string ResolvePath(string fileName, IReadOnlyList<string> sources)
    {
        if (Path.IsPathRooted(fileName)) return Path.GetFullPath(fileName);

        foreach (var source in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(source, fileName));
            if (File.Exists(candidate)) return candidate;
        }

        return Path.GetFullPath(fileName);
    }

    private static bool IsMigratedSource(string fullPath, string root, IReadOnlyList<string> excluded)
    {
        if (!fullPath.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase)) return false;
        if (excluded.Any(dir => fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))) return false;

        // Generated code (obj/): compiler output, not something the team wrote or reviews.
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return !relative.Split('/').Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
