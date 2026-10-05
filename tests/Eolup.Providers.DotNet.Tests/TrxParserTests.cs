using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class TrxParserTests
{
    private static string Trx(params (string Name, string Outcome)[] results) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
        string.Concat(results.Select(r => $"<UnitTestResult testName=\"{r.Name}\" outcome=\"{r.Outcome}\" />")) +
        "</Results></TestRun>";

    [Fact]
    public void CollectsFailedTestNames_AcrossSeveralFiles()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.trx", Trx(("A.Passes", "Passed"), ("A.Breaks", "Failed")));
        var b = dir.Write("b.trx", Trx(("B.Breaks", "Failed"), ("B.Skipped", "NotExecuted")));

        var (any, failed) = TrxParser.Read([a, b]);

        Assert.True(any);
        Assert.Equal(["A.Breaks", "B.Breaks"], failed);
    }

    [Fact]
    public void AnAllPassingRun_HasResultsButNoFailures()
    {
        using var dir = new TempDir();
        var (any, failed) = TrxParser.Read([dir.Write("a.trx", Trx(("A.Passes", "Passed")))]);

        Assert.True(any);
        Assert.Empty(failed);
    }

    [Fact]
    public void NoFilesOrNoResults_MeansNothingCanBeCompared()
    {
        // e.g. the test host couldn't start because the runtime isn't installed.
        using var dir = new TempDir();
        Assert.False(TrxParser.Read([]).AnyResults);
        Assert.False(TrxParser.Read([dir.Write("empty.trx", Trx())]).AnyResults);
    }

    [Fact]
    public void AMalformedFileIsIgnored_RatherThanCrashingTheRun()
    {
        using var dir = new TempDir();
        var broken = dir.Write("broken.trx", "<TestRun><Results><UnitTestRes");
        var good = dir.Write("good.trx", Trx(("A.Breaks", "Failed")));

        Assert.Equal(["A.Breaks"], TrxParser.Read([broken, good]).Failed);
    }
}
