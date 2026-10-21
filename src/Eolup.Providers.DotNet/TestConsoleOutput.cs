using System.Text.RegularExpressions;

namespace Eolup.Providers.DotNet;

/// <summary>
/// What the console output of `dotnet test` says about test projects that never reported a result. The TRX files
/// cannot say: a project whose test host crashed writes none (or an empty one), so it is simply absent there.
/// The console names every assembly it starts ("Test run for X.dll") and prints a summary line for each that
/// finished ("Passed! - ... - X.dll"), so a started assembly without a summary is one that could not run.
/// </summary>
internal static partial class TestConsoleOutput
{
    /// <summary>Console lines that explain why a test host stopped, most specific first.</summary>
    private static readonly string[] CrashMarkers =
    [
        "Test host process crashed",
        "The active test run was aborted",
        "You must install or update .NET to run this application",
        "Test Run Aborted",
    ];

    /// <summary>
    /// The names (no folder, no extension) of the test assemblies that started but printed no summary, sorted.
    /// Empty when the output names none, which is also what a non-English or older SDK's output looks like:
    /// the caller then simply has no names to give.
    /// </summary>
    public static IReadOnlyList<string> AssembliesThatDidNotReport(string output)
    {
        var started = StartedRun().Matches(output).Select(m => AssemblyName(m.Groups["path"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finished = Summary().Matches(output).Select(m => AssemblyName(m.Groups["path"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. started.Except(finished, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The first console line that says a test host crashed or a run was aborted, or null.</summary>
    public static string? CrashText(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var marker in CrashMarkers)
        {
            var line = lines.FirstOrDefault(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
                return line;
        }

        return null;
    }

    private static string AssemblyName(string path)
    {
        // Both separators, whatever the OS the output came from.
        var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? file[..^4]
            : file;
    }

    // "Test run for C:\repo\tests\X\bin\Debug\net8.0\X.dll (.NETCoreApp,Version=v8.0)"
    [GeneratedRegex(@"^\s*Test run for (?<path>.+?\.(?:dll|exe))\s*\(", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StartedRun();

    // "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 293 ms - X.dll (net8.0)"
    [GeneratedRegex(@"^\s*(?:Passed|Failed)!\s+-.*?-\s+(?<path>\S+?\.(?:dll|exe))\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Summary();
}
