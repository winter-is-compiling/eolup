namespace Rollforward.Core.Models;

/// <summary>
/// What a language provider found out about a project's current version.
/// <see cref="Version"/> is the version the next upgrade step starts from; for a
/// repo whose projects are on different versions that is the oldest one, so the
/// repo converges one hop per run. <see cref="Notes"/> are things worth telling the
/// user that don't change the answer (projects deliberately left alone, projects
/// already ahead), shown by `scan`.
/// </summary>
public sealed record VersionDetection(string Version, IReadOnlyList<string> Notes)
{
    public static VersionDetection Of(string version) => new(version, []);
}
