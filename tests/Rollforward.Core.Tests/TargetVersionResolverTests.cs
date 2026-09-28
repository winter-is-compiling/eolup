using Rollforward.Core.Eol;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

public class TargetVersionResolverTests
{
    private static readonly List<EolInfo> Cycles =
    [
        new("8.0", null, null, IsLts: true, IsLatest: false),
        new("9.0", null, null, IsLts: false, IsLatest: false),
        new("10", null, null, IsLts: true, IsLatest: true),
    ];

    [Fact]
    public void ExplicitVersion_PassesThroughUnchanged()
    {
        // Teams may deliberately want a version other than "latest" (e.g. 19 when
        // 22 already exists) — target is never assumed. See ARCHITECTURE.md.
        var result = TargetVersionResolver.Resolve(Cycles, "net8.0", "19");
        Assert.Equal("19", result);
    }

    [Fact]
    public void NextMajor_PicksTheImmediateNextCycle_EvenIfNotLts()
    {
        var result = TargetVersionResolver.Resolve(Cycles, "net8.0", TargetVersionResolver.NextMajor);
        Assert.Equal("9.0", result);
    }

    [Fact]
    public void NextLts_SkipsNonLtsCycles()
    {
        // From 8.0, the next major (9.0) isn't LTS — next-lts should skip it and land on 10.
        var result = TargetVersionResolver.Resolve(Cycles, "net8.0", TargetVersionResolver.NextLts);
        Assert.Equal("10", result);
    }

    [Fact]
    public void NextLts_WithNoLtsAhead_FallsBackToLatestKnownLts()
    {
        var result = TargetVersionResolver.Resolve(Cycles, "net10.0", TargetVersionResolver.NextLts);
        Assert.Equal("10", result);
    }

    // ------------------------------------------------------------------ Path

    private static readonly List<EolInfo> DotNet =
    [
        new("10.0", null, null, IsLts: true, IsLatest: true),
        new("9.0", null, null, IsLts: false, IsLatest: false),
        new("8.0", null, null, IsLts: true, IsLatest: false),
        new("7.0", null, null, IsLts: false, IsLatest: false),
        new("6.0", null, null, IsLts: true, IsLatest: false),
        new("5.0", null, null, IsLts: false, IsLatest: false),
        new("3.1", null, null, IsLts: true, IsLatest: false),
    ];

    [Theory]
    [InlineData("netcoreapp3.1", new[] { "6.0", "8.0", "10.0" })] // 3.1 -> 6 -> 8 -> 10, never straight to 10
    [InlineData("net5.0", new[] { "6.0", "8.0", "10.0" })]
    [InlineData("net8.0", new[] { "10.0" })]
    [InlineData("net9.0", new[] { "10.0" })]
    [InlineData("net10.0", new string[0])] // already at the destination
    public void Path_NextLts_WalksEveryLtsHop(string current, string[] expected) =>
        Assert.Equal(expected, TargetVersionResolver.Path(DotNet, current, TargetVersionResolver.NextLts));

    [Fact]
    public void Path_NextMajor_WalksEveryMajor() =>
        Assert.Equal(["9.0", "10.0"], TargetVersionResolver.Path(DotNet, "net8.0", TargetVersionResolver.NextMajor));

    [Fact]
    public void Path_ExplicitTarget_IsASingleHop() =>
        Assert.Equal(["net10.0"], TargetVersionResolver.Path(DotNet, "net6.0", "net10.0"));

    [Fact]
    public void Path_FirstHop_AlwaysMatchesWhatResolvePicks()
    {
        // The single hop actually performed must be the first step shown.
        foreach (var current in new[] { "netcoreapp3.1", "net5.0", "net6.0", "net8.0", "net9.0" })
        {
            var first = TargetVersionResolver.Path(DotNet, current, TargetVersionResolver.NextLts)[0];
            Assert.Equal(TargetVersionResolver.Resolve(DotNet, current, TargetVersionResolver.NextLts), first);
        }
    }

    [Theory]
    [InlineData("net10.0", "10")]
    [InlineData("net10.0", "10.0")]
    [InlineData("net10.0", "net10.0")]
    [InlineData("net10.0", "8.0")] // already beyond
    public void Path_ExplicitTargetAlreadyReached_IsEmpty(string current, string target) =>
        Assert.Empty(TargetVersionResolver.Path(DotNet, current, target));
}
