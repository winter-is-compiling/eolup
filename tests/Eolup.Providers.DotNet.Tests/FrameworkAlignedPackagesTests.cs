using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class FrameworkAlignedPackagesTests
{
    private static string Project(string items) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
        $"  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n";

    private static IReadOnlyList<string> FindIn(TempDir dir, int oldMajor) =>
        FrameworkAlignedPackages.Find(dir.Path, CsProjHelper.FindProjectFiles(dir.Path), oldMajor);

    [Fact]
    public void FlagsFrameworkPackagesStillOnTheOldMajor()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("tests/Api.Tests/Api.Tests.csproj", Project(
            "<PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" />"));

        var found = Assert.Single(FindIn(dir, 8));

        Assert.Equal("Microsoft.AspNetCore.Mvc.Testing 8.0.11 (tests/Api.Tests/Api.Tests.csproj)", found);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "8.0.1")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions", "8.0.0")]
    [InlineData("System.Text.Json", "8.0.5")]
    [InlineData("Microsoft.Extensions.Options", "8.*")]
    [InlineData("Microsoft.Extensions.Options", "[8.0.0,9.0.0)")]
    public void RecognisesEachFamilyAndFloatingOrRangeVersions(string id, string version)
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project($"<PackageReference Include=\"{id}\" Version=\"{version}\" />"));

        Assert.Single(FindIn(dir, 8));
    }

    [Fact]
    public void LeavesAlonePackagesAlreadyOnAnotherMajorOrOutsideTheFamilies()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project(
            "<PackageReference Include=\"Microsoft.Extensions.Logging\" Version=\"10.0.1\" />\n" +
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"8.0.3\" />\n" +
            "<PackageReference Include=\"xunit\" Version=\"2.5.3\" />"));

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void SkipsEntriesThatChooseAVersionPerFramework()
    {
        // Someone is already managing this deliberately — flagging it would be noise.
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project(
            "<PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />\n" +
            "<PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"10.0.1\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />"));

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void SkipsVersionsItCannotReadLikeMsBuildProperties()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project(
            "<PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"$(AspNetVersion)\" />\n" +
            "<PackageReference Include=\"Microsoft.Extensions.Options\" />"));

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void ReadsVersionElementsAndCentralPackageManagement()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project(
            "<PackageReference Include=\"Microsoft.EntityFrameworkCore\"><Version>8.0.2</Version></PackageReference>"));
        dir.Write("Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" /></ItemGroup></Project>");

        var found = FindIn(dir, 8);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.StartsWith("Microsoft.EntityFrameworkCore 8.0.2 (App/App.csproj)"));
        Assert.Contains(found, f => f.StartsWith("Microsoft.AspNetCore.Mvc.Testing 8.0.11 (Directory.Packages.props)"));
    }

    [Fact]
    public void FindsPackagesInARepoThatLivesUnderADirectoryCalledBinOrObj()
    {
        // The bin/obj filter once looked at the whole absolute path, so a checkout at
        // ~/bin/myrepo or C:\work\obj\app silently produced no hints at all.
        using var dir = new TempDir();
        var repo = Path.Combine(dir.Path, "bin", "obj", "myrepo");
        var project = dir.Write("bin/obj/myrepo/App/App.csproj",
            Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));
        dir.Write("bin/obj/myrepo/Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" /></ItemGroup></Project>");

        var found = FrameworkAlignedPackages.Find(repo, [project], 8);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void DoesNotSearchBuildOutputOrDependencyFoldersForSharedFiles()
    {
        using var dir = new TempDir().AsRepoRoot();
        const string props =
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" /></ItemGroup></Project>";
        dir.Write("obj/Directory.Packages.props", props);
        dir.Write("src/node_modules/pkg/Directory.Build.props", props);
        dir.Write("src/bin/Debug/Directory.Packages.props", props);

        Assert.Empty(FrameworkAlignedPackages.Find(dir.Path, [], 8));
    }

    [Fact]
    public void OnlyLooksAtTheProjectsItIsGiven()
    {
        // A project left on another line on purpose, or not part of this bump, isn't what broke.
        using var dir = new TempDir().AsRepoRoot();
        var bumped = dir.Write("A/A.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));
        dir.Write("B/B.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Logging\" Version=\"8.0.0\" />"));

        var found = Assert.Single(FrameworkAlignedPackages.Find(dir.Path, [bumped], 8));

        Assert.StartsWith("Microsoft.Extensions.Options", found);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.OData", "8.2.5")]
    [InlineData("Microsoft.Extensions.Http.Resilience", "8.10.0")]
    [InlineData("Microsoft.Extensions.AI.Abstractions", "8.0.0")]
    public void SkipsPackagesWithTheirOwnVersionLine(string id, string version)
    {
        // OData 8.x is not "for .NET 8"; telling the user to bump it would send triage the wrong way.
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project($"<PackageReference Include=\"{id}\" Version=\"{version}\" />"));

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void SkipsEntriesWhenAnyAncestorHasACondition()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Choose><When Condition=\"'$(TargetFramework)' == 'net8.0'\"><ItemGroup>" +
            "<PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" />" +
            "</ItemGroup></When></Choose></Project>");

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void ReadsPackagesDeclaredInDirectoryBuildProps()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("Directory.Build.props",
            "<Project><ItemGroup><PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" /></ItemGroup></Project>");

        var found = Assert.Single(FindIn(dir, 8));

        Assert.Equal("Microsoft.Extensions.Options 8.0.0 (Directory.Build.props)", found);
    }

    [Fact]
    public void AnAbsurdlyLargeMajorIsIgnored_NotAnException()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("App/App.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"99999999999.0.0\" />"));

        Assert.Empty(FindIn(dir, 8));
    }

    [Fact]
    public void AFileThatCannotBeReadIsSkipped_NotAnException()
    {
        // A hint is advisory: a locked or unreadable file must never abort the run.
        using var dir = new TempDir().AsRepoRoot();
        var good = dir.Write("A/A.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));
        var missing = Path.Combine(dir.Path, "Gone", "Gone.csproj");

        var found = FrameworkAlignedPackages.Find(dir.Path, [missing, good], 8);

        Assert.Single(found);
    }

    [Fact]
    public void ToleratesProjectFilesThatAreNotValidXml()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("Broken/Broken.csproj", "<Project><ItemGroup>");
        dir.Write("App/App.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));

        Assert.Single(FindIn(dir, 8));
    }
}
