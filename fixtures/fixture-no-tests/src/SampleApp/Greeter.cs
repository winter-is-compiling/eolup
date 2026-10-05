namespace SampleApp;

// Deliberately no test project anywhere in this fixture — exercises Eolup's
// "blocked" path, which fires whenever a change cannot be safely verified.
public class Greeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
