using System.Net;
using System.Text.Json;

namespace Eolup.Providers.DotNet;

/// <summary>Where the published versions of a NuGet package come from.</summary>
internal interface IPackageVersionSource
{
    /// <summary>Every published version of the package, in any order. Empty when the package is unknown.</summary>
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// nuget.org's flat-container index (https://api.nuget.org/v3-flatcontainer/{id}/index.json).
/// Deliberately nuget.org only for now: private feeds and nuget.config sources aren't consulted,
/// so a package that only exists on a private feed simply isn't resolved (and isn't bumped).
/// </summary>
internal sealed class NuGetOrgVersionSource : IPackageVersionSource
{
    private readonly HttpClient _http;

    public NuGetOrgVersionSource(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { BaseAddress = new Uri("https://api.nuget.org/v3-flatcontainer/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        // The flat container is keyed by the lower-cased id.
        using var response = await _http.GetAsync($"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array
            ? versions.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList()
            : [];
    }
}

/// <summary>
/// Picks the version a framework-aligned package should move to: the newest stable release on the
/// target framework's major. Always a concrete version, never a floating "10.*" — a pinned version
/// is what makes a PR reproducible and reviewable.
/// </summary>
internal static class PackageVersionResolver
{
    /// <summary>
    /// The newest stable version of <paramref name="packageId"/> whose major is <paramref name="major"/>, or null
    /// when there is none or the lookup failed. A failed lookup is never an error: bumping is a best-effort
    /// retry, and "couldn't resolve" just means that package is left as it is.
    /// </summary>
    public static async Task<string?> LatestStableOnMajorAsync(
        IPackageVersionSource source, string packageId, int major, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> versions;
        try
        {
            versions = await source.GetVersionsAsync(packageId, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return null;
        }

        string? best = null;
        Version? bestParsed = null;
        foreach (var candidate in versions)
        {
            if (!TryParseStable(candidate, out var parsed) || parsed.Major != major)
                continue;
            if (bestParsed is null || parsed > bestParsed)
                (best, bestParsed) = (candidate.Trim(), parsed);
        }

        return best;
    }

    /// <summary>A release version ("10.0.3", "10.0.3.1", "10.0.3+build"); false for prereleases ("10.0.0-rc.1") and anything unparseable.</summary>
    private static bool TryParseStable(string value, out Version version)
    {
        var core = value.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0)
            core = core[..plus];

        version = new Version(0, 0);
        if (core.Contains('-'))
            return false;
        if (!core.Contains('.'))
            core += ".0"; // System.Version needs at least major.minor

        if (!Version.TryParse(core, out var parsed))
            return false;

        version = parsed;
        return true;
    }
}
