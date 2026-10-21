namespace Eolup.Providers.DotNet;

/// <summary>
/// What a caller can set on the .NET provider. Anything left unset falls back to the repo's
/// .eolup.yml, then to the built-in default.
/// </summary>
public sealed record DotNetProviderOptions
{
    /// <summary>
    /// Opt in to the package bump: when the framework bump alone breaks the build or tests, move the
    /// stale framework-aligned packages to the target major and re-run. `bumpPackages: true` in
    /// .eolup.yml turns it on too. Off by default.
    /// </summary>
    public bool BumpPackages { get; init; }

    /// <summary>
    /// How long a restore or build may run before it is stopped (`--build-timeout`). Unset: `buildTimeoutMinutes`
    /// in .eolup.yml, then 30 minutes.
    /// </summary>
    public TimeSpan? BuildTimeout { get; init; }

    /// <summary>
    /// How long the test run may take before it is stopped (`--test-timeout`). Unset: `testTimeoutMinutes`
    /// in .eolup.yml, then 60 minutes.
    /// </summary>
    public TimeSpan? TestTimeout { get; init; }
}
