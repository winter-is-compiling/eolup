using Eolup.Core.Eol;
using Eolup.Core.Models;
using Eolup.Core.Providers;
using Xunit;

namespace Eolup.Core.Tests;

/// <summary>
/// Regression test for a real bug: EolupEngine used to hand callers'
/// projectPath straight to the provider unmodified. A relative path resolves
/// against whatever the current process's working directory happens to be at
/// the moment each downstream file/process operation runs — not reliably
/// stable across a whole call chain — which broke the exact command the GitHub
/// Action adapter runs by default (a relative path). Caught by testing the
/// adapter, not by any of the existing fixture tests, since FixtureHarness
/// always hands the engine an already-absolute temp path.
/// </summary>
public class EolupEngineTests
{
    private sealed class RecordingProvider : ILanguageProvider
    {
        public string? ReceivedPath;
        public string ProductId => "dotnet";

        public string FormatVersion(string cycle) => $"formatted:{cycle}";

        public string DetectedVersion = "net8.0";
        public IReadOnlyList<string> DetectedNotes = [];

        public Task<VersionDetection> DetectVersionAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            ReceivedPath = projectPath;
            return Task.FromResult(new VersionDetection(DetectedVersion, DetectedNotes));
        }

        public Task<RemediationOutcome> RemediateAsync(string projectPath, string targetVersion, CancellationToken cancellationToken = default)
        {
            ReceivedPath = projectPath;
            return Task.FromResult(new RemediationOutcome(true, true, true, [], "branch"));
        }
    }

    private sealed class StubEolClient : IEolClient
    {
        public Task<IReadOnlyList<EolInfo>> GetCyclesAsync(string product, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EolInfo>>([new EolInfo("8.0", null, null, IsLts: true, IsLatest: true)]);
    }

    [Fact]
    public async Task ScanAsync_NormalizesRelativePathToAbsolute_BeforeCallingProvider()
    {
        var provider = new RecordingProvider();
        var engine = new EolupEngine(new StubEolClient(), provider);

        await engine.ScanAsync(".");

        Assert.True(Path.IsPathRooted(provider.ReceivedPath));
        Assert.Equal(Path.GetFullPath("."), provider.ReceivedPath);
    }

    [Fact]
    public async Task ScanAsync_TargetDisplayComesFromTheProvider_WhileTargetVersionStaysTheRawCycle()
    {
        // Found via Pass #3: scan printed "Current: net9.0" next to "Target: 10".
        // The raw cycle must stay untouched (remediation works on it); only the
        // display value is rendered in the ecosystem's own notation.
        var engine = new EolupEngine(new StubEolClient(), new RecordingProvider());

        var result = await engine.ScanAsync(".");

        Assert.Equal("formatted:" + result.TargetVersion, result.TargetDisplay);
        Assert.DoesNotContain("formatted:", result.TargetVersion);
    }


    private static readonly EolInfo[] DotNetCycles =
    [
        new("10.0", null, null, IsLts: true, IsLatest: true),
        new("9.0", null, null, IsLts: false, IsLatest: false),
        new("8.0", null, null, IsLts: true, IsLatest: false),
        new("7.0", null, null, IsLts: false, IsLatest: false),
        new("6.0", null, null, IsLts: true, IsLatest: false),
    ];

    private sealed class CyclesEolClient(params EolInfo[] cycles) : IEolClient
    {
        public Task<IReadOnlyList<EolInfo>> GetCyclesAsync(string product, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EolInfo>>(cycles);
    }

    [Fact]
    public async Task ScanAsync_TakesOnlyTheFirstHop_ButReportsTheWholeUpgradePath()
    {
        // The agreed model: one hop per run (small PRs, breaking changes met one at
        // a time), while scan still shows where the journey ends.
        var provider = new RecordingProvider { DetectedVersion = "net6.0" };
        var engine = new EolupEngine(new CyclesEolClient(DotNetCycles), provider);

        var result = await engine.ScanAsync(".");

        Assert.Equal("8.0", result.TargetVersion);
        Assert.Equal(["formatted:8.0", "formatted:10.0"], result.UpgradePath);
    }

    [Fact]
    public async Task ScanAsync_SurfacesTheProvidersNotes()
    {
        var provider = new RecordingProvider { DetectedNotes = ["Domain (netstandard2.1) is left alone."] };
        var engine = new EolupEngine(new StubEolClient(), provider);

        var result = await engine.ScanAsync(".");

        Assert.Equal(["Domain (netstandard2.1) is left alone."], result.Notes);
    }
    [Fact]
    public async Task RemediateAsync_NormalizesRelativePathToAbsolute_BeforeCallingProvider()
    {
        var provider = new RecordingProvider();
        var engine = new EolupEngine(new StubEolClient(), provider);

        await engine.RemediateAsync(".");

        Assert.True(Path.IsPathRooted(provider.ReceivedPath));
        Assert.Equal(Path.GetFullPath("."), provider.ReceivedPath);
    }

    // ------------------------------------------------------------------ chaining

    /// <summary>A provider whose version really moves: each hop upgrades it to the target it was asked for.</summary>
    private sealed class SteppingProvider(string start, params string[] failingTargets) : ILanguageProvider
    {
        public string Current = start;
        public readonly List<string> HopsAskedFor = [];
        public string ProductId => "dotnet";
        public string FormatVersion(string cycle) => "net" + cycle;

        public Task<VersionDetection> DetectVersionAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(VersionDetection.Of(Current));

        public Task<RemediationOutcome> RemediateAsync(string projectPath, string targetVersion, CancellationToken cancellationToken = default)
        {
            HopsAskedFor.Add(targetVersion);
            Current = "net" + targetVersion;
            var passes = !failingTargets.Contains(targetVersion);
            return Task.FromResult(new RemediationOutcome(true, true, passes, [], $"eolup/upgrade-to-{targetVersion}"));
        }
    }

    private static readonly EolInfo[] CyclesFrom5 = [.. DotNetCycles, new("5.0", null, null, IsLts: false, IsLatest: false)];

    [Fact]
    public async Task Chain_WalksEveryHop_WhileEachIsHighConfidence()
    {
        var provider = new SteppingProvider("net6.0");
        var engine = new EolupEngine(new CyclesEolClient(DotNetCycles), provider);

        var run = await engine.RemediateChainAsync(".");

        Assert.Equal(["8.0", "10.0"], provider.HopsAskedFor);
        Assert.Equal([("net6.0", "net8.0"), ("net8.0", "net10.0")], run.Hops.Select(h => (h.From, h.To)));
        Assert.All(run.Hops, h => Assert.Equal(ConfidenceVerdict.HighConfidence, h.Result.Verdict));
        Assert.Same(run.Final, run.LastHighConfidence);
    }

    [Fact]
    public async Task Chain_StopsAtTheFirstHopThatIsNotHighConfidence_AndNeverBuildsOnIt()
    {
        var provider = new SteppingProvider("net5.0", failingTargets: "8.0");
        var engine = new EolupEngine(new CyclesEolClient(CyclesFrom5), provider);

        var run = await engine.RemediateChainAsync(".");

        Assert.Equal(["6.0", "8.0"], provider.HopsAskedFor); // 10.0 never attempted
        Assert.Equal(ConfidenceVerdict.NeedsReview, run.Final.Result.Verdict);
        Assert.Equal("eolup/upgrade-to-6.0", run.LastHighConfidence!.Result.BranchName);
    }

    [Fact]
    public async Task Chain_WhoseFirstHopFails_HasNothingPublishable()
    {
        var provider = new SteppingProvider("net6.0", failingTargets: "8.0");
        var engine = new EolupEngine(new CyclesEolClient(DotNetCycles), provider);

        var run = await engine.RemediateChainAsync(".");

        Assert.Single(run.Hops);
        Assert.Null(run.LastHighConfidence);
    }

    [Fact]
    public async Task RemediateAsync_IsStillExactlyOneHop_TheAgreedDefault()
    {
        var provider = new SteppingProvider("net6.0");
        var engine = new EolupEngine(new CyclesEolClient(DotNetCycles), provider);

        var result = await engine.RemediateAsync(".");

        Assert.Equal(["8.0"], provider.HopsAskedFor);
        Assert.Equal(ConfidenceVerdict.HighConfidence, result.Verdict);
    }

    [Fact]
    public async Task Chain_WithAnExplicitTarget_StopsOnceItIsReached()
    {
        // Regression guard: the path for an explicit target used to be that one hop
        // even when already there, which would make a chain retry it forever (to the cap).
        var dir = Directory.CreateTempSubdirectory("eolup-chain-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".eolup.yml"), "target: \"8.0\"\n");
            var provider = new SteppingProvider("net6.0");
            var engine = new EolupEngine(new CyclesEolClient(DotNetCycles), provider);

            var run = await engine.RemediateChainAsync(dir);

            Assert.Equal(["8.0"], provider.HopsAskedFor);
            Assert.Single(run.Hops);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
