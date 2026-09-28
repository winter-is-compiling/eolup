namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        Assert.Equal("Hello, World!", new Greeter().Greet("World"));
    }

    // Already failing on the untouched code, the way an integration test that needs
    // a database or a message broker fails on a machine that has neither. It says
    // nothing about the migration and must not be blamed on it.
    [Fact]
    public void Checkout_NeedsADatabase()
    {
        Assert.True(false, "No database is available in this environment.");
    }
}
