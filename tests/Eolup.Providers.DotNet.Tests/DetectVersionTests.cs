using Eolup.Core;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

/// <summary>DetectVersionAsync against throwaway repos, using real MSBuild evaluation.</summary>
public class DetectVersionTests
{
    private const string Single =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";

    private const string Multi =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>";

    [Fact]
    public async Task AMultiTargetProjectAmongSingleTargetServices_IsReadAndAgrees()
    {
        // dotnet/eShop's shape: single-target services next to one multi-target
        // client. The list is read, and its modern entry agrees.
        using var dir = new TempDir();
        dir.Write("src/A/A.csproj", Single);
        dir.Write("src/B/B.csproj", Single);
        dir.Write("src/Client/Client.csproj", Multi);

        Assert.Equal("net8.0", (await new DotNetLanguageProvider().DetectVersionAsync(dir.Path)).Version);
    }

    [Fact]
    public async Task DappersShape_IsNoLongerRefused_TheLibrariesThemselvesAreRead()
    {
        // Real libraries multi-target; only docs.csproj is single-target. That used
        // to be refused ("Only 1 of 3 projects could be evaluated") —
        // with the libraries readable they now answer for themselves.
        using var dir = new TempDir();
        dir.Write("Lib1/Lib1.csproj", Multi);
        dir.Write("Lib2/Lib2.csproj", Multi);
        dir.Write("docs/docs.csproj", Single);

        var detection = await new DotNetLanguageProvider().DetectVersionAsync(dir.Path);

        Assert.Equal("net8.0", detection.Version);
        Assert.Contains(detection.Notes, n => n.Contains("Lib1 targets several frameworks"));
    }
}
