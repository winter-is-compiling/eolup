using Eolup.Core.Models;

namespace Eolup.Core.Eol;

/// <summary>
/// Source of version/EOL data for a product (e.g. "dotnet", "angular").
/// Eolup deliberately does not maintain this data itself — see
/// ARCHITECTURE.md, "External data dependency".
/// </summary>
public interface IEolClient
{
    Task<IReadOnlyList<EolInfo>> GetCyclesAsync(string product, CancellationToken cancellationToken = default);
}
