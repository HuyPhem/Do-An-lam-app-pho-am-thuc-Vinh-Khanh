using System.Net.Http;
using System.Text.Json;
using TourGuideApp2.Models;

namespace TourGuideApp2.Services;

public static class PlaceApiService
{
    public const string PoiApiUrlPreferenceKey = "PoiApiUrl";
    public const string PoiApiKeyPreferenceKey = "PoiApiKey";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonDefault = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Supabase/PostgREST thường trả cột snake_case (<c>vietnamese_audio_text</c>, …).</summary>
    private static readonly JsonSerializerOptions JsonSnake = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Trả về <c>null</c> khi chưa cấu hình URL, lỗi mạng, hoặc payload rỗng — caller nên đọc SQLite cục bộ (<c>VinhKhanh.db</c>).
    /// </summary>
    public static string GetEffectiveApiUrl()
    {
        // Ưu tiên URL hard-code trong AppConfig để tránh kẹt Preferences cũ (thường là localhost).
        var configUrl = AppConfig.DefaultPoiApiUrl.Trim();
        if (!string.IsNullOrWhiteSpace(configUrl))
            return configUrl;

        var apiUrl = Preferences.Default.Get(PoiApiUrlPreferenceKey, string.Empty)?.Trim() ?? string.Empty;
        if (apiUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            apiUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return apiUrl;
    }

    public static bool HasRemoteApiConfigured()
        => !string.IsNullOrWhiteSpace(GetEffectiveApiUrl());

    public static async Task<List<Place>?> TryGetRemotePlacesAsync()
    {
        var apiUrl = GetEffectiveApiUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            var apiKey = Preferences.Default.Get(PoiApiKeyPreferenceKey, string.Empty)?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = AppConfig.DefaultPoiApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var apiItems = ParseApiItems(json);
            if (apiItems.Count == 0)
                return null;

            var mapped = apiItems
                .Select(MapApiPoiToPlace)
                .Where(x => x is not null)
                .Cast<Place>()
                .ToList();

            return mapped.Count > 0 ? mapped : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<Place>> GetPlacesAsync()
    {
        var remote = await TryGetRemotePlacesAsync();
        if (remote is { Count: > 0 })
            return remote;

        var local = await PlaceLocalRepository.TryLoadAsync();
        return local.Places.Count > 0 ? local.Places : [];
    }

    private static List<PoiApiItem> ParseApiItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return ParsePoiArray(root);

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "places", "Places", "data", "Data" })
                {
                    if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        return ParsePoiArray(arr);
                }
            }

            return [];
        }
        catch
        {
            return [];
        }
    }

    private static List<PoiApiItem> ParsePoiArray(JsonElement arrayElement)
    {
        var list = new List<PoiApiItem>();
        foreach (var el in arrayElement.EnumerateArray())
        {
            var item = DeserializePoiItem(el);
            if (item is not null)
                list.Add(item);
        }
        return list;
    }

    private static PoiApiItem? DeserializePoiItem(JsonElement el)
    {
        var raw = el.GetRawText();
        var snake = JsonSerializer.Deserialize<PoiApiItem>(raw, JsonSnake);
        if (IsUsablePoiItem(snake))
            return snake;

        var fallback = JsonSerializer.Deserialize<PoiApiItem>(raw, JsonDefault);
        return IsUsablePoiItem(fallback) ? fallback : snake ?? fallback;
    }

    private static bool IsUsablePoiItem(PoiApiItem? i)
    {
        if (i is null) return false;
        if (!string.IsNullOrWhiteSpace(i.Name))
            return true;
        return i.Latitude != 0 || i.Longitude != 0;
    }

    private static Place? MapApiPoiToPlace(PoiApiItem dto)
    {
        if (dto is null) return null;

        return new Place
        {
            Name = dto.Name ?? "POI",
            Address = dto.Address ?? string.Empty,
            Specialty = dto.Specialty ?? string.Empty,
            ImageUrl = dto.ImageUrl ?? string.Empty,
            MapUrl = dto.MapUrl ?? string.Empty,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            Description = dto.Description ?? string.Empty,
            VietnameseAudioText = dto.VietnameseAudioText ?? string.Empty,
            EnglishAudioText = dto.EnglishAudioText ?? string.Empty,
            ChineseAudioText = dto.ChineseAudioText ?? string.Empty,
            JapaneseAudioText = dto.JapaneseAudioText ?? string.Empty,
            KoreanAudioText = dto.KoreanAudioText ?? string.Empty,
            ActivationRadiusMeters = dto.ActivationRadiusMeters > 0 ? dto.ActivationRadiusMeters : 35,
            Priority = dto.Priority
        };
    }

    private sealed class PoiApiItem
    {
        public string? Name { get; set; }
        public string? Address { get; set; }
        public string? Specialty { get; set; }
        public string? ImageUrl { get; set; }
        public string? MapUrl { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? Description { get; set; }
        public string? VietnameseAudioText { get; set; }
        public string? EnglishAudioText { get; set; }
        public string? ChineseAudioText { get; set; }
        public string? JapaneseAudioText { get; set; }
        public string? KoreanAudioText { get; set; }
        public double ActivationRadiusMeters { get; set; }
        public int Priority { get; set; }
    }
}
