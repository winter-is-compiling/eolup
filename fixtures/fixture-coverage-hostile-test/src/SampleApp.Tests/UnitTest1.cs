namespace SampleApp.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_ReturnsGreeting()
    {
        Assert.Equal("Hello, World!", new Greeter().Greet("World"));
    }

    // Passes on a plain run, fails when the assembly has been coverage-instrumented
    // (Coverlet injects a tracker type). Architecture tests that reflect over their
    // own assemblies behave this way in real repos (found via Equinox): a
    // measurement side-effect must never be reported as a migration regression.
    [Fact]
    public void Assembly_ContainsOnlyTypesWeWrote()
    {
        var injected = typeof(Greeter).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Coverlet", StringComparison.Ordinal) == true);
        Assert.Empty(injected);
    }
}
