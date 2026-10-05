namespace SampleApp.Tests;

// A test project that compiles but contains no test: `dotnet test` exits 0 with "No test is available",
// writes an empty results file, and the tests-passed answer is meaningless.
public class GreeterTests
{
    public void NotATest()
    {
        var greeter = new Greeter();
        _ = greeter.Greet("World");
    }
}
