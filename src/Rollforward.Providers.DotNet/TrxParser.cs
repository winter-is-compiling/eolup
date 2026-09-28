using System.Xml.Linq;

namespace Rollforward.Providers.DotNet;

/// <summary>
/// Reads the TRX files `dotnet test --logger trx` writes (one per test project).
/// Only the per-test outcomes matter here: which tests failed, so a failing run on
/// the migrated code can be compared with one on the untouched code.
/// </summary>
internal static class TrxParser
{
    /// <summary>Per-test results found across the given TRX files.</summary>
    /// <returns>
    /// Whether any per-test result was present at all (false when the test host
    /// never got far enough to run a test), and the names of the tests that failed.
    /// </returns>
    public static (bool AnyResults, IReadOnlySet<string> Failed) Read(IEnumerable<string> trxFiles)
    {
        var any = false;
        var failed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in trxFiles)
        {
            XDocument document;
            try { document = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; } // a half-written file from a crashed run tells us nothing

            // Namespace-agnostic: element names are stable, the schema URI is an implementation detail.
            foreach (var result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                any = true;
                if (string.Equals((string?)result.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase) &&
                    (string?)result.Attribute("testName") is { Length: > 0 } name)
                {
                    failed.Add(name);
                }
            }
        }

        return (any, failed);
    }
}
