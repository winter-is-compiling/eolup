using Eolup.Core.Eol;
using Xunit;

namespace Eolup.Fixtures.Tests;

/// <summary>
/// The one test that talks to the real endoflife.date. Everything else uses
/// <see cref="RecordedEolClient"/>; this exists to notice if the live API's shape
/// drifts away from what <see cref="EndOfLifeDateClient"/> parses. Excluded from
/// offline runs with: dotnet test --filter "Category!=Live"
/// </summary>
[Trait("Category", "Live")]
public class LiveEndOfLifeDateSmokeTests
{
    [Fact]
    public async Task RealApi_ReturnsParseableDotNetCycles()
    {
        var cycles = await new EndOfLifeDateClient().GetCyclesAsync("dotnet");

        Assert.NotEmpty(cycles);
        Assert.Contains(cycles, c => c.Cycle.StartsWith("8"));
        Assert.Contains(cycles, c => c.IsLts);
        Assert.Single(cycles, c => c.IsLatest);
    }
}
