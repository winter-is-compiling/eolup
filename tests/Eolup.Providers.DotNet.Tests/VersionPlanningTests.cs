using Eolup.Core;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class VersionPlanningTests
{
    private static Dictionary<string, string> Projects(params (string Name, string Tfm)[] p) =>
        p.ToDictionary(x => $"/repo/{x.Name}/{x.Name}.csproj", x => x.Tfm);

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("net8.0", 8, 0)]
    [InlineData("net10.0", 10, 0)]
    [InlineData("net5.0", 5, 0)]
    [InlineData("netcoreapp3.1", 3, 1)]
    [InlineData("net8.0-windows", 8, 0)]
    [InlineData("NET8.0", 8, 0)]
    public void TryParse_UnderstandsModernTfms(string tfm, int major, int minor) =>
        Assert.Equal(new Version(major, minor), VersionPlanning.TryParse(tfm));

    [Theory]
    [InlineData("netstandard2.1")]
    [InlineData("net48")]    // .NET Framework — must not be read as "version 48"
    [InlineData("net472")]
    [InlineData("")]
    public void TryParse_ReturnsNullForAnythingItCannotOrder(string tfm) =>
        Assert.Null(VersionPlanning.TryParse(tfm));

    // ------------------------------------------------------------------ detection

    [Fact]
    public void AllProjectsAgree_ThatIsTheVersion_WithNoNotes()
    {
        var result = VersionPlanning.DetectCurrent("/repo", Projects(("Api", "net8.0"), ("Core", "net8.0")), 0);

        Assert.Equal("net8.0", result.Version);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void NetStandardProjects_AreIgnoredForTheVersion_AndNamedInANote()
    {
        // PeakLims / iayti shape: a netstandard shared library among modern projects
        // used to make detection refuse the whole repo.
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("Api", "net9.0"), ("Domain", "netstandard2.1"), ("Core", "net9.0")), 0);

        Assert.Equal("net9.0", result.Version);
        var note = Assert.Single(result.Notes);
        Assert.Contains("Domain (netstandard2.1) is left alone", note);
    }

    [Fact]
    public void ProjectsOnDifferentVersions_TheOldestIsCurrent_AndTheOthersAreNotedAsAhead()
    {
        // TaskoMask shape: a net6 build project among net8 projects. Oldest-first, so
        // the repo converges one hop per run instead of refusing (or leaping).
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("Api", "net8.0"), ("Build", "net6.0"), ("Core", "net8.0")), 0);

        Assert.Equal("net6.0", result.Version);
        var note = Assert.Single(result.Notes);
        Assert.Contains("Api=net8.0", note);
        Assert.Contains("Core=net8.0", note);
        Assert.DoesNotContain("Build=", note);
    }

    [Fact]
    public void ProjectsThatCannotBeOrdered_StillRefuseRatherThanGuess()
    {
        // An unknown TFM next to modern .NET: there's no honest "oldest".
        var ex = Assert.Throws<EolupUserException>(() =>
            VersionPlanning.DetectCurrent("/repo", Projects(("Store", "uap10.0"), ("Api", "net8.0")), 0));

        Assert.Contains("can't order", ex.Message);
    }

    [Fact]
    public void OnlyNetStandardProjects_SaysThereIsNothingToUpgrade()
    {
        var ex = Assert.Throws<EolupUserException>(() =>
            VersionPlanning.DetectCurrent("/repo", Projects(("Lib", "netstandard2.0")), 0));

        Assert.Contains("nothing to upgrade", ex.Message);
    }

    [Fact]
    public void WhenMostProjectsCannotBeEvaluated_TheFewThatCanAreNotTakenAsTheRepo()
    {
        var ex = Assert.Throws<EolupUserException>(() =>
            VersionPlanning.DetectCurrent("/repo", Projects(("docs", "net8.0")), unresolvedCount: 7));

        Assert.Contains("1 of 8", ex.Message);
    }

    // ------------------------------------------------------------------ what a run rewrites

    [Fact]
    public void OnlyProjectsOnTheCurrentVersionAreBumped_NeverNetStandardOrThoseAlreadyAhead()
    {
        var all = Projects(("Build", "net6.0"), ("Tests", "net6.0"), ("Api", "net8.0"), ("Domain", "netstandard2.1"));

        var bumped = VersionPlanning.SelectProjectsToBump(all, "net6.0", "net8.0");

        Assert.Equal(["Build", "Tests"], bumped.Select(b => Path.GetFileNameWithoutExtension(b.File)).Order());
        Assert.All(bumped, b => Assert.Equal("net8.0", b.NewTfm));
    }

    [Fact]
    public void ThePlatformSuffixIsKept()
    {
        var bumped = VersionPlanning.SelectProjectsToBump(
            Projects(("Desktop", "net8.0-windows")), "net8.0-windows", "net10.0");

        Assert.Equal("net10.0-windows", Assert.Single(bumped).NewTfm);
    }

    [Theory]
    [InlineData("net8.0", "net8.0")]   // already there
    [InlineData("net10.0", "net8.0")]  // would be a downgrade
    [InlineData("net48", "net10.0")]   // can't order the current version
    public void NothingIsBumped_WhenTheTargetIsNotANewerVersion(string current, string target) =>
        Assert.Empty(VersionPlanning.SelectProjectsToBump(Projects(("Api", current)), current, target));

    // ------------------------------------------------------------------ multi-targeted projects

    [Theory]
    [InlineData("net461", true)]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("net8.0", false)]
    [InlineData("netstandard2.0", false)]
    public void IsNetFramework_RecognisesDotlessFrameworkTfms(string tfm, bool expected) =>
        Assert.Equal(expected, VersionPlanning.IsNetFramework(tfm));

    [Fact]
    public void AMultiTargetedProjectsVersion_IsItsOldestModernEntry()
    {
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("Lib", "net8.0;net6.0;netstandard2.0"), ("Api", "net6.0")), 0);

        Assert.Equal("net6.0", result.Version);
    }

    [Fact]
    public void DappersShape_IsDetected_WithANoteThatCompatibilityEntriesStay()
    {
        // Real repo that used to be refused ("Only 1 of 8 projects could be evaluated").
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("Dapper", "net461;netstandard2.0;net8.0"), ("docs", "net8.0")), 0);

        Assert.Equal("net8.0", result.Version);
        var note = Assert.Single(result.Notes);
        Assert.Contains("Dapper targets several frameworks", note);
    }

    [Fact]
    public void AListWithNoModernEntry_IsLeftAlone_LikeNetStandard()
    {
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("Compat", "net461;netstandard2.0"), ("Api", "net8.0")), 0);

        Assert.Equal("net8.0", result.Version);
        Assert.Contains(result.Notes, n => n.Contains("Compat (net461;netstandard2.0) is left alone"));
    }

    [Fact]
    public void ASingleTargetFrameworkProject_IsLeftAlone_WithANote()
    {
        // Found on real repos: Dapper.EntityFramework (net461) and CliWrap.Signaler
        // (net35) are deliberate .NET Framework components of modern libraries. An
        // earlier version of this rule refused both whole repos over them.
        var result = VersionPlanning.DetectCurrent("/repo", Projects(("Signaler", "net35"), ("Lib", "net461;net8.0")), 0);

        Assert.Equal("net8.0", result.Version);
        Assert.Contains(result.Notes, n => n.Contains("Signaler (net35) is left alone") && n.Contains(".NET Framework"));
    }

    [Fact]
    public void ProjectsDifferingOnlyByPlatformSuffix_AreOnTheSameVersion()
    {
        // MAUI-style: "net10.0-android;net10.0-ios" and "net10.0" are all 10.0 — no
        // "different versions" note, and the version is reported without a suffix.
        var result = VersionPlanning.DetectCurrent("/repo",
            Projects(("App", "net10.0-android;net10.0-ios"), ("Core", "net10.0")), 0);

        Assert.Equal("net10.0", result.Version);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("net6.0;net8.0;netstandard2.0", "net8.0;netstandard2.0")]         // EOL entry replaced, duplicate dropped
    [InlineData("net461;netstandard2.0;net6.0", "net461;netstandard2.0;net8.0")]  // compatibility entries untouched
    [InlineData("net6.0-android;net6.0-ios", "net8.0-android;net8.0-ios")]        // platform suffixes kept
    [InlineData("net6.0;net10.0", "net8.0;net10.0")]                               // a newer entry is left where it is
    [InlineData("$(Base);net6.0", "$(Base);net8.0")]                               // property references pass through
    [InlineData(" net6.0 ; netstandard2.0 ", " net8.0 ; netstandard2.0 ")]        // whitespace preserved
    [InlineData("net8.0;netstandard2.0", "net8.0;netstandard2.0")]                 // nothing on the current version
    public void RetargetList_MovesOnlyEntriesOnTheCurrentVersion(string list, string expected) =>
        Assert.Equal(expected, VersionPlanning.RetargetList(list, new Version(6, 0), "net8.0"));

    [Fact]
    public void SelectProjectsToBump_IncludesAMultiTargetedProjectWithAnEntryOnTheCurrentVersion()
    {
        var all = Projects(("Lib", "net6.0;netstandard2.0"), ("Ahead", "net8.0;net10.0"), ("Api", "net6.0"));

        var bumped = VersionPlanning.SelectProjectsToBump(all, "net6.0", "net8.0");

        var lib = Assert.Single(bumped, b => b.File.EndsWith("Lib.csproj"));
        Assert.True(lib.MultiTarget);
        Assert.Equal("net8.0;netstandard2.0", lib.NewTfm);
        Assert.DoesNotContain(bumped, b => b.File.EndsWith("Ahead.csproj"));
        Assert.False(Assert.Single(bumped, b => b.File.EndsWith("Api.csproj")).MultiTarget);
    }
}
