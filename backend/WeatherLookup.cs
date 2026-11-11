using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HanekawaPet.Backend;

// A current-temperature question needs timestamped weather data, not an arbitrary web snippet.
public sealed class WeatherLookup(HttpClient http)
{
    public async Task<WebSearchResult?> Search(string query, CancellationToken ct)
    {
        if (!Regex.IsMatch(query, "天气|气温|温度|预报") || Environment.GetEnvironmentVariable("HANEKAWA_WEATHER_DISABLED") == "1") return null;
        var subject = Regex.Replace(query, @"(?:和|及|的)(?=天气|气温|温度|预报)|是(?=多少|几度)", "");
        var city = Regex.Replace(subject, @"今天|明天|后天|今日|明日|现在|当前|实时|天气|气温|温度|预报|怎么样|怎样|如何|多少度|多少|几度|[？?。！，,\s]", "");
        if (!Regex.IsMatch(city, @"^[\u4e00-\u9fffA-Za-z·-]{2,30}$")) return null;
        try
        {
            var geoEndpoint = Environment.GetEnvironmentVariable("HANEKAWA_WEATHER_GEOCODING_ENDPOINT") ?? "https://geocoding-api.open-meteo.com/v1/search";
            using var geo = await GetJson(geoEndpoint + "?name=" + Uri.EscapeDataString(city) + "&count=3&language=zh&format=json", ct);
            if (!geo.RootElement.TryGetProperty("results", out var locations) || locations.GetArrayLength() == 0) return null;
            var location = locations[0];
            var name = location.GetProperty("name").GetString() ?? city;
            var latitude = location.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
            var longitude = location.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
            var endpoint = Environment.GetEnvironmentVariable("HANEKAWA_WEATHER_FORECAST_ENDPOINT") ?? "https://api.open-meteo.com/v1/forecast";
            var url = endpoint + "?latitude=" + latitude + "&longitude=" + longitude + "&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_max,temperature_2m_min,weather_code&temperature_unit=celsius&timezone=auto&forecast_days=3";
            using var forecast = await GetJson(url, ct);
            var root = forecast.RootElement;
            var current = root.GetProperty("current");
            var offset = TimeSpan.FromSeconds(root.GetProperty("utc_offset_seconds").GetInt32());
            var time = DateTime.Parse(current.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
            var observed = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), offset);
            if (Math.Abs((DateTimeOffset.UtcNow - observed.ToUniversalTime()).TotalHours) > 3) return null;
            var country = location.TryGetProperty("country", out var countryValue) ? countryValue.GetString() : "";
            var text = $"地点：{name}，{country}；数据时间：{observed:yyyy-MM-dd HH:mm zzz}。Open-Meteo 当前天气数据（天气模型估计，并非气象站实测）：{Describe(current.GetProperty("weather_code").GetInt32())}；气温 {current.GetProperty("temperature_2m")}℃；体感 {current.GetProperty("apparent_temperature")}℃。";
            if (root.TryGetProperty("daily", out var daily))
            {
                var dates = daily.GetProperty("time"); var highs = daily.GetProperty("temperature_2m_max"); var lows = daily.GetProperty("temperature_2m_min");
                for (var i = 0; i < Math.Min(3, dates.GetArrayLength()); i++)
                    text += $"\n{dates[i].GetString()} 预报：{Describe(daily.GetProperty("weather_code")[i].GetInt32())}，最低 {lows[i]}℃，最高 {highs[i]}℃。";
            }
            return new(query, [new(name + " 天气与温度 · Open-Meteo", url, text)], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException) { return null; }
    }

    private async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 128 * 1024) throw new JsonException("Weather response too large.");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static string Describe(int code) => code switch
    {
        0 => "晴", 1 => "大部晴朗", 2 => "多云", 3 => "阴", 45 or 48 => "雾",
        >= 51 and <= 57 => "毛毛雨", >= 61 and <= 67 => "雨", >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨", 85 or 86 => "阵雪", >= 95 => "雷暴", _ => "天气代码 " + code
    };
}
