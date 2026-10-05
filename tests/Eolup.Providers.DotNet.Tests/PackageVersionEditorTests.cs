using System.Text;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class PackageVersionEditorTests
{
    private static string Project(string items) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
        $"  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n";

    private static IReadOnlyList<StalePackage> Stale(TempDir dir) =>
        FrameworkAlignedPackages.FindStale(dir.Path, CsProjHelper.FindProjectFiles(dir.Path), 8);

    private static Dictionary<string, string> To10(params string[] ids) => ids.ToDictionary(i => i, _ => "10.0.1");

    [Fact]
    public void RewritesAnAttributeVersion_AndNothingElse()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" />\n" +
            "    <PackageReference Include=\"xunit\" Version=\"2.5.3\" />"));
        var before = File.ReadAllText(file);

        var result = PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.AspNetCore.Mvc.Testing"));

        Assert.Equal(before.Replace("Version=\"8.0.11\"", "Version=\"10.0.1\""), File.ReadAllText(file));
        Assert.Equal([file], result.ChangedFiles);
        Assert.Empty(result.NotEdited);
    }

    [Fact]
    public void HandlesVersionBeforeIncludeAndSingleQuotes()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference Version='8.0.0' Include='Microsoft.Extensions.Options' PrivateAssets=\"all\" />"));

        PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.Extensions.Options"));

        Assert.Contains("Version='10.0.1' Include='Microsoft.Extensions.Options'", File.ReadAllText(file));
    }

    [Fact]
    public void HandlesATagSpreadOverSeveralLines()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference\n        Include=\"Microsoft.Extensions.Options\"\n        Version=\"8.0.0\"\n        PrivateAssets=\"all\" />"));

        PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.Extensions.Options"));

        Assert.Contains("        Version=\"10.0.1\"\n        PrivateAssets", File.ReadAllText(file));
    }

    [Fact]
    public void RewritesAChildVersionElement()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference Include=\"Microsoft.EntityFrameworkCore\">\n      <Version> 8.0.2 </Version>\n    </PackageReference>"));

        PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.EntityFrameworkCore"));

        Assert.Contains("<Version> 10.0.1 </Version>", File.ReadAllText(file));
    }

    [Fact]
    public void RewritesCentralPackageManagementAndVersionOverride()
    {
        using var dir = new TempDir().AsRepoRoot();
        var props = dir.Write("Directory.Packages.props",
            "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"8.0.11\" />\n  </ItemGroup>\n</Project>\n");
        var project = dir.Write("App/App.csproj", Project(
            "    <PackageReference Include=\"Microsoft.Extensions.Options\" VersionOverride=\"8.0.0\" />"));

        var result = PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.AspNetCore.Mvc.Testing", "Microsoft.Extensions.Options"));

        Assert.Contains("Version=\"10.0.1\"", File.ReadAllText(props));
        Assert.Contains("VersionOverride=\"10.0.1\"", File.ReadAllText(project));
        Assert.Equal(2, result.ChangedFiles.Count);
    }

    [Fact]
    public void KeepsTheBom_AndWindowsLineEndings()
    {
        using var dir = new TempDir().AsRepoRoot();
        var content = Project("    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />").Replace("\n", "\r\n");
        var file = dir.Write("App/App.csproj", content, withBom: true);

        PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.Extensions.Options"));

        var bytes = File.ReadAllBytes(file);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes)[1..];
        Assert.Equal(content.Replace("8.0.0", "10.0.1"), text);
    }

    [Fact]
    public void ACommentedOutEntryBeforeTheRealOneDoesNotShiftTheTarget()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <!-- <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" /> -->\n" +
            "    <PackageReference Include=\"Microsoft.Extensions.Logging\" Version=\"8.0.0\" />\n" +
            "    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));

        PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.Extensions.Logging", "Microsoft.Extensions.Options"));

        var text = File.ReadAllText(file);
        Assert.Contains("<!-- <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" /> -->", text);
        Assert.Contains("Include=\"Microsoft.Extensions.Logging\" Version=\"10.0.1\"", text);
        Assert.Contains("Include=\"Microsoft.Extensions.Options\" Version=\"10.0.1\"", text);
    }

    [Fact]
    public void OnlyTheDetectedEntryIsEdited_NotAConditionalOrUnrelatedOneWithTheSameId()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />\n" +
            "    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));

        var stale = Stale(dir);
        PackageVersionEditor.Apply(stale, To10("Microsoft.Extensions.Options"));

        var lines = File.ReadAllLines(file).Where(l => l.Contains("Microsoft.Extensions.Options")).ToArray();
        Assert.Single(stale);
        Assert.Contains("Version=\"8.0.0\" Condition", lines[0]);
        Assert.Contains("Version=\"10.0.1\"", lines[1]);
    }

    [Fact]
    public void ReportsEntriesItCouldNotEdit_AndLeavesTheFileAlone()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project("    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />"));
        var stale = Stale(dir);
        var before = File.ReadAllText(file);

        // The file changed under us: the entry at that position is no longer the package that was detected.
        File.WriteAllText(file, before.Replace("Microsoft.Extensions.Options", "Something.Else"));
        var changedBefore = File.ReadAllText(file);

        var result = PackageVersionEditor.Apply(stale, To10("Microsoft.Extensions.Options"));

        Assert.Equal(changedBefore, File.ReadAllText(file));
        Assert.Empty(result.ChangedFiles);
        Assert.Single(result.NotEdited);
    }

    [Fact]
    public void APackageWithNoNewVersionIsReportedNotEdited()
    {
        using var dir = new TempDir().AsRepoRoot();
        var file = dir.Write("App/App.csproj", Project(
            "    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"8.0.0\" />\n" +
            "    <PackageReference Include=\"Microsoft.Extensions.Logging\" Version=\"8.0.0\" />"));

        var result = PackageVersionEditor.Apply(Stale(dir), To10("Microsoft.Extensions.Options"));

        var skipped = Assert.Single(result.NotEdited);
        Assert.Equal("Microsoft.Extensions.Logging", skipped.Id);
        Assert.Contains("Microsoft.Extensions.Logging\" Version=\"8.0.0\"", File.ReadAllText(file));
    }
}
