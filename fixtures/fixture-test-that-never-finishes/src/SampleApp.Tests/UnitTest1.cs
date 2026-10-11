namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        // Takes far longer than any time limit a test sets: it stands in for a real suite that is legitimately
        // slow (or hung), so the run is stopped by Eolup's limit instead of finishing.
        Thread.Sleep(TimeSpan.FromMinutes(10));

        var greeter = new Greeter();
        Assert.Equal("Hello, World!", greeter.Greet("World"));
    }
}
