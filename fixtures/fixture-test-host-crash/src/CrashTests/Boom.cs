using System.Runtime.CompilerServices;

namespace CrashTests;

internal static class Boom
{
    [ModuleInitializer]
    internal static void Init() => System.Environment.Exit(3);
}
