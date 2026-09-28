using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Rollforward.Core.Config;

public static class RollforwardConfigLoader
{
    private const string FileName = ".rollforward.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Loads .rollforward.yml from the given project directory. Returns the default
    /// config (see RollforwardConfig.Default) if the file is missing — a repo is
    /// never required to have one.
    /// </summary>
    public static RollforwardConfig Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        if (!File.Exists(path))
            return RollforwardConfig.Default;

        var yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
            return RollforwardConfig.Default;

        var config = Deserializer.Deserialize<RollforwardConfig>(yaml);
        return config ?? RollforwardConfig.Default;
    }
}
