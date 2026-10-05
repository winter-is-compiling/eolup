using System.Text;
using System.Text.RegularExpressions;
using Eolup.Core;
using Eolup.Core.Infrastructure;

namespace Eolup.Providers.DotNet;

internal static partial class CsProjHelper
{
    /// <summary>
    /// Finds .csproj files anywhere under the given directory, excluding
    /// traversal/meta-build projects (SDK "Microsoft.Build.Traversal") — these
    /// aggregate references to other projects rather than containing any
    /// compilable code of their own, have no &lt;TargetFramework&gt; by nature,
    /// and should never be picked as "the project to scan". Found by testing
    /// against Dapper, whose root Build.csproj is exactly this and was getting
    /// picked over the real library projects by the naive "first non-test
    /// .csproj found" heuristic in DetectVersionAsync.
    /// </summary>
    public static IReadOnlyList<string> FindProjectFiles(string projectPath) =>
        Directory.Exists(projectPath)
            ? Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !IsTraversalProject(p))
                .ToList()
            : [];

    private static bool IsTraversalProject(string csprojPath) =>
        File.ReadAllText(csprojPath).Contains("Microsoft.Build.Traversal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The .csproj files whose name suggests a test project (by convention: name contains "Test").</summary>
    public static IReadOnlyList<string> FindTestProjectFiles(string projectPath) =>
        FindProjectFiles(projectPath)
            .Where(p => Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static bool AnyTestProjectExists(string projectPath) => FindTestProjectFiles(projectPath).Count > 0;

    /// <summary>
    /// Reads the effective &lt;TargetFramework&gt; for a project by asking MSBuild
    /// to evaluate it, rather than parsing the .csproj's XML directly. This matters
    /// in practice: many real repos (e.g. eShopOnWeb) declare TargetFramework once
    /// in a shared Directory.Build.props / Directory.Packages.props rather than in
    /// every individual .csproj — naive XML parsing of the .csproj alone would find
    /// nothing. MSBuild's own property evaluation handles that (and any other
    /// import/inheritance rule) correctly, because it's the actual build engine.
    /// </summary>
    public static async Task<string?> ReadTargetFrameworkAsync(string csprojPath, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "dotnet", ["msbuild", csprojPath, "-getProperty:TargetFramework", "-nologo"],
            Path.GetDirectoryName(csprojPath)!, cancellationToken);

        if (!result.Succeeded)
            ThrowIfSdkResolutionFailure(result, csprojPath);

        var value = result.StandardOutput.Trim();
        if (result.Succeeded && value.Length > 0)
            return value;

        // TargetFramework (singular) comes back empty for a genuinely
        // multi-targeted project — MSBuild only populates it once a specific TFM
        // has been selected for an inner build. Read TargetFrameworks (plural) then.
        // Found by testing against real multi-targeted OSS libraries (Dapper, CliWrap,
        // Polly) — the default shape for published NuGet packages; the
        // list is returned and handled rather than refused.
        var multiTarget = await ProcessRunner.RunAsync(
            "dotnet", ["msbuild", csprojPath, "-getProperty:TargetFrameworks", "-nologo"],
            Path.GetDirectoryName(csprojPath)!, cancellationToken);

        var multiTargetValue = multiTarget.StandardOutput.Trim();
        return multiTarget.Succeeded && multiTargetValue.Length > 0 ? multiTargetValue : null;
    }

    /// <summary>
    /// Whether a multi-targeted project's &lt;TargetFrameworks&gt; can be rewritten for a
    /// move off <paramref name="current"/>: the declaring file (the project, or a shared
    /// props file up to the repo root) must spell an entry on that version out
    /// literally. A list built from a property Eolup doesn't follow
    /// (`$(LibraryTargets)`) can't be rewritten safely — checked before anything is
    /// touched, so such a repo is refused cleanly instead of half-migrated.
    /// </summary>
    public static bool CanRetargetFrameworks(string csprojPath, Version current)
    {
        var declarationFile = FindTargetFrameworkDeclarationFile(csprojPath, "<TargetFrameworks");
        return declarationFile is not null &&
               TargetFrameworksPattern().Matches(File.ReadAllText(declarationFile))
                   .Any(m => VersionPlanning.Tokens(m.Groups["value"].Value).Any(t => VersionPlanning.TryParse(t) == current));
    }

    /// <summary>
    /// Whether the declaration behind a project this run moves can be rewritten — checked before
    /// anything is touched, so a repo that can't be migrated is refused cleanly instead of half-migrated.
    /// A project with several entries is a &lt;TargetFrameworks&gt; list and needs a literal entry on
    /// <paramref name="current"/> (see <see cref="CanRetargetFrameworks"/>). A project with ONE entry can be
    /// spelled either way: &lt;TargetFramework&gt;net8.0&lt;/TargetFramework&gt;, or the plural element with a
    /// single entry (Prowlarr, and many projects in MonoGame and workflow-core, do exactly that), so it
    /// follows whichever the declaring file spells. A framework set through an import, or built from a
    /// property, is neither, and can't be rewritten safely.
    /// </summary>
    public static bool CanRewriteTargetFramework(VersionPlanning.ProjectBump bump, Version current) =>
        DeclaresSingularTargetFramework(bump) || CanRetargetFrameworks(bump.File, current);

    /// <summary>
    /// Rewrites the declaration <see cref="CanRewriteTargetFramework"/> found, keeping the form it was
    /// written in (a one-entry plural list stays a plural list), and returns the file that holds it.
    /// </summary>
    public static string RewriteTargetFramework(VersionPlanning.ProjectBump bump, Version current, string targetTfm) =>
        DeclaresSingularTargetFramework(bump)
            ? WriteTargetFramework(bump.File, bump.NewTfm)
            : WriteTargetFrameworks(bump.File, current, targetTfm);

    /// <summary>
    /// Only a project that evaluates to one entry can be a singular declaration: several entries are a list by
    /// definition. When the singular element is spelled out anywhere that applies (the project, or a shared
    /// props file above it), MSBuild builds a single target and ignores a plural element, so it is the one to edit.
    /// </summary>
    private static bool DeclaresSingularTargetFramework(VersionPlanning.ProjectBump bump) =>
        !bump.MultiTarget && FindTargetFrameworkDeclarationFile(bump.File) is not null;

    /// <summary>
    /// Rewrites every &lt;TargetFrameworks&gt; element (conditional ones included) in the
    /// file that declares a multi-targeted project's list, moving only the entries on
    /// <paramref name="current"/> — see <see cref="VersionPlanning.RetargetList"/>. Same
    /// surgical, BOM-preserving, idempotent text edit as <see cref="WriteTargetFramework"/>.
    /// Returns the file that declares the list.
    /// </summary>
    public static string WriteTargetFrameworks(string csprojPath, Version current, string targetTfm)
    {
        var declarationFile = FindTargetFrameworkDeclarationFile(csprojPath, "<TargetFrameworks")
            ?? throw new InvalidOperationException(
                $"No <TargetFrameworks> declaration found for '{csprojPath}' " +
                "(checked the project file itself, and Directory.Build.props / Directory.Packages.props up to the repo root).");

        var hasBom = File.ReadAllBytes(declarationFile) is [0xEF, 0xBB, 0xBF, ..];
        var content = File.ReadAllText(declarationFile);

        var updated = TargetFrameworksPattern().Replace(content, m =>
        {
            var value = m.Groups["value"];
            var retargeted = VersionPlanning.RetargetList(value.Value, current, targetTfm);
            return m.Value[..(value.Index - m.Index)] + retargeted + m.Value[(value.Index - m.Index + value.Length)..];
        });

        // Unchanged is fine: a shared props file already moved by an earlier project this run.
        if (updated != content)
            File.WriteAllText(declarationFile, updated, new UTF8Encoding(hasBom));

        return declarationFile;
    }

    /// <summary>
    /// Distinguishes "the SDK version pinned in this repo's global.json isn't
    /// installed" from any other MSBuild failure — found by testing against
    /// Polly, which pins an exact SDK patch version that wasn't available
    /// locally, causing `dotnet msbuild` to fail before evaluating *any*
    /// property. Without this check, that failure looked identical to "no
    /// TargetFramework declared", hiding an actionable, purely environmental
    /// cause behind a message that implies a problem with the project itself.
    /// </summary>
    private static void ThrowIfSdkResolutionFailure(ProcessResult result, string csprojPath)
    {
        var combined = result.StandardOutput + result.StandardError;
        if (combined.Contains("Requested SDK version", StringComparison.OrdinalIgnoreCase))
        {
            throw new EolupUserException(
                $"Could not evaluate '{csprojPath}' — the .NET SDK version pinned in this repo's global.json isn't " +
                $"installed on this machine. Install the required SDK version or run Eolup somewhere that has " +
                $"it. Details:\n{combined.Trim()}");
        }
    }

    /// <summary>
    /// Rewrites the effective &lt;TargetFramework&gt; declaration to the given TFM
    /// (e.g. "net10.0"), wherever it actually lives — the project's own .csproj, or
    /// a Directory.Build.props / Directory.Packages.props found by walking up from
    /// the project towards the repo root (bounded by the first ".git" directory
    /// found, so this never wanders outside the repo being scanned).
    ///
    /// Uses a surgical text replacement rather than an XML load/save round-trip —
    /// the latter reformats the entire file (strips blank lines, adds an XML
    /// declaration, drops the trailing newline, strips a BOM), which produces a
    /// noisy diff that undermines the whole point of a "trivial to review" PR.
    ///
    /// Idempotent: if the target file already declares the desired TFM (e.g.
    /// because an earlier project in the same remediation run already rewrote a
    /// props file shared by several projects), this is a no-op rather than an
    /// error. Returns the path of the file that declares the TFM (whether or not it
    /// needed changing), so the caller knows exactly which files belong in the commit.
    /// </summary>
    public static string WriteTargetFramework(string csprojPath, string newTfm)
    {
        var declarationFile = FindTargetFrameworkDeclarationFile(csprojPath)
            ?? throw new InvalidOperationException(
                $"No <TargetFramework> declaration found for '{csprojPath}' " +
                "(checked the project file itself, and Directory.Build.props / Directory.Packages.props up to the repo root).");

        var hasBom = File.ReadAllBytes(declarationFile) is [0xEF, 0xBB, 0xBF, ..];
        var content = File.ReadAllText(declarationFile);

        if (content.Contains($"<TargetFramework>{newTfm}</TargetFramework>", StringComparison.Ordinal))
            return declarationFile; // already updated — e.g. a shared props file bumped by an earlier project this run

        var updated = TargetFrameworkPattern().Replace(content, $"<TargetFramework>{newTfm}</TargetFramework>", 1);
        if (updated == content)
            throw new InvalidOperationException($"No <TargetFramework> element found in '{declarationFile}'.");

        // File.WriteAllText defaults to UTF-8 without a BOM, which would silently
        // strip a BOM the original file had — another source of pointless diff
        // noise. Preserve whatever the file started with.
        File.WriteAllText(declarationFile, updated, new UTF8Encoding(hasBom));
        return declarationFile;
    }

    private static string? FindTargetFrameworkDeclarationFile(string csprojPath, string marker = "<TargetFramework>")
    {
        if (File.ReadAllText(csprojPath).Contains(marker, StringComparison.Ordinal))
            return csprojPath;

        var dir = new DirectoryInfo(Path.GetDirectoryName(csprojPath)!);
        while (dir is not null)
        {
            foreach (var candidate in SharedPropsFileNames)
            {
                var path = Path.Combine(dir.FullName, candidate);
                if (File.Exists(path) && File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
                    return path;
            }

            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                break; // repo root — don't climb outside the repo being scanned

            dir = dir.Parent;
        }

        return null;
    }

    private static readonly string[] SharedPropsFileNames = ["Directory.Build.props", "Directory.Packages.props"];

    [GeneratedRegex(@"<TargetFramework>[^<]+</TargetFramework>")]
    private static partial Regex TargetFrameworkPattern();

    // Attributes allowed: conditional lists (`Condition="..."`) are common in MAUI-style projects.
    [GeneratedRegex(@"<TargetFrameworks(?:\s[^>]*)?>(?<value>[^<]*)</TargetFrameworks>")]
    private static partial Regex TargetFrameworksPattern();

    /// <summary>Converts a bare cycle string like "10" or "8.0" (as reported by endoflife.date) into a TFM like "net10.0".</summary>
    public static string CycleToTfm(string cycle)
    {
        // An explicit `target: net10.0` in .eolup.yml passes through target
        // resolution unchanged; without this it became "netnet10.0.0".
        if (cycle.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return cycle;

        var normalized = cycle.Contains('.') ? cycle : $"{cycle}.0";
        return $"net{normalized}";
    }

    /// <summary>
    /// Resolves an explicit path to pass to `dotnet build`/`dotnet test` instead of
    /// just the repo directory. Plain `dotnet build &lt;directory&gt;` fails with
    /// MSB1011 ("more than one project or solution file") whenever a repo keeps more
    /// than one .sln/project file at its root — a real, fairly common pattern (e.g.
    /// eShopOnWeb ships a main .sln alongside a docker-compose.dcproj). See
    /// MANUAL_TEST_PASS.md, Pass #1 finding 2.
    ///
    /// Resolution order: an explicit `solution:` in .eolup.yml always wins;
    /// otherwise, if exactly one .sln/.slnx exists at the root, use it (this alone
    /// fixes the common "one real solution plus one unrelated project file" case,
    /// since we're no longer asking `dotnet build` to auto-discover among *all*
    /// project-like files itself); otherwise, if exactly one .csproj exists at the
    /// root, use that. If none of that disambiguates — e.g. genuinely multiple
    /// competing .sln files — fall back to the directory itself, so the existing
    /// MSB1011 error path still fires rather than silently guessing which solution
    /// is "the right one".
    /// </summary>
    public static string ResolveBuildTarget(string projectPath, string? configuredSolution)
    {
        if (configuredSolution is not null)
        {
            var explicitPath = Path.GetFullPath(Path.Combine(projectPath, configuredSolution));
            return File.Exists(explicitPath)
                ? explicitPath
                : throw new EolupUserException(
                    $"Configured solution '{configuredSolution}' not found under '{projectPath}'.");
        }

        var solutionFiles = Directory.GetFiles(projectPath, "*.sln")
            .Concat(Directory.GetFiles(projectPath, "*.slnx"))
            .ToList();
        if (solutionFiles.Count == 1)
            return solutionFiles[0];

        if (solutionFiles.Count == 0)
        {
            var rootProjects = Directory.GetFiles(projectPath, "*.csproj");
            if (rootProjects.Length == 1)
                return rootProjects[0];

            // Nothing buildable at the root at all: a single solution kept in a
            // subfolder (src/App.sln is a common layout) is unambiguous too. Without
            // this, `dotnet build <dir>` fails with MSB1003.
            if (rootProjects.Length == 0)
            {
                var nested = FindSolutionFiles(projectPath);
                if (nested.Count == 1)
                    return nested[0];
            }
        }

        return projectPath;
    }

    /// <summary>Every .sln/.slnx under the directory, skipping build output.</summary>
    public static IReadOnlyList<string> FindSolutionFiles(string projectPath) =>
        Directory.GetFiles(projectPath, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(projectPath, "*.slnx", SearchOption.AllDirectories))
            .Where(p => !Path.GetRelativePath(projectPath, p).Replace('\\', '/').Split('/')
                .Any(segment => segment is "bin" or "obj" or ".git"))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The message for `dotnet build` reporting MSB1003: nothing buildable at the root,
    /// and ResolveBuildTarget couldn't pick a single solution below it. Found via adnc
    /// (six solutions in subfolders), where this used to be reported as "does not build
    /// in its current state" — wrong, and no help in fixing it.
    /// </summary>
    public static string DescribeNoBuildTargetAtRoot(string projectPath)
    {
        var solutions = FindSolutionFiles(projectPath)
            .Select(p => Path.GetRelativePath(projectPath, p).Replace('\\', '/'))
            .ToList();

        if (solutions.Count == 0)
            return $"There's no solution or project file at the root of '{projectPath}', and no solution file below it " +
                   "either, so Eolup can't tell what to build. Set `solution: <path to a .csproj>` in .eolup.yml.";

        var listed = string.Join(", ", solutions.Take(5)) + (solutions.Count > 5 ? $" and {solutions.Count - 5} more" : "");
        return $"There's no solution or project file at the root of '{projectPath}', and {solutions.Count} solution files " +
               $"below it ({listed}), so Eolup can't tell which one to build. Set `solution: <one of those paths>` " +
               "in .eolup.yml.";
    }
}
