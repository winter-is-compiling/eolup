namespace SampleApp;

public class Greeter
{
    public string Greet(string name) => SampleLib.Punctuation.Exclaim($"Hello, {name}");
}
