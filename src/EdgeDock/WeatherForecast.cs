using System.Globalization;
using System.Text.Json;

namespace EdgeDock;

/// <summary>A place chosen in Settings for the weather. Only its coordinates are ever sent.</summary>
internal sealed record WeatherLocation(string Name, double Latitude, double Longitude);

internal sealed record WeatherHour(DateTime Time, double Temperature, int Code, bool IsDay);

internal sealed record WeatherSnapshot(
    double Temperature,
    int Code,
    bool IsDay,
    double High,
    double Low,
    IReadOnlyList<WeatherHour> NextHours);

/// <summary>
/// Reading Open-Meteo responses (free, no account or key) and describing WMO weather codes.
/// Kept free of networking and WinUI so the console checks can cover it.
/// </summary>
internal static class WeatherForecast
{
    /// <summary>Coordinates are rounded to four decimals (about 10 m); nothing else identifies the user.</summary>
    public static string ForecastUrl(WeatherLocation location, bool fahrenheit) =>
        FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude:0.####}&longitude={location.Longitude:0.####}") +
        "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day" +
        "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2" +
        "&temperature_unit=" + (fahrenheit ? "fahrenheit" : "celsius");

    public static string SearchUrl(string place) =>
        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(place.Trim())}&count=5&language=en&format=json";

    /// <summary>
    /// Current conditions, today's range and the next few hours after <paramref name="localNow"/>
    /// (times are the place's local time). Null if the response is not usable.
    /// </summary>
    public static WeatherSnapshot? Parse(string json, DateTime localNow, int hours = 6)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var current = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var hourly = root.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().Select(time => DateTime.Parse(time.GetString()!, CultureInfo.InvariantCulture)).ToArray();
            var temperatures = hourly.GetProperty("temperature_2m").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var codes = hourly.GetProperty("weather_code").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            // Real daylight for the place, so a winter evening gets a moon, not a sun.
            var days = hourly.TryGetProperty("is_day", out var dayValues)
                ? dayValues.EnumerateArray().Select(value => value.GetInt32() == 1).ToArray()
                : times.Select(time => time.Hour is >= 7 and < 19).ToArray();
            var thisHour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
            var next = Enumerable.Range(0, new[] { times.Length, temperatures.Length, codes.Length, days.Length }.Min())
                .Where(index => times[index] > thisHour)
                .Take(hours)
                .Select(index => new WeatherHour(times[index], temperatures[index], codes[index], days[index]))
                .ToArray();
            return new WeatherSnapshot(
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.TryGetProperty("is_day", out var isDay) && isDay.GetInt32() == 1,
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                next);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                              or FormatException or IndexOutOfRangeException or ArgumentNullException)
        {
            return null;
        }
    }

    /// <summary>Places matching a search, named "Town, Region, Country".</summary>
    public static IReadOnlyList<WeatherLocation> ParseSearch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results)) return [];
            return results.EnumerateArray().Select(result =>
            {
                var parts = new[] { "name", "admin1", "country" }
                    .Select(key => result.TryGetProperty(key, out var part) ? part.GetString() : null)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Distinct();
                return new WeatherLocation(string.Join(", ", parts), result.GetProperty("latitude").GetDouble(), result.GetProperty("longitude").GetDouble());
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Plain words and an emoji for a WMO weather code.</summary>
    public static (string Text, string Symbol) Describe(int code, bool isDay = true) => code switch
    {
        0 => ("Clear", isDay ? "☀️" : "🌙"),
        1 => ("Mainly clear", isDay ? "🌤️" : "🌙"),
        2 => ("Partly cloudy", isDay ? "⛅" : "☁️"),
        3 => ("Overcast", "☁️"),
        45 or 48 => ("Fog", "🌫️"),
        51 or 53 or 55 => ("Drizzle", "🌦️"),
        56 or 57 => ("Freezing drizzle", "🌧️"),
        61 or 63 => ("Rain", "🌧️"),
        65 => ("Heavy rain", "🌧️"),
        66 or 67 => ("Freezing rain", "🌧️"),
        71 or 73 => ("Snow", "🌨️"),
        75 => ("Heavy snow", "❄️"),
        77 => ("Snow grains", "🌨️"),
        80 or 81 => ("Showers", "🌦️"),
        82 => ("Heavy showers", "🌧️"),
        85 or 86 => ("Snow showers", "🌨️"),
        95 => ("Thunderstorm", "⛈️"),
        96 or 99 => ("Thunderstorm with hail", "⛈️"),
        _ => ("Unknown", "🌡️")
    };

    /// <summary>Whole degrees, avoiding "-0".</summary>
    public static string Degrees(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("0", CultureInfo.CurrentCulture) + "°";
    }
}
