using SampleApp;

namespace GoodTests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        var greeter = new Greeter();
        Assert.Equal("Hello, World!", greeter.Greet("World"));
    }
}
