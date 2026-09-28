using Rollforward.Core.Eol;
using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

public class EolEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 9, 14);

    private static readonly List<EolInfo> Cycles =
    [
        new("6.0", new DateOnly(2021, 11, 8), new DateOnly(2024, 11, 12), true, false),
        new("8.0", new DateOnly(2023, 11, 14), new DateOnly(2026, 11, 10), true, false),
        new("9.0", new DateOnly(2024, 11, 12), new DateOnly(2026, 5, 12), false, false),
        new("10", new DateOnly(2025, 11, 11), new DateOnly(2028, 11, 14), true, true),
    ];

    [Fact]
    public void PastEolVersion_IsReportedAsPastEol()
    {
        var (status, _, days) = EolEvaluator.Evaluate(Cycles, "net6.0", Today);

        Assert.Equal(EolStatus.PastEol, status);
        Assert.True(days < 0);
    }

    [Fact]
    public void VersionWithinThreshold_IsApproachingEol()
    {
        // net8.0 EOLs 2026-11-10; "today" is 2026-09-14 => 57 days out, inside the 90-day window.
        var (status, eolDate, days) = EolEvaluator.Evaluate(Cycles, "net8.0", Today);

        Assert.Equal(EolStatus.ApproachingEol, status);
        Assert.Equal(new DateOnly(2026, 11, 10), eolDate);
        Assert.Equal(57, days);
    }

    [Fact]
    public void VersionFarFromEol_IsCurrent()
    {
        var (status, _, _) = EolEvaluator.Evaluate(Cycles, "net10.0", Today);

        Assert.Equal(EolStatus.Current, status);
    }

    [Fact]
    public void UnknownVersion_IsReportedAsUnknown_NotThrown()
    {
        var (status, eolDate, days) = EolEvaluator.Evaluate(Cycles, "net99.0", Today);

        Assert.Equal(EolStatus.Unknown, status);
        Assert.Null(eolDate);
        Assert.Null(days);
    }

    [Theory]
    [InlineData("net8.0", "8.0")]
    [InlineData("8.0.4", "8.0.4")]
    [InlineData("v10", "10")]
    public void ExtractVersionNumber_PullsLeadingNumericVersion(string input, string expected)
    {
        Assert.Equal(expected, EolEvaluator.ExtractVersionNumber(input));
    }
}
