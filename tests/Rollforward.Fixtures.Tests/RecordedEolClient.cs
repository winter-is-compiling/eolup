using Rollforward.Core.Eol;
using Rollforward.Core.Models;

namespace Rollforward.Fixtures.Tests;

/// <summary>
/// A frozen snapshot of endoflife.date's .NET data (recorded September 2026), so
/// the fixture verdicts depend only on this repo's code — not on network access,
/// the service being up, or a new .NET release quietly changing what
/// "next-lts" resolves to. The live API is still exercised once, by
/// <see cref="LiveEndOfLifeDateSmokeTests"/>.
/// </summary>
internal sealed class RecordedEolClient : IEolClient
{
    private static readonly IReadOnlyList<EolInfo> DotNet =
    [
        new("10.0", new DateOnly(2025, 11, 11), new DateOnly(2028, 11, 14), IsLts: true, IsLatest: true),
        new("9.0", new DateOnly(2024, 11, 12), new DateOnly(2026, 11, 10), IsLts: false, IsLatest: false),
        new("8.0", new DateOnly(2023, 11, 14), new DateOnly(2026, 11, 10), IsLts: true, IsLatest: false),
        new("7.0", new DateOnly(2022, 11, 8), new DateOnly(2024, 5, 14), IsLts: false, IsLatest: false),
        new("6.0", new DateOnly(2021, 11, 8), new DateOnly(2024, 11, 12), IsLts: true, IsLatest: false),
    ];

    public Task<IReadOnlyList<EolInfo>> GetCyclesAsync(string product, CancellationToken cancellationToken = default) =>
        product == "dotnet"
            ? Task.FromResult(DotNet)
            : throw new InvalidOperationException($"RecordedEolClient only knows 'dotnet', not '{product}'.");
}
