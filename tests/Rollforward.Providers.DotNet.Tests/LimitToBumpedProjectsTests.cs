using Xunit;

namespace Rollforward.Providers.DotNet.Tests;

public class LimitToBumpedProjectsTests
{
    private static StalePackage In(string file, string id = "Microsoft.Extensions.Options") =>
        new(id, "8.0.0", file, Path.GetFileName(file), 0);

    private static readonly string A = Path.Combine("repo", "A", "A.csproj");
    private static readonly string B = Path.Combine("repo", "B", "B.csproj");
    private static readonly string Props = Path.Combine("repo", "Directory.Packages.props");

    [Fact]
    public void EverythingIsKept_WhenEveryProjectInTheRepoIsBeingBumped()
    {
        var stale = new[] { In(A), In(B), In(Props, "Microsoft.AspNetCore.Mvc.Testing") };

        var kept = FrameworkAlignedPackages.LimitToBumpedProjects(stale, allProjectFiles: [A, B], bumpedProjectFiles: [A, B]);

        Assert.Equal(stale, kept);
    }

    [Fact]
    public void SharedFilesAreDropped_WhenSomeProjectIsLeftBehind()
    {
        // B stays on its own line; a central version edit would change its dependencies too.
        var stale = new[] { In(A), In(B), In(Props, "Microsoft.AspNetCore.Mvc.Testing") };

        var kept = FrameworkAlignedPackages.LimitToBumpedProjects(stale, allProjectFiles: [A, B], bumpedProjectFiles: [A]);

        Assert.Equal([stale[0]], kept);
    }

    [Fact]
    public void FileComparisonIgnoresCase()
    {
        var stale = new[] { In(A.ToUpperInvariant()) };

        var kept = FrameworkAlignedPackages.LimitToBumpedProjects(stale, allProjectFiles: [A, B], bumpedProjectFiles: [A]);

        Assert.Single(kept);
    }

    [Fact]
    public void NothingStale_IsNothing()
    {
        Assert.Empty(FrameworkAlignedPackages.LimitToBumpedProjects([], [A], [A]));
    }
}
