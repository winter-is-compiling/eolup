using Eolup.Core.Models;

namespace Eolup.Core.Providers;

/// <summary>
/// The one seam between Eolup's language-agnostic orchestration and a
/// specific ecosystem's tooling. .NET is the only implementation in v0; adding
/// another ecosystem later means implementing this interface, not redesigning
/// the core. See ARCHITECTURE.md, "Language providers".
/// </summary>
public interface ILanguageProvider
{
    /// <summary>The endoflife.date product slug this provider's versions are checked against (e.g. "dotnet").</summary>
    string ProductId { get; }

    /// <summary>
    /// The version this project (or repo) is on, plus any notes worth surfacing. When a
    /// repo mixes versions, the answer is the <em>oldest</em> — upgrades go one hop per
    /// run and the repo converges; see ARCHITECTURE.md, "Chained migrations".
    /// </summary>
    Task<VersionDetection> DetectVersionAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a version identifier in this ecosystem's natural notation for display.
    /// Target versions are resolved in endoflife.date cycle naming (".NET 10" is
    /// "10"), while DetectVersionAsync returns whatever the ecosystem itself calls
    /// the current version ("net9.0"). Without this, output showed the two side by
    /// side in different notations. Purely presentational — remediation still
    /// works on the raw cycle.
    /// </summary>
    string FormatVersion(string cycle);

    Task<RemediationOutcome> RemediateAsync(string projectPath, string targetVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the user's checkout stands right now (a branch name, or a commit when it is detached), captured before a
    /// run so a run that fails can put it back. Null when there is nothing to put back. A provider that never touches
    /// the checkout can leave this default.
    /// </summary>
    Task<string?> CaptureCheckoutAsync(string projectPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Puts the checkout back where <see cref="CaptureCheckoutAsync"/> found it. Best effort, and it never throws: the run
    /// has already failed, and this must not hide why. Returns what went wrong when it could not, otherwise null.
    /// </summary>
    Task<string?> RestoreCheckoutAsync(string projectPath, string checkout) =>
        Task.FromResult<string?>(null);
}
