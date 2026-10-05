using Eolup.Core.Config;
using Eolup.Core.Confidence;
using Eolup.Core.Eol;
using Eolup.Core.Models;
using Eolup.Core.Providers;

namespace Eolup.Core;

/// <summary>
/// Ties config loading, EOL lookup, and a language provider together into the
/// two commands the CLI exposes: scan and remediate.
/// </summary>
public sealed class EolupEngine(IEolClient eolClient, ILanguageProvider provider)
{
    public async Task<ScanResult> ScanAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        // Normalized here, at the engine's own public API boundary — not just in
        // the CLI that happens to call it today. A relative path resolves against
        // whatever the current process's working directory happens to be at the
        // moment each downstream file/process operation runs, which isn't
        // reliably stable across a whole call chain (found via the GitHub Action
        // adapter, which passes a relative path by default). Any future caller
        // of this engine in-process — a hosted portal, say — gets this for free.
        projectPath = Path.GetFullPath(projectPath);

        var config = EolupConfigLoader.Load(projectPath);
        var detection = await provider.DetectVersionAsync(projectPath, cancellationToken);
        var currentVersion = detection.Version;
        var cycles = await eolClient.GetCyclesAsync(provider.ProductId, cancellationToken);

        var targetVersion = TargetVersionResolver.Resolve(cycles, currentVersion, config.Target);
        var upgradePath = TargetVersionResolver.Path(cycles, currentVersion, config.Target)
            .Select(provider.FormatVersion).ToList();
        var (status, eolDate, daysUntilEol) = EolEvaluator.Evaluate(cycles, currentVersion, DateOnly.FromDateTime(DateTime.UtcNow));

        return new ScanResult(
            projectPath, currentVersion, targetVersion, provider.FormatVersion(targetVersion),
            status, eolDate, daysUntilEol, upgradePath, detection.Notes);
    }

    /// <summary>One hop: the upgrade `scan` would pick next. See <see cref="RemediateChainAsync"/> for several.</summary>
    public async Task<RemediationResult> RemediateAsync(string projectPath, CancellationToken cancellationToken = default) =>
        (await RemediateChainAsync(projectPath, maxHops: 1, cancellationToken)).Final.Result;

    /// <summary>A safety bound on a chain; a real .NET path is at most a handful of hops.</summary>
    public const int MaxChainHops = 10;

    /// <summary>
    /// Upgrades hop by hop: each hop is remediated and scored as usual; if
    /// it is HighConfidence and the upgrade path has further hops, the next one starts
    /// from that hop's branch, so every hop is its own commit. Stops at the first hop
    /// that isn't HighConfidence, when the path is complete, or after
    /// <paramref name="maxHops"/>. With maxHops = 1 this is exactly the one-hop-per-run
    /// default agreed for Eolup — chaining is opt-in.
    /// </summary>
    public async Task<ChainedRemediation> RemediateChainAsync(
        string projectPath, int maxHops = MaxChainHops, CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);

        // Where the user's checkout is before anything happens: a run that fails puts it back here, whichever
        // hop it fails in (each hop leaves the checkout on its own branch, so the provider alone would only go
        // back to the previous hop's).
        var startedOn = await provider.CaptureCheckoutAsync(projectPath, cancellationToken);
        var hops = new List<RemediationHop>();

        try
        {
            while (hops.Count < Math.Max(1, maxHops))
            {
                // Re-scanned every hop: the previous hop's commit is checked out, so
                // detection sees the versions it produced.
                var scan = await ScanAsync(projectPath, cancellationToken);
                if (hops.Count > 0 && scan.UpgradePath.Count == 0)
                    break; // destination reached

                var result = await RemediateHopAsync(projectPath, scan, cancellationToken);
                hops.Add(new RemediationHop(scan.CurrentVersion, scan.TargetDisplay, result));

                if (result.Verdict != ConfidenceVerdict.HighConfidence)
                    break; // never build further on a hop that needs a human
            }
        }
        catch (Exception failure) when (startedOn is not null)
        {
            var problem = await provider.RestoreCheckoutAsync(projectPath, startedOn);
            if (failure is OperationCanceledException)
                throw; // the user stopped it: put back quietly

            // The steps that finished stay on their branches. Say so, because no pull request was opened for them.
            var finished = hops.Select(h => h.Result.BranchName).OfType<string>().ToList();
            if (finished.Count == 0 && problem is null)
                throw; // a single hop: the provider's own message already says what it put back

            var note = finished.Count == 0 ? "" :
                $"The {(finished.Count == 1 ? "step that finished is" : "steps that finished are")} kept on " +
                $"{string.Join(", ", finished.Select(CheckoutRef.Describe))}; no pull request was opened for {(finished.Count == 1 ? "it" : "them")}. ";
            note += problem is null
                ? $"Your checkout is back on {CheckoutRef.Describe(startedOn)}."
                : $"Eolup could not put your checkout back on {CheckoutRef.Describe(startedOn)}: {problem}";

            var what = failure is EolupUserException ? failure.Message : $"Unexpected {failure.GetType().Name}: {failure.Message}";
            throw new EolupUserException($"{what}\n{note.TrimEnd()}", failure);
        }

        return new ChainedRemediation(hops, startedOn);
    }

    private async Task<RemediationResult> RemediateHopAsync(string projectPath, ScanResult scan, CancellationToken cancellationToken)
    {
        var config = EolupConfigLoader.Load(projectPath);
        var outcome = await provider.RemediateAsync(projectPath, scan.TargetVersion, cancellationToken);

        return ConfidenceScorer.Score(
            outcome.BuildSucceeded,
            outcome.TestProjectExists,
            outcome.TestsPassed,
            outcome.ManualActionMarkers,
            outcome.BranchName,
            outcome.Coverage,
            config.MinCoverage,
            outcome.TestComparison,
            outcome.FrameworkAlignedPackages,
            outcome.PackagesBumped,
            outcome.UnhelpfulPackageBumps,
            outcome.Unverifiable);
    }
}
