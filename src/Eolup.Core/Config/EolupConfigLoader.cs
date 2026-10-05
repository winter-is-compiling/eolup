using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eolup.Core.Config;

public static class EolupConfigLoader
{
    private const string FileName = ".eolup.yml";

    /// <summary>
    /// The file name from before the project was renamed (it was called Rollforward). Still read, so a repo that
    /// already has one keeps working, but only when there is no .eolup.yml: the new name always wins.
    /// </summary>
    private const string LegacyFileName = ".rollforward.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Loads .eolup.yml (or, failing that, the legacy .rollforward.yml) from the given project directory.
    /// Returns the default config (see EolupConfig.Default) if neither exists — a repo is never required to have one.
    /// </summary>
    public static EolupConfig Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        if (!File.Exists(path))
            path = Path.Combine(projectDirectory, LegacyFileName);
        if (!File.Exists(path))
            return EolupConfig.Default;

        var yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
            return EolupConfig.Default;

        var config = Deserializer.Deserialize<EolupConfig>(yaml);
        if (config is null)
            return EolupConfig.Default;

        // A zero or negative limit would stop every build or test run the moment it starts.
        RequireAtLeastOneMinute(config.BuildTimeoutMinutes, "buildTimeoutMinutes", path);
        RequireAtLeastOneMinute(config.TestTimeoutMinutes, "testTimeoutMinutes", path);
        return config;
    }

    private static void RequireAtLeastOneMinute(int minutes, string key, string path)
    {
        if (minutes < 1)
            throw new EolupUserException(
                $"`{key}` in {Path.GetFileName(path)} must be at least 1 (a number of minutes), but it is {minutes}.");
    }
}
