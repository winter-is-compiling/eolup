using Rollforward.Core.Config;
using Xunit;

namespace Rollforward.Core.Tests;

public class RollforwardConfigTests
{
    private static RollforwardConfig LoadYaml(string? yaml)
    {
        var dir = Directory.CreateTempSubdirectory("rollforward-config-").FullName;
        try
        {
            if (yaml is not null) File.WriteAllText(Path.Combine(dir, ".rollforward.yml"), yaml);
            return RollforwardConfigLoader.Load(dir);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MinCoverage_DefaultsTo50_WhenNoConfigOrKey() =>
        Assert.All([LoadYaml(null), LoadYaml("target: next-lts\n")], c => Assert.Equal(50, c.MinCoverage));

    [Fact]
    public void MinCoverage_IsReadFromTheCamelCaseKey() =>
        Assert.Equal(80, LoadYaml("minCoverage: 80\n").MinCoverage);

    [Fact]
    public void Chain_IsOffByDefault_AndReadFromTheChainKey()
    {
        Assert.False(LoadYaml(null).Chain);
        Assert.True(LoadYaml("chain: true\n").Chain);
    }

    [Fact]
    public void BumpPackages_IsOffByDefault_AndReadFromTheBumpPackagesKey()
    {
        Assert.False(LoadYaml(null).BumpPackages);
        Assert.True(LoadYaml("bumpPackages: true\n").BumpPackages);
    }
}
