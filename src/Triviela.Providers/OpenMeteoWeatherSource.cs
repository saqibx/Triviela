using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Triviela.Domain;

namespace Triviela.Providers;

public sealed class OpenMeteoWeatherSource(HttpClient http, ILogger<OpenMeteoWeatherSource> logger) : IWeatherSource
{
    public async Task<Weather?> GetWeatherAsync(double latitude, double longitude, CancellationToken ct)
    {
        var lat = latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var url = $"v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,precipitation,wind_speed_10m,weather_code&wind_speed_unit=kmh";

        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Open-Meteo returned {Status}", (int)resp.StatusCode);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("current", out var cur)) return null;

            double temp = cur.TryGetProperty("temperature_2m", out var t) ? t.GetDouble() : 0;
            double wind = cur.TryGetProperty("wind_speed_10m", out var w) ? w.GetDouble() : 0;
            double precip = cur.TryGetProperty("precipitation", out var p) ? p.GetDouble() : 0;
            int? humidity = cur.TryGetProperty("relative_humidity_2m", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null;
            int code = cur.TryGetProperty("weather_code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;

            return new Weather(temp, wind, precip, humidity, DescribeCode(code));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Open-Meteo lookup failed");
            return null;
        }
    }

    public async Task<(double Latitude, double Longitude)?> GeocodeAsync(string place, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(place)) return null;

        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(place)}&count=1&language=en&format=json";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Open-Meteo geocoding returned {Status}", (int)resp.StatusCode);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;
            var first = results[0];
            if (first.TryGetProperty("latitude", out var lat) && first.TryGetProperty("longitude", out var lon))
                return (lat.GetDouble(), lon.GetDouble());
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Open-Meteo geocoding failed for {Place}", place);
            return null;
        }
    }

    private static string DescribeCode(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 67 => "Drizzle",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        >= 95 => "Thunderstorm",
        _ => "—"
    };
}
