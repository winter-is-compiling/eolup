namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void GreetFormally_ReturnsGreeting()
    {
        var greeter = new Greeter();
        Assert.Equal("Good day, World.", greeter.GreetFormally("World"));
    }
}
