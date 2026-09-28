using System.Text.Json;
using System.Text.Json.Serialization;
using Rollforward.Core.Models;

namespace Rollforward.Core.Eol;

/// <summary>
/// IEolClient backed by the free, community-maintained endoflife.date API.
/// </summary>
public sealed class EndOfLifeDateClient : IEolClient
{
    private readonly HttpClient _httpClient;

    public EndOfLifeDateClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri("https://endoflife.date/api/") };
    }

    public async Task<IReadOnlyList<EolInfo>> GetCyclesAsync(string product, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"{product}.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var entries = await JsonSerializer.DeserializeAsync<List<EolDateEntry>>(stream, JsonOptions, cancellationToken)
            ?? [];

        var latestCycle = entries.Count > 0 ? entries[0].Cycle : null;

        return entries
            .Select(e => new EolInfo(
                Cycle: e.Cycle,
                ReleaseDate: ParseDate(e.ReleaseDate),
                EolDate: ParseEol(e.Eol),
                IsLts: e.Lts,
                IsLatest: e.Cycle == latestCycle))
            .ToList();
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, out var date) ? date : null;

    // endoflife.date represents "no announced EOL yet" as a JSON `false` rather than
    // a date string on some products, hence the JsonElement handling below.
    private static DateOnly? ParseEol(JsonElement? element)
    {
        if (element is null) return null;
        if (element.Value.ValueKind == JsonValueKind.String && DateOnly.TryParse(element.Value.GetString(), out var date))
            return date;
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class EolDateEntry
    {
        [JsonPropertyName("cycle")]
        public string Cycle { get; set; } = "";

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("eol")]
        public JsonElement? Eol { get; set; }

        [JsonPropertyName("lts")]
        public bool Lts { get; set; }
    }
}
