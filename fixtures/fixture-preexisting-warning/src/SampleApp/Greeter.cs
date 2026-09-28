namespace SampleApp;

// References EntityFramework 6.1.3 (net45-only, no netstandard support), which
// deterministically triggers a real NU1701 compatibility warning on build —
// identically on net8.0 (before) and net10.0 (after), since the package is
// equally incompatible with both. That's deliberate: this fixture proves
// Rollforward's baseline-diffing correctly ignores warnings that already existed
// before the migration, rather than misattributing pre-existing tech debt to
// this particular version bump. See DotNetLanguageProvider.RemediateAsync.
public class Greeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
