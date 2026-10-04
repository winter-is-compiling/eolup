namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        Assert.Equal("Hello, World!", new Greeter().Greet("World"));
    }

    // Passes on net8.0, fails on net10.0: a stand-in for the failure a stale framework-aligned
    // package causes after the bump (SampleApp references Microsoft.Extensions.Options 8.0.0).
    // The verdict must name that package as a likely cause.
    [Fact]
    public void Runtime_IsOlderThanDotNet10()
    {
        Assert.True(Environment.Version.Major < 10, $"Running on .NET {Environment.Version.Major}.");
    }
}
