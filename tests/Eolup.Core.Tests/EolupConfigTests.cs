using Eolup.Core.Config;
using Xunit;

namespace Eolup.Core.Tests;

public class EolupConfigTests
{
    private static EolupConfig LoadYaml(string? yaml)
    {
        var dir = Directory.CreateTempSubdirectory("eolup-config-").FullName;
        try
        {
            if (yaml is not null) File.WriteAllText(Path.Combine(dir, ".eolup.yml"), yaml);
            return EolupConfigLoader.Load(dir);
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

    [Fact]
    public void Timeouts_DefaultTo30MinutesForBuildsAnd60ForTests_AndAreReadFromTheirKeys()
    {
        var defaults = LoadYaml(null);
        Assert.Equal(30, defaults.BuildTimeoutMinutes);
        Assert.Equal(60, defaults.TestTimeoutMinutes);

        var set = LoadYaml("buildTimeoutMinutes: 45\ntestTimeoutMinutes: 120\n");
        Assert.Equal(45, set.BuildTimeoutMinutes);
        Assert.Equal(120, set.TestTimeoutMinutes);

        // One key at a time: the other keeps its default.
        Assert.Equal(60, LoadYaml("buildTimeoutMinutes: 45\n").TestTimeoutMinutes);
    }

    [Theory]
    [InlineData("buildTimeoutMinutes: 0\n", "buildTimeoutMinutes")]
    [InlineData("testTimeoutMinutes: -5\n", "testTimeoutMinutes")]
    public void ATimeoutBelowOneMinute_IsRefusedWithTheKeyNamed(string yaml, string key)
    {
        // A zero limit would stop every build or test run the moment it starts.
        var error = Assert.Throws<EolupUserException>(() => LoadYaml(yaml));

        Assert.Contains(key, error.Message);
        Assert.Contains(".eolup.yml", error.Message);
    }

    private static EolupConfig LoadFiles(params (string Name, string Yaml)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("eolup-config-").FullName;
        try
        {
            foreach (var (name, yaml) in files)
                File.WriteAllText(Path.Combine(dir, name), yaml);
            return EolupConfigLoader.Load(dir);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheLegacyFileNameFromBeforeTheRename_StillWorks()
    {
        // The project used to be called Rollforward; a repo that already has .rollforward.yml must not break.
        var config = LoadFiles((".rollforward.yml", "minCoverage: 80\nchain: true\n"));

        Assert.Equal(80, config.MinCoverage);
        Assert.True(config.Chain);
    }

    [Fact]
    public void TheNewFileNameWins_WhenBothExist()
    {
        var config = LoadFiles((".rollforward.yml", "minCoverage: 10\n"), (".eolup.yml", "minCoverage: 90\n"));

        Assert.Equal(90, config.MinCoverage);
    }
}
