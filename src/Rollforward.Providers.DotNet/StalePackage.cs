namespace Rollforward.Providers.DotNet;

/// <summary>
/// A framework-aligned package still on the old framework's major version, and where it is
/// declared. <paramref name="ElementIndex"/> is the position of its PackageReference/PackageVersion
/// element among all such elements in <paramref name="File"/>, in document order (comments don't
/// count), which is how <see cref="PackageVersionEditor"/> finds the same element in the raw text.
/// </summary>
internal sealed record StalePackage(string Id, string Version, string File, string RelativePath, int ElementIndex)
{
    /// <summary>"Id Version (relative/path)" — the form shown in verdict reasons.</summary>
    public string Describe() => $"{Id} {Version} ({RelativePath})";
}
