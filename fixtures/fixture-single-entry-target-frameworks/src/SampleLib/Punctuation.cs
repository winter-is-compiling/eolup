// A library whose <TargetFrameworks> lists a single entry: the shape of every project in Prowlarr
// and of many in MonoGame and workflow-core. The element is plural, so a rewrite that only looks for
// the singular <TargetFramework> finds nothing to change. A migration moves the entry (net8.0 ->
// net10.0) and keeps the plural form.
namespace SampleLib;

public static class Punctuation
{
    public static string Exclaim(string text) => text + "!";
}
