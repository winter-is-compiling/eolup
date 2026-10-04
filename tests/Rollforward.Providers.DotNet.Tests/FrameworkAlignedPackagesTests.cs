using Xunit;

namespace Rollforward.Providers.DotNet.Tests;

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
    public void ToleratesProjectFilesThatAreNotValidXml()
    {
        using var dir = new TempDir().AsRepoRoot();
        dir.Write("Broken/Broken.csproj", "<Project><ItemGroup>");
        dir.Write("App/App.csproj", Project("<PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));

        Assert.Single(FindIn(dir, 8));
    }
}
