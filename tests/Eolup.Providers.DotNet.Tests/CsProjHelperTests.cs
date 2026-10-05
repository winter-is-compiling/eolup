using Eolup.Core;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class CsProjHelperTests
{
    private const string SingleTargetProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFramework>net8.0</TargetFramework>\r\n" +
        "    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    // ---------------------------------------------------------------- FindProjectFiles

    [Fact]
    public void FindProjectFiles_FindsProjectsAtAnyDepth()
    {
        using var dir = new TempDir();
        dir.Write("src/App/App.csproj", SingleTargetProject);
        dir.Write("tests/App.Tests/App.Tests.csproj", SingleTargetProject);

        var found = CsProjHelper.FindProjectFiles(dir.Path);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void FindProjectFiles_ExcludesTraversalProjects()
    {
        // Dapper's root Build.csproj aggregates every other project, has no
        // TargetFramework by nature, and used to get picked as "the project".
        using var dir = new TempDir();
        dir.Write("Build.csproj", "<Project Sdk=\"Microsoft.Build.Traversal/2.0.24\"></Project>");
        var real = dir.Write("Lib/Lib.csproj", SingleTargetProject);

        var found = CsProjHelper.FindProjectFiles(dir.Path);

        Assert.Equal([real], found);
    }

    [Fact]
    public void FindProjectFiles_ReturnsEmptyForMissingDirectory()
    {
        Assert.Empty(CsProjHelper.FindProjectFiles(Path.Combine(Path.GetTempPath(), "eolup-does-not-exist-" + Guid.NewGuid())));
    }

    // ---------------------------------------------------------------- AnyTestProjectExists

    [Theory]
    [InlineData("tests/App.Tests/App.Tests.csproj", true)]
    [InlineData("tests/UnitTests/UnitTests.csproj", true)]
    [InlineData("tests/app.tests/app.tests.csproj", true)] // case-insensitive
    [InlineData("src/App/App.csproj", false)]
    public void AnyTestProjectExists_MatchesByNameConvention(string projectPath, bool expected)
    {
        using var dir = new TempDir();
        dir.Write(projectPath, SingleTargetProject);

        Assert.Equal(expected, CsProjHelper.AnyTestProjectExists(dir.Path));
    }

    // ---------------------------------------------------------------- ResolveBuildTarget

    [Fact]
    public void ResolveBuildTarget_ConfiguredSolutionAlwaysWins_EvenWithSeveralSolutionFiles()
    {
        using var dir = new TempDir();
        dir.Write("App.sln", "");
        var chosen = dir.Write("Everything.sln", "");

        Assert.Equal(chosen, CsProjHelper.ResolveBuildTarget(dir.Path, "Everything.sln"));
    }

    [Fact]
    public void ResolveBuildTarget_ConfiguredSolutionThatDoesNotExist_ThrowsClearError()
    {
        using var dir = new TempDir();
        dir.Write("App.sln", "");

        var ex = Assert.Throws<EolupUserException>(() => CsProjHelper.ResolveBuildTarget(dir.Path, "Missing.sln"));

        Assert.Contains("Missing.sln", ex.Message);
    }

    [Fact]
    public void ResolveBuildTarget_SingleSolutionWithStrayProjectAtRoot_UsesTheSolution()
    {
        // eShopOnWeb's shape (a real .sln beside an unrelated .dcproj): bare
        // `dotnet build <dir>` fails with MSB1011/MSB1050, but there's only one
        // solution that matters.
        using var dir = new TempDir();
        var sln = dir.Write("App.sln", "");
        dir.Write("Tooling.csproj", SingleTargetProject);

        Assert.Equal(sln, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_SingleSlnxIsRecognisedToo()
    {
        using var dir = new TempDir();
        var slnx = dir.Write("App.slnx", "<Solution />");

        Assert.Equal(slnx, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_NoSolutionButExactlyOneRootProject_UsesTheProject()
    {
        using var dir = new TempDir();
        var proj = dir.Write("App.csproj", SingleTargetProject);

        Assert.Equal(proj, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_SeveralSolutions_FallsBackToTheDirectory_RatherThanGuessing()
    {
        // Deliberate: silently picking one of two competing solutions would be
        // worse than the clear ambiguity error the caller raises from the fallback.
        using var dir = new TempDir();
        dir.Write("App.sln", "");
        dir.Write("Everything.sln", "");

        Assert.Equal(dir.Path, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_SeveralRootProjectsAndNoSolution_FallsBackToTheDirectory()
    {
        using var dir = new TempDir();
        dir.Write("A.csproj", SingleTargetProject);
        dir.Write("B.csproj", SingleTargetProject);

        Assert.Equal(dir.Path, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_EmptyDirectory_FallsBackToTheDirectory()
    {
        using var dir = new TempDir();

        Assert.Equal(dir.Path, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_NothingAtTheRoot_ButOneSolutionInASubfolder_UsesIt()
    {
        // src/App.sln is a common layout; without this `dotnet build <dir>` hits
        // MSB1003.
        using var dir = new TempDir();
        var sln = dir.Write("src/App.sln", "");
        dir.Write("src/App/App.csproj", SingleTargetProject);

        Assert.Equal(sln, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_SeveralSolutionsInSubfolders_FallsBackRatherThanGuessing()
    {
        // adnc's shape: six solutions, none at the root.
        using var dir = new TempDir();
        dir.Write("src/Gateway/Gateway.sln", "");
        dir.Write("src/Services/Services.sln", "");

        Assert.Equal(dir.Path, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_ASolutionInASubfolderIsNotUsed_WhenTheRootHasAProject()
    {
        using var dir = new TempDir();
        var proj = dir.Write("App.csproj", SingleTargetProject);
        dir.Write("samples/Sample.sln", "");

        Assert.Equal(proj, CsProjHelper.ResolveBuildTarget(dir.Path, null));
    }

    [Fact]
    public void ResolveBuildTarget_AConfiguredSolutionMayBeInASubfolder()
    {
        using var dir = new TempDir();
        var chosen = dir.Write("src/Services/Services.sln", "");
        dir.Write("src/Gateway/Gateway.sln", "");

        Assert.Equal(chosen, CsProjHelper.ResolveBuildTarget(dir.Path, "src/Services/Services.sln"));
    }

    [Fact]
    public void FindSolutionFiles_IgnoresBuildOutput()
    {
        using var dir = new TempDir();
        var real = dir.Write("src/App.sln", "");
        dir.Write("src/App/bin/Debug/Copied.sln", "");
        dir.Write("src/App/obj/Generated.slnx", "");

        Assert.Equal([real], CsProjHelper.FindSolutionFiles(dir.Path));
    }

    [Fact]
    public void DescribeNoBuildTargetAtRoot_ListsTheSolutionsAndPointsAtTheSolutionSetting()
    {
        using var dir = new TempDir();
        dir.Write("src/Gateway/Gateway.sln", "");
        dir.Write("src/Services/Services.sln", "");

        var message = CsProjHelper.DescribeNoBuildTargetAtRoot(dir.Path);

        Assert.Contains("src/Gateway/Gateway.sln", message);
        Assert.Contains("src/Services/Services.sln", message);
        Assert.Contains("solution:", message);
        Assert.DoesNotContain("does not build", message);
    }

    [Fact]
    public void DescribeNoBuildTargetAtRoot_ShortensALongList()
    {
        using var dir = new TempDir();
        foreach (var i in Enumerable.Range(1, 7))
            dir.Write($"s{i}/S{i}.sln", "");

        Assert.Contains("and 2 more", CsProjHelper.DescribeNoBuildTargetAtRoot(dir.Path));
    }

    // ---------------------------------------------------------------- WriteTargetFramework

    [Fact]
    public void WriteTargetFramework_ChangesOnlyTheTfm_LeavingEverythingElseByteIdentical()
    {
        // The whole point of a "trivial to review" PR: the diff is one line, not a
        // reformatted file (an XML load/save round-trip changes blank lines,
        // adds a declaration, drops the trailing newline).
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject);

        CsProjHelper.WriteTargetFramework(path, "net10.0");

        Assert.Equal(SingleTargetProject.Replace("net8.0", "net10.0"), File.ReadAllText(path));
    }

    [Fact]
    public void WriteTargetFramework_PreservesAnExistingBom()
    {
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject, withBom: true);

        CsProjHelper.WriteTargetFramework(path, "net10.0");

        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path).Take(3).ToArray());
    }

    [Fact]
    public void WriteTargetFramework_DoesNotAddABomTheFileNeverHad()
    {
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject, withBom: false);

        CsProjHelper.WriteTargetFramework(path, "net10.0");

        Assert.NotEqual([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path).Take(3).ToArray());
    }

    [Fact]
    public void WriteTargetFramework_IsIdempotent()
    {
        // Several projects can share one Directory.Build.props; the second call
        // must be a no-op, not an error.
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject);

        CsProjHelper.WriteTargetFramework(path, "net10.0");
        var afterFirst = File.ReadAllText(path);
        CsProjHelper.WriteTargetFramework(path, "net10.0");

        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void WriteTargetFramework_RewritesTheSharedPropsFile_WhenTheProjectDeclaresNothing()
    {
        using var dir = new TempDir().AsRepoRoot();
        const string props = "<Project>\r\n  <PropertyGroup>\r\n    <TargetFramework>net8.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        var propsPath = dir.Write("Directory.Build.props", props);
        var proj = dir.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        CsProjHelper.WriteTargetFramework(proj, "net10.0");

        Assert.Equal(props.Replace("net8.0", "net10.0"), File.ReadAllText(propsPath));
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", File.ReadAllText(proj)); // project itself untouched
    }

    [Fact]
    public void WriteTargetFramework_FindsDirectoryPackagesPropsToo()
    {
        // eShopOnWeb keeps <TargetFramework> in Directory.Packages.props.
        using var dir = new TempDir().AsRepoRoot();
        var propsPath = dir.Write("Directory.Packages.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var proj = dir.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        CsProjHelper.WriteTargetFramework(proj, "net10.0");

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", File.ReadAllText(propsPath));
    }

    // ---------------------------------------------------------------- CycleToTfm

    [Theory]
    [InlineData("10", "net10.0")]
    [InlineData("8.0", "net8.0")]
    [InlineData("9", "net9.0")]
    [InlineData("net10.0", "net10.0")] // an explicit `target: net10.0` in config must not become "netnet10.0.0"
    [InlineData("NET10.0", "NET10.0")]
    public void CycleToTfm_ConvertsEndOfLifeDateCyclesToTfms(string cycle, string expected)
    {
        Assert.Equal(expected, CsProjHelper.CycleToTfm(cycle));
    }

    [Theory]
    [InlineData("10", "net10.0")]
    [InlineData("8.0", "net8.0")]
    public void DotNetProvider_FormatVersion_RendersCyclesAsTfms(string cycle, string expected)
    {
        Assert.Equal(expected, new DotNetLanguageProvider().FormatVersion(cycle));
    }

    // ---------------------------------------------------------------- ReadTargetFrameworkAsync (real MSBuild)

    [Fact]
    public async Task ReadTargetFrameworkAsync_ReadsASingleDeclaredTfm()
    {
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject);

        Assert.Equal("net8.0", await CsProjHelper.ReadTargetFrameworkAsync(path));
    }

    [Fact]
    public async Task ReadTargetFrameworkAsync_ResolvesATfmDeclaredOnlyInASharedPropsFile()
    {
        // MSBuild's own evaluation handles the inheritance — the reason detection
        // asks MSBuild instead of parsing the .csproj's XML by hand.
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var proj = dir.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal("net9.0", await CsProjHelper.ReadTargetFrameworkAsync(proj));
    }

    [Fact]
    public async Task ReadTargetFrameworkAsync_ForAMultiTargetedProject_ReturnsTheWholeList()
    {
        using var dir = new TempDir();
        var path = dir.Write("Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>");

        Assert.Equal("net8.0;netstandard2.0", await CsProjHelper.ReadTargetFrameworkAsync(path));
    }

    // ---------------------------------------------------------------- WriteTargetFrameworks

    private const string MultiTargetProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net461;netstandard2.0;net6.0</TargetFrameworks>\r\n" +
        "    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    [Fact]
    public void WriteTargetFrameworks_MovesOnlyTheCurrentEntry_LeavingTheRestOfTheFileByteIdentical()
    {
        using var dir = new TempDir();
        var path = dir.Write("Lib.csproj", MultiTargetProject, withBom: true);

        CsProjHelper.WriteTargetFrameworks(path, new Version(6, 0), "net8.0");

        Assert.Equal(MultiTargetProject.Replace(";net6.0<", ";net8.0<"), File.ReadAllText(path));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path).Take(3).ToArray());
    }

    [Fact]
    public void WriteTargetFrameworks_RewritesConditionalListsToo_AndLeavesPropertyReferencesAlone()
    {
        // MAUI-style: a base list plus a conditional one that appends to it.
        const string project =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFrameworks>net8.0-android;net8.0-ios</TargetFrameworks>" +
            "<TargetFrameworks Condition=\"$([MSBuild]::IsOSPlatform('windows'))\">$(TargetFrameworks);net8.0-windows10.0.19041.0</TargetFrameworks>" +
            "</PropertyGroup></Project>";
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", project);

        CsProjHelper.WriteTargetFrameworks(path, new Version(8, 0), "net10.0");

        Assert.Equal(project.Replace("net8.0-", "net10.0-"), File.ReadAllText(path));
    }

    [Fact]
    public void WriteTargetFrameworks_IsIdempotent_ForASharedPropsFile()
    {
        using var dir = new TempDir().AsRepoRoot();
        var props = dir.Write("Directory.Build.props",
            "<Project><PropertyGroup><TargetFrameworks>net6.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>");
        var a = dir.Write("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var b = dir.Write("src/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal(props, CsProjHelper.WriteTargetFrameworks(a, new Version(6, 0), "net8.0"));
        Assert.Equal(props, CsProjHelper.WriteTargetFrameworks(b, new Version(6, 0), "net8.0"));

        Assert.Contains("<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", File.ReadAllText(props));
    }

    [Fact]
    public void CanRetargetFrameworks_IsFalse_WhenTheListIsBuiltFromAProperty()
    {
        // Polly-style indirection: the entries live in a custom property Eolup
        // doesn't follow, so rewriting would be guesswork — refuse up front instead.
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("Directory.Build.props", "<Project><PropertyGroup><LibraryTargets>net6.0;netstandard2.0</LibraryTargets></PropertyGroup></Project>");
        var viaProperty = dir.Write("src/Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>$(LibraryTargets)</TargetFrameworks></PropertyGroup></Project>");
        var literal = dir.Write("src/Other/Other.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net6.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>");

        Assert.False(CsProjHelper.CanRetargetFrameworks(viaProperty, new Version(6, 0)));
        Assert.True(CsProjHelper.CanRetargetFrameworks(literal, new Version(6, 0)));
    }

    // ---------------------------------------------------------------- RewriteTargetFramework / CanRewriteTargetFramework

    private static VersionPlanning.ProjectBump Bump(string file, string newTfm, bool multiTarget = false) =>
        new(file, newTfm, multiTarget);

    // Prowlarr, MonoGame and workflow-core declare <TargetFrameworks>net8.0</TargetFrameworks>: a plural
    // element with ONE entry. It evaluates like a single target, but there is no singular element to find.
    private const string SingleEntryPluralProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net8.0</TargetFrameworks>\r\n" +
        "    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    [Fact]
    public void RewriteTargetFramework_SingleEntryPluralList_StaysPlural_LeavingTheRestOfTheFileByteIdentical()
    {
        using var dir = new TempDir();
        var path = dir.Write("Lib.csproj", SingleEntryPluralProject, withBom: true);

        var written = CsProjHelper.RewriteTargetFramework(Bump(path, "net10.0"), new Version(8, 0), "net10.0");

        Assert.Equal(path, written);
        Assert.Equal(SingleEntryPluralProject.Replace("net8.0", "net10.0"), File.ReadAllText(path));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path).Take(3).ToArray());
    }

    [Fact]
    public void RewriteTargetFramework_SingleEntryPluralList_KeepsThePlatformSuffix()
    {
        using var dir = new TempDir();
        var path = dir.Write("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0-windows</TargetFrameworks></PropertyGroup></Project>");

        CsProjHelper.RewriteTargetFramework(Bump(path, "net10.0-windows"), new Version(8, 0), "net10.0");

        Assert.Contains("<TargetFrameworks>net10.0-windows</TargetFrameworks>", File.ReadAllText(path));
    }

    [Fact]
    public void RewriteTargetFramework_SingleEntryPluralList_InASharedPropsFile_RewritesThatFile()
    {
        using var dir = new TempDir().AsRepoRoot();
        const string props = "<Project>\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net8.0</TargetFrameworks>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        var propsPath = dir.Write("Directory.Build.props", props);
        var proj = dir.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var written = CsProjHelper.RewriteTargetFramework(Bump(proj, "net10.0"), new Version(8, 0), "net10.0");

        Assert.Equal(propsPath, written);
        Assert.Equal(props.Replace("net8.0", "net10.0"), File.ReadAllText(propsPath));
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", File.ReadAllText(proj)); // project itself untouched
    }

    [Fact]
    public void RewriteTargetFramework_SingularDeclaration_StillGoesThroughTheSingularElement()
    {
        using var dir = new TempDir();
        var path = dir.Write("App.csproj", SingleTargetProject);

        var written = CsProjHelper.RewriteTargetFramework(Bump(path, "net10.0"), new Version(8, 0), "net10.0");

        Assert.Equal(path, written);
        Assert.Equal(SingleTargetProject.Replace("net8.0", "net10.0"), File.ReadAllText(path));
    }

    [Fact]
    public void RewriteTargetFramework_ASingularDeclarationWinsOverAPluralOneElsewhere()
    {
        // When the project spells the singular element, MSBuild builds a single target and ignores the
        // plural one in the shared file: the singular element is the declaration to edit.
        using var dir = new TempDir().AsRepoRoot();
        var propsPath = dir.Write("Directory.Build.props",
            "<Project><PropertyGroup><TargetFrameworks>net6.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>");
        var proj = dir.Write("src/App/App.csproj", SingleTargetProject);

        CsProjHelper.RewriteTargetFramework(Bump(proj, "net10.0"), new Version(8, 0), "net10.0");

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", File.ReadAllText(proj));
        Assert.Contains("net6.0;netstandard2.0", File.ReadAllText(propsPath));
    }

    [Fact]
    public void RewriteTargetFramework_AListWithSeveralEntries_IsRetargetedEntryByEntry()
    {
        using var dir = new TempDir();
        var path = dir.Write("Lib.csproj", MultiTargetProject);

        CsProjHelper.RewriteTargetFramework(Bump(path, "net8.0;netstandard2.0", multiTarget: true), new Version(6, 0), "net8.0");

        Assert.Equal(MultiTargetProject.Replace(";net6.0<", ";net8.0<"), File.ReadAllText(path));
    }

    [Fact]
    public void CanRewriteTargetFramework_IsTrue_ForEverySpellingEolupCanWrite()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("shared/Directory.Build.props", "<Project><PropertyGroup><TargetFrameworks>net8.0</TargetFrameworks></PropertyGroup></Project>");
        var singular = dir.Write("a/A.csproj", SingleTargetProject);
        var pluralSingleEntry = dir.Write("b/B.csproj", SingleEntryPluralProject);
        var viaProps = dir.Write("shared/C/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.True(CsProjHelper.CanRewriteTargetFramework(Bump(singular, "net10.0"), new Version(8, 0)));
        Assert.True(CsProjHelper.CanRewriteTargetFramework(Bump(pluralSingleEntry, "net10.0"), new Version(8, 0)));
        Assert.True(CsProjHelper.CanRewriteTargetFramework(Bump(viaProps, "net10.0"), new Version(8, 0)));
    }

    [Fact]
    public void CanRewriteTargetFramework_IsFalse_WhenTheFrameworkComesFromSomewhereEolupDoesNotEdit()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("build/framework.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var viaImport = dir.Write("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"../../build/framework.props\" /></Project>");
        var viaProperty = dir.Write("src/Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>$(LibraryTarget)</TargetFrameworks></PropertyGroup></Project>");

        Assert.False(CsProjHelper.CanRewriteTargetFramework(Bump(viaImport, "net10.0"), new Version(8, 0)));
        Assert.False(CsProjHelper.CanRewriteTargetFramework(Bump(viaProperty, "net10.0"), new Version(8, 0)));
    }
}
