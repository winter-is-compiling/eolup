using System.Text.Json;

namespace Eolup.Providers.DotNet;

/// <summary>
/// Whether a repo runs its tests on Microsoft.Testing.Platform (MTP) instead of VSTest. The two take different
/// command-line options: `--logger trx` and `--collect` are VSTest's, and under MTP every test host rejects them
/// and exits with code 5, so a suite that passes fine reads as failing.
/// </summary>
internal static class TestingPlatform
{
    /// <summary>MTP's exit code for "invalid command-line arguments" — what VSTest-only options cause there.</summary>
    public const int InvalidArgumentsExitCode = 5;

    private const string RunnerName = "Microsoft.Testing.Platform";

    /// <summary>
    /// True when the global.json that applies to <paramref name="directory"/> (the nearest one, found the way the
    /// SDK finds it: this folder, then each parent) selects MTP with <c>"test": { "runner": "Microsoft.Testing.Platform" }</c>.
    /// </summary>
    public static bool IsSelectedByGlobalJson(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "global.json");
            if (File.Exists(file))
                return RunnerOf(file) is { } runner && string.Equals(runner, RunnerName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? RunnerOf(string globalJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(globalJson),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                    ? runner.GetString()
                    : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
