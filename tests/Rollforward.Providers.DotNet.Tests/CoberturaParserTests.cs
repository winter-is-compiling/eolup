using Xunit;

namespace Rollforward.Providers.DotNet.Tests;

public class CoberturaParserTests
{
    private static string Report(string root, params (string File, (int Line, int Hits)[] Lines)[] classes)
    {
        var body = string.Concat(classes.Select(c =>
            $"<class name=\"C\" filename=\"{c.File}\"><lines>" +
            string.Concat(c.Lines.Select(l => $"<line number=\"{l.Line}\" hits=\"{l.Hits}\" />")) +
            "</lines></class>"));
        return $"<coverage><sources><source>{root}</source></sources><packages><package><classes>{body}</classes></package></packages></coverage>";
    }

    [Fact]
    public void ReportsPerFileLineCoverage_RelativeToTheProjectRoot()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "src", "App", "Greeter.cs");
        var xml = dir.Write("r/coverage.cobertura.xml", Report(dir.Path, (src, [(1, 1), (2, 0), (3, 4), (4, 0)])));

        var report = CoberturaParser.Merge([xml], dir.Path, []);

        var file = Assert.Single(report!.Files);
        Assert.Equal("src/App/Greeter.cs", file.File);
        Assert.Equal(2, file.CoveredLines);
        Assert.Equal(4, file.CoverableLines);
        Assert.Equal(50, report.Percent);
    }

    [Fact]
    public void MergesOverlappingReports_ALineIsCoveredIfAnyReportHitIt()
    {
        // Two test projects exercising the same library each write their own report.
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "Lib.cs");
        var a = dir.Write("a/coverage.cobertura.xml", Report(dir.Path, (src, [(1, 1), (2, 0)])));
        var b = dir.Write("b/coverage.cobertura.xml", Report(dir.Path, (src, [(1, 0), (2, 3)])));

        var report = CoberturaParser.Merge([a, b], dir.Path, []);

        Assert.Equal(2, report!.CoveredLines);
        Assert.Equal(2, report.CoverableLines);
    }

    [Fact]
    public void ExcludesTestProjectsGeneratedCodeAndFilesOutsideTheRepo()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "src", "App", "Real.cs");
        var test = Path.Combine(dir.Path, "tests", "App.Tests", "RealTests.cs");
        var generated = Path.Combine(dir.Path, "src", "App", "obj", "Debug", "AssemblyInfo.cs");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Other.cs");
        var xml = dir.Write("c/coverage.cobertura.xml", Report(dir.Path,
            (real, [(1, 1)]), (test, [(1, 1)]), (generated, [(1, 1)]), (outside, [(1, 1)])));

        var report = CoberturaParser.Merge([xml], dir.Path, [Path.Combine(dir.Path, "tests", "App.Tests")]);

        Assert.Equal(["src/App/Real.cs"], report!.Files.Select(f => f.File));
    }

    [Fact]
    public void ReturnsNull_WhenThereIsNothingCoverableToJudgeBy()
    {
        using var dir = new TempDir();
        var xml = dir.Write("c/coverage.cobertura.xml", Report(dir.Path, (Path.Combine(dir.Path, "Empty.cs"), [])));

        Assert.Null(CoberturaParser.Merge([xml], dir.Path, []));
        Assert.Null(CoberturaParser.Merge([], dir.Path, []));
    }
}
