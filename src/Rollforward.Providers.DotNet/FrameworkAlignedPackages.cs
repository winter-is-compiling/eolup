using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Rollforward.Providers.DotNet;

/// <summary>
/// Finds packages that are versioned in lockstep with the .NET framework (ASP.NET Core,
/// EF Core, Microsoft.Extensions.*, System.Text.Json) and are still on the major version
/// of the framework Rollforward just moved away from.
///
/// A target-framework bump leaves these where they were, and that is a classic cause of
/// post-upgrade failures: found by upgrading a demo solution from net8.0 to net10.0
/// whose API tests used Microsoft.AspNetCore.Mvc.Testing 8.0.x — its test server no
/// longer worked with the newer System.Text.Json, and two tests returned HTTP 500.
///
/// This is a hint, not a verdict: some Microsoft.Extensions.* packages version
/// independently of the framework, so the result is only ever reported when the build
/// or tests have already failed. Entries carrying a Condition are skipped — someone is
/// already choosing a version per target framework on purpose.
/// </summary>
internal static partial class FrameworkAlignedPackages
{
    private static readonly string[] Families =
    [
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.",
        "System.Text.Json",
    ];

    /// <summary>
    /// The framework-aligned packages declared in the given project files (and any
    /// Directory.Packages.props under <paramref name="root"/>) whose version is on
    /// <paramref name="oldMajor"/>, as "Id Version (relative/path)" lines.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, IEnumerable<string> projectFiles, int oldMajor)
    {
        var files = projectFiles
            .Concat(Directory.Exists(root)
                ? Directory.GetFiles(root, "Directory.Packages.props", SearchOption.AllDirectories)
                : [])
            .Where(f => !IsBuildOutput(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var found = new List<string>();
        foreach (var file in files)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (XmlException) { continue; } // not ours to judge; the build will say what's wrong

            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
            {
                if (item.Attribute("Condition") is not null || item.Parent?.Attribute("Condition") is not null)
                    continue;

                var id = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
                if (id is null || !Families.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var version = (string?)item.Attribute("Version")
                    ?? (string?)item.Attribute("VersionOverride")
                    ?? item.Elements().FirstOrDefault(e => e.Name.LocalName is "Version" or "VersionOverride")?.Value;
                if (version is null || MajorOf(version) != oldMajor)
                    continue;

                found.Add($"{id} {version.Trim()} ({Path.GetRelativePath(root, file).Replace('\\', '/')})");
            }
        }

        return found;
    }

    /// <summary>The leading major of a literal, floating or range version ("8.0.11", "8.*", "[8.0.0,9.0.0)"); null for anything else (e.g. a $(Property)).</summary>
    private static int? MajorOf(string version)
    {
        var match = LeadingMajorPattern().Match(version);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"^\s*[\[(]?\s*(\d+)\.")]
    private static partial Regex LeadingMajorPattern();
}
