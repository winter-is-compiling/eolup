using System.IO.Enumeration;
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
/// This is a hint, not a verdict, and it must never be the reason a run fails: some
/// packages under those prefixes version independently of the framework (see
/// <see cref="IndependentlyVersioned"/>), so the result is only ever reported when the
/// build or tests have already failed, entries carrying a Condition are skipped (someone
/// is already choosing a version per target framework on purpose), and anything that can't
/// be read is skipped rather than thrown.
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
    /// Packages under the prefixes above that have their own version line, so a "8.x" on a
    /// net8.0 project is a coincidence, not alignment (OData 8.x is not "for .NET 8").
    /// </summary>
    private static readonly string[] IndependentlyVersioned =
    [
        "Microsoft.AspNetCore.OData",
        "Microsoft.AspNetCore.Mvc.Versioning",
        "Microsoft.AspNetCore.SpaServices",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.Azure",
        "Microsoft.Extensions.Compliance",
        "Microsoft.Extensions.Configuration.AzureAppConfiguration",
        "Microsoft.Extensions.DependencyInjection.AutoActivation",
        "Microsoft.Extensions.Diagnostics.ExceptionSummarization",
        "Microsoft.Extensions.Diagnostics.Testing",
        "Microsoft.Extensions.Http.Resilience",
        "Microsoft.Extensions.Resilience",
        "Microsoft.Extensions.ServiceDiscovery",
        "Microsoft.Extensions.Telemetry",
        "Microsoft.Extensions.TimeProvider.Testing",
        "Microsoft.Extensions.VectorData",
    ];

    /// <summary>Files that declare packages for many projects at once, wherever they sit under the root.</summary>
    private static readonly HashSet<string> SharedBuildFiles =
        new(["Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SkippedDirectories =
        new(["bin", "obj", "node_modules", ".git"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The framework-aligned packages declared in the given project files (and in any shared
    /// Directory.Packages/Build.props/targets under <paramref name="root"/>) whose version is
    /// on <paramref name="oldMajor"/>, as "Id Version (relative/path)" lines.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, IEnumerable<string> projectFiles, int oldMajor) =>
        FindStale(root, projectFiles, oldMajor).Select(p => p.Describe()).ToList();

    /// <summary>
    /// Same search as <see cref="Find"/>, but returning where each package is declared so
    /// <see cref="PackageVersionEditor"/> can rewrite exactly that entry.
    /// </summary>
    public static IReadOnlyList<StalePackage> FindStale(string root, IEnumerable<string> projectFiles, int oldMajor)
    {
        var files = projectFiles
            .Concat(FindSharedBuildFiles(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var found = new List<StalePackage>();
        foreach (var file in files)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
            {
                continue; // not ours to judge, and a hint must never fail the run
            }

            var elementIndex = -1;
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
            {
                elementIndex++; // document order, counting every such element, not only the stale ones
                // Chosen per framework, configuration or anything else on purpose.
                if (item.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null))
                    continue;

                var id = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
                if (id is null || !IsFrameworkAligned(id))
                    continue;

                var version = (string?)item.Attribute("Version")
                    ?? (string?)item.Attribute("VersionOverride")
                    ?? item.Elements().FirstOrDefault(e => e.Name.LocalName is "Version" or "VersionOverride")?.Value;
                if (version is null || MajorOf(version) != oldMajor)
                    continue;

                found.Add(new StalePackage(id, version.Trim(), file, Path.GetRelativePath(root, file).Replace('\\', '/'), elementIndex));
            }
        }

        return found;
    }

    private static bool IsFrameworkAligned(string id) =>
        Families.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase)) &&
        !IndependentlyVersioned.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shared build files under <paramref name="root"/>, without descending into bin, obj,
    /// node_modules or .git. The pruning is applied below the root only, so a repo that happens
    /// to live under a directory called "bin" is still searched.
    /// </summary>
    private static IEnumerable<string> FindSharedBuildFiles(string root) =>
        !Directory.Exists(root)
            ? []
            : new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => e.ToFullPath(), new EnumerationOptions { RecurseSubdirectories = true })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && SharedBuildFiles.Contains(e.FileName.ToString()),
                ShouldRecursePredicate = (ref FileSystemEntry e) => !SkippedDirectories.Contains(e.FileName.ToString()),
            };

    /// <summary>The leading major of a literal, floating or range version ("8.0.11", "8.*", "[8.0.0,9.0.0)"); null for anything else (e.g. a $(Property)).</summary>
    private static int? MajorOf(string version)
    {
        var match = LeadingMajorPattern().Match(version);
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : null;
    }

    [GeneratedRegex(@"^\s*[\[(]?\s*(\d+)\.")]
    private static partial Regex LeadingMajorPattern();
}
