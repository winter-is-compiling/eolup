namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        Assert.Equal("Hello, World!", new Greeter().Greet("World"));
    }

    // Passes on net6.0 and net8.0, fails on net10.0: a stand-in for a behavioural
    // change that only the second hop of the path meets. A chain must take the first
    // hop, stop at the second, and publish only the first.
    [Fact]
    public void Runtime_IsOlderThanDotNet10()
    {
        Assert.True(Environment.Version.Major < 10, $"Running on .NET {Environment.Version.Major}.");
    }
}
