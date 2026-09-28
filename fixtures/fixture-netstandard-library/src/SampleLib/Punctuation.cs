// A shared library that deliberately stays on netstandard2.1 — usable from any
// modern .NET, so Rollforward must neither refuse the repo because of it nor
// "upgrade" it (that would stop older consumers from referencing it).
// (Block-scoped namespace: netstandard2.1 defaults to C# 8.)
namespace SampleLib
{
    public static class Punctuation
    {
        public static string Exclaim(string text) => text + "!";
    }
}
