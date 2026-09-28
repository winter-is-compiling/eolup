// A library that multi-targets a modern .NET version and netstandard (the default
// shape of a published NuGet package). A migration moves only the modern entry
// (net8.0 -> net10.0) and leaves netstandard2.1 exactly as it is.
// (Block-scoped namespace: the netstandard2.1 build defaults to C# 8.)
namespace SampleLib
{
    public static class Punctuation
    {
        public static string Exclaim(string text) => text + "!";
    }
}
