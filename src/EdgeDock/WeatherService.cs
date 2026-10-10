using System.Net.Http;

namespace EdgeDock;

/// <summary>
/// Fetches weather from Open-Meteo. The only data sent is the chosen place's coordinates (or
/// the search text while choosing one). Forecasts are shared between widget instances and
/// reused for 15 minutes, so several clock widgets or reloads do not multiply requests.
/// </summary>
internal static class WeatherService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string Key, DateTime FetchedAt, WeatherSnapshot Snapshot)? _cache;

    static WeatherService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("EdgeDock/1.0 (+https://github.com/Clinteastman/edgedock)");

    /// <summary>The forecast, from cache if recent. Null when offline or the service fails.</summary>
    public static async Task<(WeatherSnapshot? Snapshot, DateTime FetchedAt)> GetAsync(WeatherLocation location, bool fahrenheit)
    {
        var url = WeatherForecast.ForecastUrl(location, fahrenheit);
        await Gate.WaitAsync();
        try
        {
            if (_cache is { } cached && cached.Key == url && DateTime.UtcNow - cached.FetchedAt < RefreshInterval)
                return (cached.Snapshot, cached.FetchedAt);
            var json = await Http.GetStringAsync(url);
            // Hourly times are the place's local time; compare against that, not this PC's zone.
            var snapshot = WeatherForecast.Parse(json, LocalTimeAt(json));
            if (snapshot is null) return (null, default);
            _cache = (url, DateTime.UtcNow, snapshot);
            return (snapshot, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return _cache is { } stale && stale.Key == url ? (stale.Snapshot, stale.FetchedAt) : (null, default);
        }
        finally { Gate.Release(); }
    }

    public static async Task<IReadOnlyList<WeatherLocation>> SearchAsync(string place)
    {
        if (string.IsNullOrWhiteSpace(place)) return [];
        try { return WeatherForecast.ParseSearch(await Http.GetStringAsync(WeatherForecast.SearchUrl(place))); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return []; }
    }

    private static DateTime LocalTimeAt(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var offset = document.RootElement.GetProperty("utc_offset_seconds").GetInt32();
            return DateTime.UtcNow.AddSeconds(offset);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return DateTime.Now;
        }
    }
}
