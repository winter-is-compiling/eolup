namespace SampleApp;

// Excluded from net8.0 (see SampleApp.csproj) and included only once the project
// targets net10.0 — simulating an obsolete-API usage that only becomes a problem
// *because of* the migration, not one that already existed. This is what lets
// the fixture suite prove Eolup's baseline-diffing actually works: a marker
// present in both the before and after build should never count as "new", but a
// marker that only appears after the bump genuinely should.
public class NewApiUsage
{
    [Obsolete("Use GreetFormally instead.")]
    public string Greet(string name) => $"Hi {name}!";

    public string GreetDefault(string name) => Greet(name);
}
