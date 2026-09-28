using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Rollforward.Fixtures.Tests;

/// <summary>
/// Copies a fixture from /fixtures into a fresh temp directory and git-inits it
/// there. Remediation runs `git checkout -b`, so it needs to operate on a real,
/// freshly-initialized working copy — not a fixture with its own committed
/// .git (which would create a nested-repo mess in Rollforward's own history).
/// This mirrors what actually happens in real usage: Rollforward always runs
/// against a freshly checked-out copy of someone else's repo, never a
/// long-lived local clone it owns.
///
/// Each copy ends up holding full bin/obj output for every target framework the
/// engine builds, so callers hold the returned <see cref="FixtureCopy"/> in a
/// `using` and the directory is deleted when the test ends.
/// </summary>
internal static class FixtureHarness
{
    public static FixtureCopy CopyToTemp(string fixtureName, [CallerFilePath] string sourceFilePath = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        var fixtureSource = Path.Combine(repoRoot, "fixtures", fixtureName);

        if (!Directory.Exists(fixtureSource))
            throw new DirectoryNotFoundException($"Fixture '{fixtureName}' not found at '{fixtureSource}'.");

        var copy = new FixtureCopy(Path.Combine(Path.GetTempPath(), "rollforward-fixture-" + Guid.NewGuid().ToString("N")));
        try
        {
            CopyDirectory(fixtureSource, copy.Path);

            RunGit(copy.Path, "init -q");
            RunGit(copy.Path, "config user.email test@rollforward.local");
            RunGit(copy.Path, "config user.name Rollforward-Tests");
            RunGit(copy.Path, "add -A");
            RunGit(copy.Path, "commit -q -m initial");
        }
        catch
        {
            copy.Dispose();
            throw;
        }

        return copy;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj") continue;
            CopyDirectory(dir, Path.Combine(dest, name));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
    }
}

/// <summary>
/// A fixture's temp working copy; disposing it deletes the directory. Deletion
/// is best effort: a test must not fail just because it couldn't clean up.
/// </summary>
internal sealed class FixtureCopy(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        if (TryDelete()) return;

        // On Windows the usual culprits are git's object files, which git creates
        // read-only and Directory.Delete refuses to remove, and build output under
        // bin/ still briefly locked by a testhost that is exiting or by antivirus.
        Thread.Sleep(500);
        ClearReadOnlyAttributes();
        TryDelete();
    }

    private bool TryDelete()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ClearReadOnlyAttributes()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
