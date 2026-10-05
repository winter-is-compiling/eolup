using System.Text;

namespace Eolup.Providers.DotNet.Tests;

/// <summary>A throwaway directory tree for tests that need real files on disk.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eolup-provider-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Marks this directory as a repo root, so anything that walks upwards looking
    /// for a shared Directory.Build.props stops here instead of wandering into
    /// whatever happens to sit above the OS temp directory.
    /// </summary>
    public TempDir AsRepoRoot()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        return this;
    }

    public string Write(string relativePath, string content, bool withBom = false)
    {
        // GetFullPath normalises separators, so returned paths compare equal to
        // what Directory.GetFiles reports even when relativePath used forward slashes.
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(withBom));
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort — never fail a test over temp cleanup */ }
    }
}
