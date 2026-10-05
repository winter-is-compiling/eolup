namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        Assert.Equal("Hello, World!", new Greeter().Greet("World"));
    }

    // Passes on net8.0 and fails once the project runs on net10.0: a stand-in for a
    // genuine behavioural change between runtimes. This is the failure Eolup
    // must call a likely regression, by name.
    [Fact]
    public void Runtime_IsDotNet8()
    {
        Assert.Equal(8, Environment.Version.Major);
    }
}
