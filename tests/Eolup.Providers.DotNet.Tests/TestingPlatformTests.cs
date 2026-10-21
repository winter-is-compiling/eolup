using Eolup.Providers.DotNet;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public sealed class TestingPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eolup-mtp-" + Guid.NewGuid().ToString("N"));

    public TestingPlatformTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "App"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string Sub => Path.Combine(_root, "src", "App");

    [Fact]
    public void RunnerSelectedInGlobalJson_IsRecognised()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Assert.True(TestingPlatform.IsSelectedByGlobalJson(_root));
    }

    [Fact]
    public void GlobalJsonIsFoundInAParentFolder_LikeTheSdkDoes()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Assert.True(TestingPlatform.IsSelectedByGlobalJson(Sub));
    }

    [Fact]
    public void TheNearestGlobalJsonWins()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        File.WriteAllText(Path.Combine(Sub, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");
        Assert.False(TestingPlatform.IsSelectedByGlobalJson(Sub));
    }

    [Fact]
    public void CommentsTrailingCommasAndCaseDoNotMatter()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"),
            "{\n  // MTP\n  \"test\": { \"runner\": \"microsoft.testing.platform\", },\n}");
        Assert.True(TestingPlatform.IsSelectedByGlobalJson(_root));
    }

    [Theory]
    [InlineData("""{ "sdk": { "version": "10.0.100" } }""")]
    [InlineData("""{ "test": { "runner": "VSTest" } }""")]
    [InlineData("""{ "test": "Microsoft.Testing.Platform" }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("not json at all")]
    public void AnythingElse_IsNotMtp(string json)
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), json);
        Assert.False(TestingPlatform.IsSelectedByGlobalJson(_root));
    }
}
