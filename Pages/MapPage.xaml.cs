using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TourGuideApp2.Models;
using TourGuideApp2.Services;
using QRCoder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
namespace TourGuideApp2;

public partial class MapPage : ContentPage
{
    private List<Place> _pois = [];
    private bool _suppressQrPickerEvent;
    private bool _suppressSimulatePoiPickerEvent;
    private readonly Dictionary<int, DateTime> _autoGeoLastLogByPoi = [];
    private const int AutoGeoLogCooldownSeconds = 60;
    /// <summary>Chống phát lặp cùng POI quá sớm (ra/vào vùng hoặc GPS giật) — khác với cooldown chỉ ghi log.</summary>
    private readonly Dictionary<int, DateTime> _autoGeoNextAllowedPlayUtcByPoi = [];
    private const int AutoGeoSpeechDebounceSeconds = 45;
    /// <summary>Fallback nếu POI chưa có bán kính riêng.</summary>
    private const double DefaultAutoGeoEnterMeters = 35;
    /// <summary>Tỷ lệ bán kính thoát so với bán kính vào để tạo hysteresis (đủ nhỏ để ra khỏi quán là dừng đọc).</summary>
    private const double AutoGeoExitMultiplier = 1.22;
    private const double MinAutoGeoExitMeters = 38;
    private const double AutoGeoMinGapMeters = 8;       // khoảng cách tối thiểu giữa quán gần nhất và quán thứ 2 (tránh nhầm khi hai quán sát nhau)

    /// <summary>POI đang “ở trong vùng” thuyết minh tự động (-1 = không có).</summary>
    private int _activeProximityPoiIndex = -1;
    private CancellationTokenSource? _proximityTtsCts;
    private readonly SemaphoreSlim _proximityCheckGate = new(1, 1);
    private CancellationTokenSource? _busStopTtsCts;
    private string? _activeBusStopToken;
    private const double BusStopEnterMeters = 28;
    private const double BusStopExitMeters = 38;
    /// <summary>GPS foreground: cập nhật tọa độ khi đang mở tab Bản đồ (không chạy nền khi thoát tab).</summary>
    private bool _isForegroundGpsListening;
#if ANDROID
    /// <summary>Một số máy (Samsung + Fake GPS) không bắn <see cref="Geolocation.LocationChanged"/>; poll bổ sung.</summary>
    private CancellationTokenSource? _androidGpsPollCts;
#endif
    /// <summary>Tránh xử lý trùng khi vừa có event vừa có poll.</summary>
    private double _lastQueuedGpsLat = double.NaN;
    private double _lastQueuedGpsLng = double.NaN;
    private const double GpsDuplicateEpsilonDegrees = 1e-7;
    /// <summary>Chỉ nhận điểm GPS có độ chính xác chấp nhận được (m). Null/0 = không có metadata, vẫn cho qua.</summary>
    private const double MaxGpsAccuracyMeters = 55;
    /// <summary>Loại bỏ điểm nhảy bất thường trong khoảng thời gian ngắn (m/s).</summary>
    private const double MaxGpsSpeedMetersPerSecond = 45;
    /// <summary>Chỉ áp speed-filter khi quãng nhảy đủ lớn, tránh false-positive do nhiễu nhỏ.</summary>
    private const double MinDistanceForSpeedFilterMeters = 90;
    /// <summary>Cho phép "teleport" lớn khi test Fake GPS để không bị kẹt tại điểm cũ.</summary>
    private const double AllowLargeJumpMeters = 280;
    private DateTime? _lastAcceptedGpsUtc;
    private string _currentZoneStatus = "Đang xác định vùng...";
    /// <summary>Sau khi bấm mũi tên / xe buýt, tạm không áp GPS để không đè vị trí giả lập.</summary>
    private DateTime? _gpsManualOverrideUntilUtc;
    private const int GpsManualOverrideSeconds = 60;
    private EventHandler<WebNavigatedEventArgs>? _mapNavigatedHandler;
    // Biến cho chế độ giả lập di chuyển
    private double _simulatedLat;
    private double _simulatedLng;
    private bool _hasSimulationPosition;
    private const double MOVE_STEP_METERS = 15; // mét mỗi lần nhấn
    /// <summary>Tọa độ mặc định khu phố ẩm thực Vĩnh Khánh (Q4) — nút &quot;Về khu demo&quot;.</summary>
    private const double DemoVinhKhanhLat = 10.7590;
    private const double DemoVinhKhanhLng = 106.7041;
    private string _selectedLanguage = "vi";

    /// <summary>Ưu tiên POI từ API khi đã cấu hình <c>PoiApiUrl</c> (+ <c>PoiApiKey</c> cho Supabase); không thì đọc <c>VinhKhanh.db</c> cục bộ.</summary>
    private async Task<List<Place>> LoadPlacesAsync()
    {
        var remote = await PlaceApiService.TryGetRemotePlacesAsync();
        if (remote is { Count: > 0 })
            return SanitizePlaces(remote);

        if (PlaceApiService.HasRemoteApiConfigured())
        {
            UpdateGeoStatusLabel("Khong tai duoc API, dang dung du lieu cuc bo");
        }

        return SanitizePlaces(await LoadPlacesFromLocalDatabaseAsync());
    }

    private static List<Place> SanitizePlaces(List<Place> places)
    {
        foreach (var p in places)
        {
            if (p is null) continue;
            p.VietnameseAudioText = CleanupNarrationNoise(p.VietnameseAudioText);
            p.EnglishAudioText = CleanupNarrationNoise(p.EnglishAudioText);
            p.ChineseAudioText = CleanupNarrationNoise(p.ChineseAudioText);
            p.JapaneseAudioText = CleanupNarrationNoise(p.JapaneseAudioText);
        }

        return places;
    }

    /// <summary>Loại tiền tố số rác kiểu "7272727..." trước nội dung thuyết minh.</summary>
    private static string CleanupNarrationNoise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = Regex.Replace(
            text.Trim(),
            @"^\s*(?:\d[\d\s\-_.,;:\|]*){3,}",
            string.Empty,
            RegexOptions.CultureInvariant);

        return cleaned.TrimStart();
    }

    private async Task<List<Place>> LoadPlacesFromLocalDatabaseAsync()
    {
        // false: không xóa DB trên máy mỗi lần mở map. true khi cần ép copy lại VinhKhanh.db từ bản cài.
        const bool forceUpdate = false;
        var result = await PlaceLocalRepository.TryLoadAsync(forceRecopyFromPackage: forceUpdate);

        switch (result.Error)
        {
            case PlaceLocalRepository.LoadError.DbEmptyNoTables:
                await DisplayAlertAsync("Lỗi Database - DB rỗng",
                    "File VinhKhanh.db đã copy nhưng không có bảng nào.\n\n" +
                    "Hãy mở file VinhKhanh.db bằng DB Browser for SQLite để kiểm tra lại.",
                    "OK");
                break;
            case PlaceLocalRepository.LoadError.NoPlaceTable:
                await DisplayAlertAsync("Lỗi Database - Không tìm thấy bảng",
                    "Không có bảng 'Place' hoặc 'Places' trong VinhKhanh.db.",
                    "OK");
                break;
            case PlaceLocalRepository.LoadError.Exception when !string.IsNullOrEmpty(result.Message):
                await DisplayAlertAsync("Lỗi Database",
                    $"Không đọc được VinhKhanh.db:\n{result.Message}", "OK");
                break;
        }

        return result.Places;
    }
    public MapPage()
    {
        InitializeComponent();
        btnCurrentLang.Text = "🇻🇳 VI";
        langOptions.IsVisible = false;
        lblGeoStatus.Text = "📍 Trạng thái: Đang xác định vùng...";
        lblCooldownStatus.Text = "⏳ Cooldown: -";
        lblLastPlayedStatus.Text = "🔊 Đã phát gần nhất: -";
    }
    // ── Mở/đóng thanh chọn ngôn ngữ ──
    private void OnLanguageButtonClicked(object? sender, EventArgs e)
    {
        langOptions.IsVisible = !langOptions.IsVisible;
    }

    // ── Chọn Tiếng Việt ──
    private void OnSelectVietnameseClicked(object? sender, EventArgs e)
    {
        _selectedLanguage = "vi";
        btnCurrentLang.Text = "🇻🇳 VI";
        langOptions.IsVisible = false;
    }

    // ── Chọn English ──
    private void OnSelectEnglishClicked(object? sender, EventArgs e)
    {
        _selectedLanguage = "en";
        btnCurrentLang.Text = "🇬🇧 EN";
        langOptions.IsVisible = false;
    }
    // ── Chọn Tiếng Trung (mới) ──
    private void OnSelectChineseClicked(object? sender, EventArgs e)
    {
        _selectedLanguage = "zh";
        btnCurrentLang.Text = "🇨🇳 ZH";
        langOptions.IsVisible = false;
    }

    // ── Chọn Tiếng Nhật (mới) ──
    private void OnSelectJapaneseClicked(object? sender, EventArgs e)
    {
        _selectedLanguage = "ja";
        btnCurrentLang.Text = "🇯🇵 JA";
        langOptions.IsVisible = false;
    }
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = SafeLoadMapAsync();
        _ = TryStartForegroundGpsListeningAsync();
        _ = PlayWelcomeMessageAsync();
    }

    private async Task SafeLoadMapAsync()
    {
        try
        {
            await LoadMapAsync();
        }
        catch (Exception ex)
        {
            mapView.Source = new HtmlWebViewSource
            {
                Html = $"""
                <html>
                  <body style="font-family:Arial,Helvetica,sans-serif;background:#fafafa;color:#222;padding:16px;">
                    <h3>Khong tai duoc ban do</h3>
                    <p>Vui long mo lai tab Ban do hoac khoi dong lai app.</p>
                    <pre style="white-space:pre-wrap;background:#fff;border:1px solid #ddd;padding:8px;border-radius:8px;">{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>
                  </body>
                </html>
                """
            };
        }
    }

    protected override void OnDisappearing()
    {
        // Không dừng GPS khi chuyển tab — vẫn theo dõi vị trí (kể cả khi gập app, nếu đã cấp quyền nền).
        base.OnDisappearing();
    }

    private async Task PlayWelcomeMessageAsync()
    {
        // Đợi UI/map ổn định một chút để TTS không bị hụt câu khi vừa mở trang.
        await Task.Delay(900);
        _ = await NarrationQueueService.EnqueuePoiOrTtsAsync(-1, "vi",
            "Xin chào, chào mừng bạn đến với phố ẩm thực Vĩnh Khánh.");
    }

    private async Task LoadMapAsync()
    {
        _pois = await LoadPlacesAsync();
        PopulateQrPicker();

        // Vị trí mặc định - khu Vĩnh Khánh, Q4
        double centerLat = DemoVinhKhanhLat;
        double centerLng = DemoVinhKhanhLng;
        var gpsFix = await TryGetCurrentLocationAsync();
        if (gpsFix is not null)
        {
            centerLat = gpsFix.Latitude;
            centerLng = gpsFix.Longitude;
        }

        // Luôn hiện điểm xe buýt demo trên map (không phụ thuộc GPS).
        const bool hasCurrentLocation = true;

        // Chuẩn bị dữ liệu POI
        var poiDtos = new List<object>();
        for (int i = 0; i < _pois.Count; i++)
        {
            var p = _pois[i];
            if (p == null) continue;
            poiDtos.Add(new
            {
                id = i,
                name = p.Name,
                lat = p.Latitude,
                lng = p.Longitude,
                img = p.ImageUrl,
                mapUrl = p.MapUrl,
                viText = p.VietnameseAudioText,
                enText = p.EnglishAudioText,
                zhText = p.ChineseAudioText,
                jaText = p.JapaneseAudioText
            });
        }

        // Tạo mảng JSON thủ công (tránh vấn đề escape)
        var poiJsArray = string.Join(",", poiDtos.Select(x => JsonSerializer.Serialize(x)));
        var hasCurrentLocationJs = hasCurrentLocation ? "true" : "false";
        var busStopDtos = new List<object>
        {
            new { code = "KHANH_HOI", name = "Điểm dừng xe buýt Khánh Hội", lat = 10.7597, lng = 106.7050, poiId = 4 },
            new { code = "VINH_HOI",  name = "Điểm dừng xe buýt Vĩnh Hội",  lat = 10.7586, lng = 106.7036, poiId = 0 },
            new { code = "XOM_CHIEU", name = "Điểm dừng xe buýt Xóm Chiếu", lat = 10.7603, lng = 106.7026, poiId = 1 }
        };
        var busStopJsArray = string.Join(",", busStopDtos.Select(x => JsonSerializer.Serialize(x)));
        var routePoints = await RouteTrackService.GetPointsAsync();
        var routeJsArray = string.Join(",", routePoints.Select(x => JsonSerializer.Serialize(new
        {
            lat = x.Latitude,
            lng = x.Longitude
        })));

        string html = $@"
<!DOCTYPE html>
<html>
<head>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'>
  <style>
    html, body {{
      margin:0;
      padding:0;
      height:100%;
      width:100%;
      font-family: Arial, Helvetica, sans-serif;
    }}
    #map {{
      height: 100vh;
      width: 100%;
    }}
    .poi-icon {{
      width: 44px;
      height: 44px;
      border-radius: 50%;
      background: rgba(255, 107, 0, 0.9);
      border: 3px solid #ffffff;
      box-shadow: 0 2px 6px rgba(0,0,0,0.25);
      display: flex;
      align-items: center;
      justify-content: center;
      overflow: hidden;
    }}
    .poi-icon img {{
      width: 28px;
      height: 28px;
      border-radius: 7px;
      object-fit: cover;
      background: #fff;
    }}
    .leaflet-popup-content-wrapper {{
      border-radius: 16px;
      padding: 0;
      overflow: hidden;
    }}
    .leaflet-popup-content {{
      margin: 0;
      width: 220px;
    }}
    .poi-popup {{
      background: rgba(255,255,255,0.98);
      padding: 12px;
    }}
    .poi-title {{
      font-size: 16px;
      font-weight: 700;
      margin-bottom: 8px;
      color: #FF6B00;
    }}
    .poi-photo {{
      width: 100%;
      height: 120px;
      object-fit: cover;
      border-radius: 10px;
      margin-bottom: 10px;
      background: #f3f3f3;
    }}
    .poi-desc {{
      margin-bottom: 8px;
      font-size: 12px;
      line-height: 1.4;
      color: #2c2c2c;
      max-height: 74px;
      overflow-y: auto;
      padding-right: 2px;
    }}
    .poi-label {{
      display: inline-block;
      font-size: 11px;
      font-weight: 700;
      color: #2456d1;
      margin-right: 6px;
    }}
    .poi-actions {{
      display:flex;
      gap:10px;
      justify-content: flex-start;
    }}
    .poi-btn {{
      background: #ff9999;
      color: #ffffff;
      padding: 10px 12px;
      border-radius: 999px;
      text-decoration: none;
      font-weight: 700;
      font-size: 13px;
      display: inline-block;
      user-select: none;
      white-space: nowrap;
      box-shadow: 0 2px 8px rgba(255, 140, 140, 0.4);
      border: 0;
      cursor: pointer;
    }}
    .poi-btn.secondary {{
      background: #d88aff;
    }}
  </style>
  <link rel=""stylesheet"" href=""https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"" />
  <script src=""https://unpkg.com/leaflet-color-markers@latest/leaflet-color-markers.min.js""></script>
</head>
<body>
  <div id='map'></div>
  <div id='debug' style='position:absolute; top:8px; left:8px; z-index:9999; background:rgba(0,0,0,0.55); color:#fff; padding:8px 10px; border-radius:10px; font-size:12px; max-width:85%;'>
    Loading...
  </div>

  <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
  <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
  <script>
    (function () {{
      var dbg = document.getElementById('debug');
      function setDbg(text) {{
        try {{ if (dbg) dbg.innerText = text; }} catch (_) {{}}
      }}

      function esc(value) {{
        if (value === null || value === undefined) return '';
        return String(value)
          .replace(/&/g, '&amp;')
          .replace(/</g, '&lt;')
          .replace(/>/g, '&gt;')
          .replace(/""/g, '&quot;')
          .replace(/'/g, '&#39;');
      }}

      try {{
        setDbg('JS init...');

        var pois = [{poiJsArray}];
        if (!Array.isArray(pois)) pois = [];
        pois = pois.filter(function(p) {{
          return p && typeof p.lat === 'number' && typeof p.lng === 'number';
        }});

        var map = L.map('map', {{ scrollWheelZoom: true, zoomControl: true }})
          .setView([{centerLat.ToString(CultureInfo.InvariantCulture)}, {centerLng.ToString(CultureInfo.InvariantCulture)}], 16);
        window.appMap = map;
        // ==================== CHẤM ĐEN CỐ ĐỊNH KÍCH THƯỚC (pixel) ====================
        var blackDotIcon = L.divIcon({{
            className: 'custom-black-dot',
            html: `<div style='width:15px;height:15px;background:#000;border-radius:50%;border:3px solid #fff;box-shadow:0 2px 8px rgba(0,0,0,0.4);'></div>`,
            iconSize: [24, 24],
            iconAnchor: [12, 12]
        }});

        window.userMarker = L.marker([{centerLat.ToString(CultureInfo.InvariantCulture)}, {centerLng.ToString(CultureInfo.InvariantCulture)}], {{
            icon: blackDotIcon,
            zIndexOffset: 1000   // luôn nằm trên cùng
        }}).addTo(map);

        window.userMarker.bindPopup('<b>Vị trí của bạn</b><br/><small>GPS khi bật quyền; trong phòng có thể chọn quán (mô phỏng gần quán).</small>');
        var hasCurrentLocation = {hasCurrentLocationJs};
        var currentLat = {centerLat.ToString(CultureInfo.InvariantCulture)};
        var currentLng = {centerLng.ToString(CultureInfo.InvariantCulture)};
        var busStops = [{busStopJsArray}];
        var routePoints = [{routeJsArray}];
        var routePolyline = null;

        window.renderRoutePath = function(points) {{
          if (!Array.isArray(points) || points.length === 0) return false;
          var latlngs = points
            .filter(function(p) {{ return p && typeof p.lat === 'number' && typeof p.lng === 'number'; }})
            .map(function(p) {{ return [p.lat, p.lng]; }});
          if (latlngs.length === 0) return false;

          if (!routePolyline) {{
            routePolyline = L.polyline(latlngs, {{
              color: '#0057D9',
              weight: 4,
              opacity: 0.9
            }}).addTo(map);
          }} else {{
            routePolyline.setLatLngs(latlngs);
          }}
          return true;
        }};

        window.appendRoutePoint = function(lat, lng) {{
          if (typeof lat !== 'number' || typeof lng !== 'number') return false;
          routePoints.push({{ lat: lat, lng: lng }});
          return window.renderRoutePath(routePoints);
        }};

        window.clearRoutePath = function() {{
          routePoints = [];
          if (routePolyline && map) {{
            map.removeLayer(routePolyline);
            routePolyline = null;
          }}
          return true;
        }};

        // Tránh tile.openstreetmap.org vì WebView mobile thường không gửi Referer => bị chặn.
        var tileProviders = [
          {{
            name: 'CartoDB Voyager',
            url: 'https://{{s}}.basemaps.cartocdn.com/rastertiles/voyager/{{z}}/{{x}}/{{y}}{{r}}.png',
            options: {{
              subdomains: 'abcd',
              maxZoom: 20,
              attribution: '&copy; OpenStreetMap contributors &copy; CARTO'
            }}
          }},
          {{
            name: 'OpenStreetMap',
            url: 'https://{{s}}.tile.openstreetmap.org/{{z}}/{{x}}/{{y}}.png',
            options: {{
              maxZoom: 19,
              attribution: '&copy; OpenStreetMap contributors'
            }}
          }}
        ];
        var providerIndex = 0;
        var layer = null;
        var tileErrorCount = 0;

        function switchTileProvider(index) {{
          if (layer) {{
            map.removeLayer(layer);
          }}

          var provider = tileProviders[index];
          layer = L.tileLayer(provider.url, provider.options);
          layer.addTo(map);
          tileErrorCount = 0;
          setDbg('Tile: ' + provider.name + ' | POIs: ' + pois.length);
        }}

        switchTileProvider(providerIndex);

        map.on('tileload', function() {{
          setDbg('POIs: ' + pois.length + ' | map loaded');
        }});
        map.on('tileerror', function() {{
          tileErrorCount++;
          if (tileErrorCount >= 2 && providerIndex < tileProviders.length - 1) {{
            providerIndex++;
            switchTileProvider(providerIndex);
            return;
          }}
          setDbg('Tile error (' + tileErrorCount + ')');
        }});

        // Mục 8: bật GPS thì hiển thị 3 điểm dừng xe buýt (Khánh Hội, Vĩnh Hội, Xóm Chiếu).
        if (hasCurrentLocation && Array.isArray(busStops)) {{
          var busIcon = L.divIcon({{
            className: 'bus-stop-icon',
            html: `<div style='width:30px;height:30px;background:#1976D2;color:#fff;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:16px;border:2px solid #fff;box-shadow:0 2px 6px rgba(0,0,0,0.3);'>🚌</div>`,
            iconSize: [30, 30],
            iconAnchor: [15, 15]
          }});

          busStops.forEach(function(s) {{
            if (!s || typeof s.lat !== 'number' || typeof s.lng !== 'number') return;
            var marker = L.marker([s.lat, s.lng], {{ icon: busIcon, zIndexOffset: 800 }}).addTo(map);
            var popup = `<div style='font-size:13px;line-height:1.4'>`
              + `<b>${{esc(s.name || 'Điểm dừng xe buýt')}}</b><br/>`
              + `Mã QR: <b>${{esc(s.code || '')}}</b><br/>`
              + `<a class='poi-btn secondary' href='app://poi?id=${{s.poiId}}&lang=vi'>Nghe ngay</a>`
              + `</div>`;
            marker.bindPopup(popup);
          }});
        }}
// Biến để lưu circle/marker theo POI id
var circles = [];
window.poiMarkers = {{}};
window.poiCircles = {{}};
window._highlightPoiId = -1;
window.openPoiById = function(id) {{
    var marker = window.poiMarkers[id];
    if (marker && typeof marker.openPopup === 'function') {{
        marker.openPopup();
        if (marker.getLatLng && map && map.panTo) {{
            map.panTo(marker.getLatLng());
        }}
        return true;
    }}
    var circle = window.poiCircles[id];
    if (circle && typeof circle.openPopup === 'function') {{
        circle.openPopup();
        if (circle.getLatLng && map && map.panTo) {{
            map.panTo(circle.getLatLng());
        }}
        return true;
    }}
    return false;
}};

window.setNearestPoiHighlight = function(id) {{
    var previousId = window._highlightPoiId;
    if (typeof previousId === 'number' && previousId >= 0) {{
        var previousMarker = window.poiMarkers[previousId];
        if (previousMarker && window.redPinIcon && previousMarker.setIcon) {{
            previousMarker.setIcon(window.redPinIcon);
        }}
        var previousCircle = window.poiCircles[previousId];
        if (previousCircle && previousCircle.setStyle) {{
            previousCircle.setStyle({{
                color: '#3388ff',
                fillColor: '#3388ff',
                fillOpacity: 0.2,
                weight: 2,
                opacity: 0.7
            }});
        }}
    }}

    if (typeof id !== 'number' || id < 0) {{
        window._highlightPoiId = -1;
        return false;
    }}

    var marker = window.poiMarkers[id];
    if (marker && window.nearestPinIcon && marker.setIcon) {{
        marker.setIcon(window.nearestPinIcon);
    }}

    var circle = window.poiCircles[id];
    if (circle && circle.setStyle) {{
        circle.setStyle({{
            color: '#ff6b00',
            fillColor: '#ffa43a',
            fillOpacity: 0.35,
            weight: 3,
            opacity: 0.95
        }});
    }}

    window._highlightPoiId = id;
    return true;
}};

var redPinIcon = L.icon({{
    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-red.png',
    iconRetinaUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-red.png',
    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34],
    shadowSize: [41, 41]
}});
var nearestPinIcon = L.icon({{
    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-orange.png',
    iconRetinaUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-orange.png',
    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34],
    shadowSize: [41, 41]
}});
window.redPinIcon = redPinIcon;
window.nearestPinIcon = nearestPinIcon;

for (var i = 0; i < pois.length; i++) {{
    var p = pois[i];

    var marker = L.marker([p.lat, p.lng], {{
        icon: redPinIcon
    }}).addTo(map);
    window.poiMarkers[p.id] = marker;

    // 2. Tạo vòng tròn (circle) xung quanh marker
    var circle = L.circle([p.lat, p.lng], {{
        radius: 50,               
        color: '#3388ff',          // viền xanh giống Google Maps
        fillColor: '#3388ff',      // màu nền xanh nhạt
        fillOpacity: 0.2,          // độ mờ thấp để không che khuất quá nhiều
        weight: 2,                 // độ dày viền
        opacity: 0.7
    }}).addTo(map);
    window.poiCircles[p.id] = circle;

    circles.push(circle);  // lưu lại nếu sau này muốn remove/update
// 3. Popup
    var name = (p.name && String(p.name).length > 0) ? String(p.name) : 'POI';
          var viText = (p.viText && String(p.viText).length > 0) ? String(p.viText) : 'Chưa có nội dung tiếng Việt.';
          var enText = (p.enText && String(p.enText).length > 0) ? String(p.enText) : 'English description is not available yet.';
          var zhText = (p.zhText && String(p.zhText).length > 0) ? String(p.zhText) : '暂无中文解说。';
          var jaText = (p.jaText && String(p.jaText).length > 0) ? String(p.jaText) : '日本語の解説はまだありません。';
          var imgFile = (p.img && String(p.img).length > 0) ? String(p.img) : '';
          var photoSrc = '';
          if (imgFile) {{
            var u = imgFile.trim().toLowerCase();
            if (u.indexOf('http://') === 0 || u.indexOf('https://') === 0 || u.indexOf('//') === 0)
              photoSrc = imgFile;
            else
              photoSrc = 'file:///android_asset/' + esc(imgFile);
          }}
          var mapHref = (p.mapUrl && String(p.mapUrl).length > 0) ? String(p.mapUrl) : ('https://www.google.com/maps?q=' + p.lat + ',' + p.lng);
          
          var popupHtml = 
              `<div class='poi-popup'>`
              + `<div class='poi-title'>${{name}}</div>`
              + `${{photoSrc ? `<img class='poi-photo' src='${{photoSrc}}' onerror=""this.style.display='none'"" />` : ''}}`
              + `<div class='poi-desc'><a class='poi-btn secondary' href='`
              + ('app://map?u=' + encodeURIComponent(mapHref))
              + `'>🗺 Bản đồ ngoài</a></div>`
              + `<div class='poi-desc'><span class='poi-label'>VI</span>${{esc(viText)}}</div>`
              + `<div class='poi-desc'><span class='poi-label'>EN</span>${{esc(enText)}}</div>`
              + `<div class='poi-desc'><span class='poi-label'>ZH</span>${{esc(zhText)}}</div>`
              + `<div class='poi-desc'><span class='poi-label'>JA</span>${{esc(jaText)}}</div>`
              + `<div class='poi-actions'>`
              + `<a class='poi-btn' href='app://poi?id=${{p.id}}&lang=vi'>Nghe VN</a>`
              + `<a class='poi-btn' href='app://poi?id=${{p.id}}&lang=zh'>听 ZH</a>`
              + `<a class='poi-btn secondary' href='app://poi?id=${{p.id}}&lang=en'>Listen EN</a>`
              + `<a class='poi-btn secondary' href='app://poi?id=${{p.id}}&lang=ja'>聞く JA</a>`
              + `</div>`
              + `</div>`;

    // Bind popup vào marker
    marker.bindPopup(popupHtml);
    circle.bindPopup(popupHtml);
}}

        if (pois.length === 0) {{
          setDbg('No POIs');
        }} else {{
          window.setNearestPoiHighlight(0);
        }}

        if (Array.isArray(routePoints) && routePoints.length > 0) {{
          window.renderRoutePath(routePoints);
        }}
      }} catch (e) {{
        var msg = (e && e.message) ? e.message : String(e);
        setDbg('JS crash: ' + msg);
      }}
    }})();
  </script>
</body>
</html>";

        mapView.Source = new HtmlWebViewSource { Html = html };

        if (_mapNavigatedHandler is not null)
        {
            mapView.Navigated -= _mapNavigatedHandler;
            _mapNavigatedHandler = null;
        }

        _mapNavigatedHandler = async (_, _) =>
        {
            await Task.Delay(1500); // đợi map ổn định

            // Chỉ set vị trí giả lập lần đầu. Các lần quay lại tab Map giữ nguyên vị trí hiện tại.
            if (!_hasSimulationPosition)
            {
                _simulatedLat = hasCurrentLocation ? centerLat : DemoVinhKhanhLat;
                _simulatedLng = hasCurrentLocation ? centerLng : DemoVinhKhanhLng;
                _hasSimulationPosition = true;
            }

            try
            {
                await SyncUserMarkerPositionOnMapAsync(panToMarker: true);
                await TrackRoutePointAsync("init");
                await CheckProximityAndSpeakAsync();
            }
            catch
            {
                // Không để lỗi sync marker làm văng app.
            }
        };
        mapView.Navigated += _mapNavigatedHandler;
    }

    // ── Event handlers cho nút di chuyển ──
    private async void OnMoveUpClicked(object? sender, EventArgs e)
        => await MoveSimulation(MOVE_STEP_METERS, 0);

    private async void OnMoveDownClicked(object? sender, EventArgs e)
        => await MoveSimulation(-MOVE_STEP_METERS, 0);

    private async void OnMoveLeftClicked(object? sender, EventArgs e)
        => await MoveSimulation(0, -MOVE_STEP_METERS);

    private async void OnMoveRightClicked(object? sender, EventArgs e)
        => await MoveSimulation(0, MOVE_STEP_METERS);

    /// <summary>Nhảy nhanh về tọa độ demo Vĩnh Khánh (không cần mở QR / xe buýt).</summary>
    private async void OnJumpToVinhKhanhDemoClicked(object? sender, EventArgs e)
    {
        // Không tạm chặn GPS: nếu không, 60s sau nút này Fake GPS / GPS thật sẽ không cập nhật marker.
        await MoveSimulatedMarkerToAsync(DemoVinhKhanhLat, DemoVinhKhanhLng, runProximityCheck: true, pauseGpsAfterMove: false);
    }

    private async void OnClearRouteClicked(object? sender, EventArgs e)
    {
        try
        {
            await RouteTrackService.ClearAsync();
            await mapView.EvaluateJavaScriptAsync("window.clearRoutePath && window.clearRoutePath();");
            await DisplayAlertAsync("Đã xóa tuyến", "Đã xóa toàn bộ dữ liệu tuyến di chuyển.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Lỗi", $"Không thể xóa tuyến.\n{ex.Message}", "OK");
        }
    }

    private async void OnResetDemoStateClicked(object? sender, EventArgs e)
    {
        try
        {
            CancelProximitySpeech();
            CancelBusStopSpeech();

            _activeProximityPoiIndex = -1;
            _activeBusStopToken = null;
            _autoGeoNextAllowedPlayUtcByPoi.Clear();
            _autoGeoLastLogByPoi.Clear();
            _lastQueuedGpsLat = double.NaN;
            _lastQueuedGpsLng = double.NaN;
            _lastAcceptedGpsUtc = null;

            UpdateGeoStatusLabel("Đã reset demo - ngoài vùng POI");
            UpdateCooldownLabel(-1);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                lblLastPlayedStatus.Text = "🔊 Đã phát gần nhất: -";
            });

            await mapView.EvaluateJavaScriptAsync("window.setNearestPoiHighlight && window.setNearestPoiHighlight(-1);");
            await DisplayAlertAsync("Reset demo", "Đã reset trạng thái geofence/cooldown để test lại từ đầu.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Lỗi reset", $"Không thể reset trạng thái demo.\n{ex.Message}", "OK");
        }
    }

    private void PopulateQrPicker()
    {
        pickerPoiForQr.Items.Clear();
        foreach (var p in _pois)
        {
            pickerPoiForQr.Items.Add(p.Name);
        }

        PopulateSimulatePoiPicker();
    }

    /// <summary>
    /// Trong phòng không có GPS/mock ổn định: đặt marker đúng tọa độ quán trong DB (không phải mũi tên lưới ô).
    /// </summary>
    void PopulateSimulatePoiPicker()
    {
        _suppressSimulatePoiPickerEvent = true;
        try
        {
            pickerSimulateNearPoi.Items.Clear();
            pickerSimulateNearPoi.Items.Add("-- Chọn quán --");
            foreach (var p in _pois)
            {
                if (p is not null)
                    pickerSimulateNearPoi.Items.Add(p.Name);
            }

            pickerSimulateNearPoi.SelectedIndex = 0;
        }
        finally
        {
            _suppressSimulatePoiPickerEvent = false;
        }
    }

    void OnSimulateNearPoiPickerChanged(object? sender, EventArgs e)
    {
        if (_suppressSimulatePoiPickerEvent)
            return;
        if (pickerSimulateNearPoi.SelectedIndex <= 0)
            return;

        var poiIndex = pickerSimulateNearPoi.SelectedIndex - 1;
        if (poiIndex < 0 || poiIndex >= _pois.Count)
            return;

        var place = _pois[poiIndex];
        if (place is null)
            return;

        _ = MoveSimulatedMarkerToAsync(place.Latitude, place.Longitude, runProximityCheck: true, pauseGpsAfterMove: false);
    }

    /// <summary>Bấm nút QR xanh: mở/đóng khối QR; nội dung luôn là mã QR từng điểm (chọn được trong Picker).</summary>
    private void OnQrToggleClicked(object? sender, EventArgs e)
    {
        if (qrNearbyPanel.IsVisible)
        {
            qrNearbyPanel.IsVisible = false;
            btnQrToggle.Text = "QR ▼";
            return;
        }

        qrNearbyPanel.IsVisible = true;
        btnQrToggle.Text = "QR ▲";
        ApplyDefaultSelectionForQrPanel();
    }

    private void ApplyDefaultSelectionForQrPanel()
    {
        if (_pois.Count == 0)
        {
            lblQrHint.Text = "Chưa có điểm thuyết minh.";
            return;
        }

        // QR hoạt động độc lập GPS: luôn chọn theo danh sách POI hoặc id trong mã QR.
        var index = pickerPoiForQr.SelectedIndex >= 0 ? pickerPoiForQr.SelectedIndex : 0;
        lblQrHint.Text = "QR này không cần GPS: khách quét mã sẽ mở đúng điểm theo poiId trong QR.";

        _suppressQrPickerEvent = true;
        try
        {
            pickerPoiForQr.SelectedIndex = index;
        }
        finally
        {
            _suppressQrPickerEvent = false;
        }

        UpdateQrPanelContent(index);
    }

    private void OnPoiQrPickerChanged(object? sender, EventArgs e)
    {
        if (_suppressQrPickerEvent) return;
        if (pickerPoiForQr.SelectedIndex < 0) return;
        UpdateQrPanelContent(pickerPoiForQr.SelectedIndex);
    }

    private void OnSelectKhanhHoiQrClicked(object? sender, EventArgs e) => _ = RunBusStopSelectionAsync("KHANH_HOI");
    private void OnSelectVinhHoiQrClicked(object? sender, EventArgs e) => _ = RunBusStopSelectionAsync("VINH_HOI");
    private void OnSelectXomChieuQrClicked(object? sender, EventArgs e) => _ = RunBusStopSelectionAsync("XOM_CHIEU");

    /// <summary>
    /// Xử lý tuần tự: nhảy marker → (không kích proximity trùng) → hủy TTS cũ → phát đúng tuyến mới.
    /// Trước đây Move + Speak chạy song song nên proximity/TTS cũ làm nghe nhầm “tuyến cũ”.
    /// </summary>
    private async Task RunBusStopSelectionAsync(string token)
    {
        if (!TryResolveBusStopPoiIndex(token, out var poiIndex) && !TryResolvePoiIndexFromQr(token, out poiIndex))
            return;

        _suppressQrPickerEvent = true;
        try
        {
            pickerPoiForQr.SelectedIndex = poiIndex;
        }
        finally
        {
            _suppressQrPickerEvent = false;
        }

        UpdateQrPanelContent(poiIndex);

        if (TryGetBusStopCoordinates(token, out var lat, out var lng))
            await MoveSimulatedMarkerToAsync(lat, lng, runProximityCheck: false);

        await SpeakPoiImmediatelyFromBusStopAsync(token, poiIndex);
    }

    /// <summary>Chỉ định POI theo đúng 3 mã điểm dừng (không dùng Contains để tránh nhầm).</summary>
    private static bool TryResolveBusStopPoiIndex(string token, out int poiIndex)
    {
        poiIndex = -1;
        var n = NormalizeQrToken(token);
        switch (n)
        {
            case "KHANHHOI":
                poiIndex = 4;
                return true;
            case "VINHHOI":
                poiIndex = 0;
                return true;
            case "XOMCHIEU":
                poiIndex = 1;
                return true;
            default:
                return false;
        }
    }

    private static string GetBusStopDisplayName(string token)
    {
        var normalized = NormalizeQrToken(token);
        return normalized switch
        {
            "KHANHHOI" => "Khánh Hội",
            "VINHHOI" => "Vĩnh Hội",
            "XOMCHIEU" => "Xóm Chiếu",
            _ => "gần đây"
        };
    }

    /// <summary>
    /// Trên tuyến xe buýt chỉ cần giới thiệu món/đặc điểm quán; câu dạng "Bạn đang ở khu vực…" / "You are now…"
    /// dành cho khi khách thật sự vào vùng POI (AutoGeo), không lặp khi đang ở trạm.
    /// </summary>
    private static string StripArrivalFramingForBusRouteContext(string? text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var t = text.Trim();
        var startsArrival = false;
        switch (lang)
        {
            case "en":
                startsArrival = t.StartsWith("You are now ", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("You are currently", StringComparison.OrdinalIgnoreCase);
                break;
            case "zh":
                startsArrival = t.StartsWith("您正在");
                break;
            case "ja":
                startsArrival = t.StartsWith("あなたは今、");
                break;
            default:
                startsArrival = t.StartsWith("Bạn đang ở", StringComparison.OrdinalIgnoreCase);
                break;
        }

        if (!startsArrival) return text;

        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '.' || t[i] == '。')
            {
                var rest = t[(i + 1)..].TrimStart();
                return string.IsNullOrWhiteSpace(rest) ? text : rest;
            }
        }

        return text;
    }

    private async Task SpeakPoiImmediatelyFromBusStopAsync(string token, int poiIndex)
    {
        if (poiIndex < 0 || poiIndex >= _pois.Count) return;
        var place = _pois[poiIndex];
        if (place is null) return;
        var busStopName = GetBusStopDisplayName(token);
        var lang = string.IsNullOrEmpty(_selectedLanguage) ? "vi" : _selectedLanguage;

        var viMain = place.VietnameseAudioText ?? place.EnglishAudioText ?? "";
        var enMain = place.EnglishAudioText ?? place.VietnameseAudioText ?? "";
        var zhMain = place.ChineseAudioText ?? place.VietnameseAudioText ?? "";
        var jaMain = place.JapaneseAudioText ?? place.VietnameseAudioText ?? "";

        var text = lang switch
        {
            "en" =>
                $"Along the {busStopName} bus route, near {place.Name}. {StripArrivalFramingForBusRouteContext(enMain, "en").TrimStart()}",
            "zh" =>
                $"途经{busStopName}公交线，靠近{place.Name}。{StripArrivalFramingForBusRouteContext(zhMain, "zh").TrimStart()}",
            "ja" =>
                $"{busStopName}のバス路線沿い、{place.Name}の近く。{StripArrivalFramingForBusRouteContext(jaMain, "ja").TrimStart()}",
            _ =>
                $"Trên tuyến xe buýt {busStopName}, gần khu ẩm thực {place.Name}. {StripArrivalFramingForBusRouteContext(viMain, "vi").TrimStart()}"
        };
        if (string.IsNullOrWhiteSpace(text)) return;

        CancelProximitySpeech();
        CancelBusStopSpeech();
        _busStopTtsCts?.Dispose();
        _busStopTtsCts = new CancellationTokenSource();
        var cts = _busStopTtsCts;
        var ct = cts.Token;

        try
        {
            var durationSeconds = await NarrationQueueService.EnqueuePoiOrTtsAsync(poiIndex, _selectedLanguage, text, ct);
            UpdateLastPlayedLabel(place.Name, "BusStop");
            await HistoryLogService.AddAsync(place.Name, "BusStop", _selectedLanguage, durationSeconds);
        }
        catch (OperationCanceledException)
        {
            // Đã bấm điểm dừng khác — bỏ log.
        }
        finally
        {
            if (ReferenceEquals(_busStopTtsCts, cts))
            {
                _busStopTtsCts?.Dispose();
                _busStopTtsCts = null;
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    void CancelBusStopSpeech()
    {
        try
        {
            _busStopTtsCts?.Cancel();
            NarrationQueueService.StopActivePlayer();
        }
        catch
        {
            // Bỏ qua.
        }
    }

    private static bool TryGetBusStopCoordinates(string token, out double lat, out double lng)
    {
        lat = 0;
        lng = 0;
        var normalized = NormalizeQrToken(token);
        switch (normalized)
        {
            case "KHANHHOI":
                lat = 10.7597; lng = 106.7050; return true;
            case "VINHHOI":
                lat = 10.7586; lng = 106.7036; return true;
            case "XOMCHIEU":
                lat = 10.7603; lng = 106.7026; return true;
            default:
                return false;
        }
    }

    private static bool TryGetNearestBusStopInRange(double lat, double lng, double enterMeters, out string token, out int poiIndex)
    {
        token = string.Empty;
        poiIndex = -1;
        var nearestDistance = double.MaxValue;

        var stops = new[]
        {
            "KHANH_HOI",
            "VINH_HOI",
            "XOM_CHIEU"
        };

        foreach (var stop in stops)
        {
            if (!TryGetBusStopCoordinates(stop, out var sLat, out var sLng))
                continue;

            var d = CalculateDistanceStatic(lat, lng, sLat, sLng);
            if (d <= enterMeters && d < nearestDistance && TryResolveBusStopPoiIndex(stop, out var mappedPoi))
            {
                nearestDistance = d;
                token = stop;
                poiIndex = mappedPoi;
            }
        }

        return poiIndex >= 0;
    }

    private static double CalculateDistanceStatic(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000;
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return r * c;
    }

    private async Task MoveSimulatedMarkerToAsync(double lat, double lng, bool runProximityCheck = true, bool pauseGpsAfterMove = true)
    {
        _simulatedLat = lat;
        _simulatedLng = lng;
        if (pauseGpsAfterMove)
            PauseGpsForManualDemo();

        var js = $@"
        if (window.userMarker && typeof window.userMarker.setLatLng === 'function') {{
            window.userMarker.setLatLng([{lat.ToString(CultureInfo.InvariantCulture)}, {lng.ToString(CultureInfo.InvariantCulture)}]);
            if (window.userMarker.openPopup) {{
                window.userMarker.openPopup();
            }}
        }}
        if (window.appMap && typeof window.appMap.panTo === 'function') {{
            window.appMap.panTo([{lat.ToString(CultureInfo.InvariantCulture)}, {lng.ToString(CultureInfo.InvariantCulture)}]);
        }}
        ";

        for (var i = 0; i < 4; i++)
        {
            try
            {
                await mapView.EvaluateJavaScriptAsync(js);
                await TrackRoutePointAsync("jump");
                if (runProximityCheck)
                    await CheckProximityAndSpeakAsync();
                return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }
    }

    private void UpdateQrPanelContent(int poiIndex)
    {
        if (poiIndex < 0 || poiIndex >= _pois.Count) return;

        var place = _pois[poiIndex];
        var payload = $"app://poi?id={poiIndex}";

        lblQrNearbyTitle.Text = place.Name;
        lblNearbyPayload.Text = payload;
        imgNearbyQr.Source = GenerateQrImage(payload);
    }

    private async Task OpenScannerWithPermissionAsync()
    {
        var cameraPermission = await Permissions.RequestAsync<Permissions.Camera>();
        if (cameraPermission != PermissionStatus.Granted)
        {
            await DisplayAlertAsync("Không thể quét QR", "Bạn cần cấp quyền camera để quét mã QR.", "OK");
            return;
        }

        try
        {
            await Navigation.PushModalAsync(new QrScannerPage(OnQrScanned));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Lỗi mở QR", $"Không thể mở camera quét QR trên thiết bị này.\n{ex.Message}", "OK");
        }
    }

    private ImageSource GenerateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(8);
        return ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    /// <summary>Mở QR phóng to nền trắng — khách dùng điện thoại khác quét màn hình này.</summary>
    private async void OnOpenGuestQrFullscreenClicked(object? sender, EventArgs e)
    {
        if (pickerPoiForQr.SelectedIndex < 0 || pickerPoiForQr.SelectedIndex >= _pois.Count)
        {
            await DisplayAlertAsync("Chưa chọn điểm", "Chọn quán trong danh sách trước khi hiển thị QR cho khách.", "OK");
            return;
        }

        var idx = pickerPoiForQr.SelectedIndex;
        var place = _pois[idx];
        var payload = $"app://poi?id={idx}";

        try
        {
            await Navigation.PushModalAsync(new QrGuestFullscreenPage(place.Name, payload));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Lỗi", $"Không mở được màn hình QR.\n{ex.Message}", "OK");
        }
    }

    private async void OnOpenScannerFromPanelClicked(object? sender, EventArgs e)
    {
        await OpenScannerWithPermissionAsync();
    }

    private void OnCloseNearbyQrPanelClicked(object? sender, EventArgs e)
    {
        qrNearbyPanel.IsVisible = false;
        btnQrToggle.Text = "QR ▼";
    }

    private void OnQrScanned(string rawValue)
    {
        MainThread.BeginInvokeOnMainThread(async () => await HandleQrScanAsync(rawValue));
    }

    private async Task HandleQrScanAsync(string rawValue)
    {
        if (!TryResolvePoiIndexFromQr(rawValue, out var poiIndex))
        {
            await DisplayAlertAsync("QR không hợp lệ", "Không tìm thấy POI từ mã QR này.", "OK");
            return;
        }

        if (poiIndex < 0 || poiIndex >= _pois.Count)
        {
            await DisplayAlertAsync("POI không tồn tại", "Mã QR đã quét không có trong dữ liệu hiện tại.", "OK");
            return;
        }

        var place = _pois[poiIndex];
        var text = _selectedLanguage switch
        {
            "en" => place.EnglishAudioText ?? place.VietnameseAudioText ?? "",
            "zh" => place.ChineseAudioText ?? place.VietnameseAudioText ?? "",
            "ja" => place.JapaneseAudioText ?? place.VietnameseAudioText ?? "",
            _ => place.VietnameseAudioText ?? place.EnglishAudioText ?? ""
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            CancelProximitySpeech();
            CancelBusStopSpeech();
            try
            {
                var durationSeconds = await NarrationQueueService.EnqueuePoiOrTtsAsync(poiIndex, _selectedLanguage, text);
                UpdateLastPlayedLabel(place.Name, "QR");
                await HistoryLogService.AddAsync(place.Name, "QR", _selectedLanguage, durationSeconds);
            }
            catch (OperationCanceledException)
            {
                // Đã hủy thuyết minh — bỏ ghi log.
            }
        }
        else
        {
            await HistoryLogService.AddAsync(place.Name, "QR", _selectedLanguage);
        }

        await mapView.EvaluateJavaScriptAsync($"window.openPoiById && window.openPoiById({poiIndex});");
        // Theo yêu cầu "quét là nghe ngay", không chặn luồng bằng popup success.
    }

    private bool TryResolvePoiIndexFromQr(string rawValue, out int poiIndex)
    {
        poiIndex = -1;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var value = rawValue.Trim();
        var normalized = NormalizeQrToken(value);

        // ===== Mục 8 trong yêu cầu: QR tại điểm dừng xe buýt, không cần GPS =====
        // Khớp chính xác trước (tránh nhầm khi chuỗi dài chứa nhiều từ khóa).
        if (normalized is "KHANHHOI" or "VINHHOI" or "XOMCHIEU")
        {
            poiIndex = normalized switch
            {
                "KHANHHOI" => 4,
                "VINHHOI" => 0,
                "XOMCHIEU" => 1,
                _ => -1
            };
            return true;
        }

        // Chuỗi QR có tiền tố/hậu tố (vd. STOP_KHANHHOI_V1)
        if (normalized.Contains("KHANHHOI"))
        {
            poiIndex = 4;
            return true;
        }
        if (normalized.Contains("VINHHOI"))
        {
            poiIndex = 0;
            return true;
        }
        if (normalized.Contains("XOMCHIEU"))
        {
            poiIndex = 1;
            return true;
        }

        // Hỗ trợ trường hợp mã chỉ chứa số thứ tự POI.
        if (int.TryParse(value, out var directIndex))
        {
            poiIndex = directIndex;
            return true;
        }

        // Hỗ trợ deep link: tourguide://poi?id=2 hoặc app://poi?id=2
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?');
            if (!string.IsNullOrWhiteSpace(query))
            {
                var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
                    var val = Uri.UnescapeDataString(kv[1]);
                    if ((key == "id" || key == "poiid" || key == "poi") && int.TryParse(val, out var parsed))
                    {
                        poiIndex = parsed;
                        return true;
                    }
                }
            }
        }

        // Một số thiết bị/scanner trả về app://poi?id=n mà Uri.Query rỗng — bắt bằng regex
        var idMatch = Regex.Match(value, @"[?&]id=(\d+)", RegexOptions.IgnoreCase);
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var idFromRegex))
        {
            poiIndex = idFromRegex;
            return true;
        }

        // Hỗ trợ JSON: {"poiId":2} hoặc {"id":2}
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("stop", out var stopProp))
                {
                    var stopValue = NormalizeQrToken(stopProp.GetString() ?? string.Empty);
                    if (stopValue.Contains("KHANHHOI")) { poiIndex = 4; return true; }
                    if (stopValue.Contains("VINHHOI")) { poiIndex = 0; return true; }
                    if (stopValue.Contains("XOMCHIEU")) { poiIndex = 1; return true; }
                }

                if (root.TryGetProperty("ward", out var wardProp))
                {
                    var wardValue = NormalizeQrToken(wardProp.GetString() ?? string.Empty);
                    if (wardValue.Contains("KHANHHOI")) { poiIndex = 4; return true; }
                    if (wardValue.Contains("VINHHOI")) { poiIndex = 0; return true; }
                    if (wardValue.Contains("XOMCHIEU")) { poiIndex = 1; return true; }
                }

                if (root.TryGetProperty("poiId", out var poiIdProp) && poiIdProp.TryGetInt32(out var jsonPoiId))
                {
                    poiIndex = jsonPoiId;
                    return true;
                }

                if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var jsonId))
                {
                    poiIndex = jsonId;
                    return true;
                }
            }
        }
        catch
        {
            // Bỏ qua nếu không phải JSON.
        }

        // Fallback: tìm số đầu tiên trong chuỗi kiểu "POI-3"
        var match = Regex.Match(value, @"\d+");
        if (match.Success && int.TryParse(match.Value, out var extracted))
        {
            poiIndex = extracted;
            return true;
        }

        return false;
    }

    private static string NormalizeQrToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb
            .ToString()
            .ToUpperInvariant()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);
    }

    private async Task MoveSimulation(double deltaLatM, double deltaLngM)
    {
        const double metersPerLatDeg = 111000;
        double metersPerLngDeg = 111000 * Math.Cos(_simulatedLat * Math.PI / 180);

        double deltaLat = deltaLatM / metersPerLatDeg;
        double deltaLng = deltaLngM / metersPerLngDeg;

        _simulatedLat += deltaLat;
        _simulatedLng += deltaLng;

        try
        {
            // Pan theo chấm đen khi demo bằng mũi tên — trước đây pan=false nên map đứng yên,
            // phải bật QR/chọn điểm mới thấy “bay” tới quán.
            await SyncUserMarkerPositionOnMapAsync(panToMarker: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MoveSimulation JS error: {ex.Message}");
        }

        PauseGpsForManualDemo();
        await TrackRoutePointAsync("arrow");
        await CheckProximityAndSpeakAsync();
    }

    void PauseGpsForManualDemo()
    {
        _gpsManualOverrideUntilUtc = DateTime.UtcNow.AddSeconds(GpsManualOverrideSeconds);
        _lastQueuedGpsLat = double.NaN;
        _lastQueuedGpsLng = double.NaN;
    }

    private async Task CheckProximityAndSpeakAsync()
    {
        string? textToSpeak = null;
        int speakPoiIndex = -1;
        Place? speakPlace = null;
        CancellationTokenSource? speakCts = null;
        int nearestPoiForHighlight = -1;
        string? busStopToSpeak = null;
        int busStopPoiIndex = -1;
        var inBusStopZone = false;
        var nearStopPoiForHighlight = -1;

        await _proximityCheckGate.WaitAsync();
        try
        {
            if (TryGetNearestBusStopInRange(_simulatedLat, _simulatedLng, BusStopEnterMeters, out var nearStopToken, out var nearStopPoi))
            {
                inBusStopZone = true;
                nearStopPoiForHighlight = nearStopPoi;
                UpdateGeoStatusLabel($"Trong vùng trạm {GetBusStopDisplayName(nearStopToken)}");
                if (!string.Equals(_activeBusStopToken, nearStopToken, StringComparison.Ordinal))
                {
                    _activeBusStopToken = nearStopToken;
                    busStopToSpeak = nearStopToken;
                    busStopPoiIndex = nearStopPoi;
                }
            }
            else if (!string.IsNullOrWhiteSpace(_activeBusStopToken))
            {
                if (TryGetBusStopCoordinates(_activeBusStopToken!, out var activeStopLat, out var activeStopLng))
                {
                    var distToActiveStop = CalculateDistance(_simulatedLat, _simulatedLng, activeStopLat, activeStopLng);
                    if (distToActiveStop > BusStopExitMeters)
                        _activeBusStopToken = null;
                }
                else
                {
                    _activeBusStopToken = null;
                }
            }

            if (_activeProximityPoiIndex >= 0 && _activeProximityPoiIndex < _pois.Count)
            {
                var activePlace = _pois[_activeProximityPoiIndex];
                if (activePlace is not null)
                {
                var distToActive = CalculateDistance(_simulatedLat, _simulatedLng, activePlace.Latitude, activePlace.Longitude);
                if (distToActive > GetExitRadiusMeters(activePlace))
                    {
                        CancelProximitySpeech();
                        _activeProximityPoiIndex = -1;
                        UpdateGeoStatusLabel("Ngoài vùng POI");
                        UpdateCooldownLabel(-1);
                    }
                    else
                    {
                        UpdateGeoStatusLabel($"Đang trong vùng: {activePlace.Name}");
                    }
                }
                else
                {
                    _activeProximityPoiIndex = -1;
                    UpdateGeoStatusLabel("Ngoài vùng POI");
                }
            }

            // Trong vùng trạm xe buýt: ưu tiên thuyết minh trạm, không để AutoGeo POI (vd. quán ốc) chặn hoặc trùng.
            if (inBusStopZone)
            {
                nearestPoiForHighlight = nearStopPoiForHighlight >= 0 ? nearStopPoiForHighlight : -1;
                CancelProximitySpeech();
                _activeProximityPoiIndex = -1;
                if (string.IsNullOrWhiteSpace(busStopToSpeak))
                {
                    _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                    return;
                }
            }
            else if (_activeProximityPoiIndex >= 0)
            {
                nearestPoiForHighlight = _activeProximityPoiIndex;
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            if (!inBusStopZone)
            {
            var nearestIndex = -1;
            var nearestDistance = double.MaxValue;
            var secondNearestDistance = double.MaxValue;
            var candidateIndex = -1;
            var candidateDistance = double.MaxValue;
            var candidatePriority = int.MinValue;
            var secondCandidateDistance = double.MaxValue;
            var secondCandidatePriority = int.MinValue;

            for (var i = 0; i < _pois.Count; i++)
            {
                var place = _pois[i];
                if (place == null) continue;

                var distance = CalculateDistance(_simulatedLat, _simulatedLng, place.Latitude, place.Longitude);
                if (distance < nearestDistance)
                {
                    secondNearestDistance = nearestDistance;
                    nearestDistance = distance;
                    nearestIndex = i;
                }
                else if (distance < secondNearestDistance)
                {
                    secondNearestDistance = distance;
                }

                if (distance <= GetEnterRadiusMeters(place))
                {
                    if (place.Priority > candidatePriority || (place.Priority == candidatePriority && distance < candidateDistance))
                    {
                        secondCandidateDistance = candidateDistance;
                        secondCandidatePriority = candidatePriority;
                        candidatePriority = place.Priority;
                        candidateDistance = distance;
                        candidateIndex = i;
                    }
                    else if (place.Priority == candidatePriority && distance < secondCandidateDistance)
                    {
                        secondCandidateDistance = distance;
                        secondCandidatePriority = place.Priority;
                    }
                }
            }

            nearestPoiForHighlight = nearestIndex;
            if (nearestIndex < 0)
            {
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }
            if (candidateIndex < 0)
            {
                UpdateGeoStatusLabel("Ngoài vùng POI");
                UpdateCooldownLabel(-1);
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            var distanceGap = secondCandidateDistance - candidateDistance;
            var isAmbiguousSamePriority = secondCandidatePriority == candidatePriority
                                          && secondCandidateDistance < double.MaxValue
                                          && distanceGap < AutoGeoMinGapMeters;
            if (isAmbiguousSamePriority)
            {
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            var nearestPlace = _pois[candidateIndex];
            if (nearestPlace == null)
            {
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            var text = _selectedLanguage switch
            {
                "vi" => nearestPlace.VietnameseAudioText ?? nearestPlace.EnglishAudioText ?? nearestPlace.ChineseAudioText ?? nearestPlace.JapaneseAudioText ?? "",
                "en" => nearestPlace.EnglishAudioText ?? nearestPlace.VietnameseAudioText ?? nearestPlace.ChineseAudioText ?? nearestPlace.JapaneseAudioText ?? "",
                "zh" => nearestPlace.ChineseAudioText ?? nearestPlace.VietnameseAudioText ?? nearestPlace.EnglishAudioText ?? nearestPlace.JapaneseAudioText ?? "",
                "ja" => nearestPlace.JapaneseAudioText ?? nearestPlace.VietnameseAudioText ?? nearestPlace.EnglishAudioText ?? nearestPlace.ChineseAudioText ?? "",
                _ => nearestPlace.VietnameseAudioText ?? ""
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                UpdateGeoStatusLabel($"Đang trong vùng: {nearestPlace.Name}");
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            if (IsAutoGeoPlaybackDebounced(candidateIndex))
            {
                UpdateGeoStatusLabel($"Đang trong vùng: {nearestPlace.Name}");
                UpdateCooldownLabel(candidateIndex);
                nearestPoiForHighlight = nearestIndex;
                _ = UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);
                return;
            }

            _activeProximityPoiIndex = candidateIndex;
            CancelProximitySpeech();
            speakCts = new CancellationTokenSource();
            _proximityTtsCts = speakCts;
            textToSpeak = text;
            speakPoiIndex = candidateIndex;
            speakPlace = nearestPlace;
            UpdateGeoStatusLabel($"Đang trong vùng: {nearestPlace.Name}");
            }
        }
        finally
        {
            _proximityCheckGate.Release();
        }

        await UpdateNearestPoiHighlightAsync(nearestPoiForHighlight);

        if (!string.IsNullOrWhiteSpace(busStopToSpeak) && busStopPoiIndex >= 0)
        {
            await SpeakPoiImmediatelyFromBusStopAsync(busStopToSpeak, busStopPoiIndex);
            return;
        }

        if (string.IsNullOrWhiteSpace(textToSpeak) || speakCts is null || speakPlace is null)
            return;

        var token = speakCts.Token;
        try
        {
            var durationSeconds = await NarrationQueueService.EnqueuePoiOrTtsAsync(speakPoiIndex, _selectedLanguage, textToSpeak, token);
            RegisterAutoGeoPlaybackCompleted(speakPoiIndex);
            UpdateLastPlayedLabel(speakPlace.Name, "AutoGeo");
            UpdateCooldownLabel(speakPoiIndex);
            if (ShouldLogAutoGeo(speakPoiIndex))
            {
                await HistoryLogService.AddAsync(speakPlace.Name, "AutoGeo", _selectedLanguage, durationSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // Đã rời vùng — hủy TTS là đúng ý.
        }
        finally
        {
            if (ReferenceEquals(_proximityTtsCts, speakCts))
            {
                _proximityTtsCts?.Dispose();
                _proximityTtsCts = null;
            }
            else
            {
                speakCts.Dispose();
            }
        }
    }

    private void CancelProximitySpeech()
    {
        try
        {
            _proximityTtsCts?.Cancel();
            NarrationQueueService.StopActivePlayer();
        }
        catch
        {
            // Bỏ qua.
        }
    }

    private bool ShouldLogAutoGeo(int poiIndex)
    {
        var now = DateTime.Now;
        if (_autoGeoLastLogByPoi.TryGetValue(poiIndex, out var lastTime))
        {
            if ((now - lastTime).TotalSeconds < AutoGeoLogCooldownSeconds)
                return false;
        }

        _autoGeoLastLogByPoi[poiIndex] = now;
        return true;
    }

    private bool IsAutoGeoPlaybackDebounced(int poiIndex)
    {
        if (poiIndex < 0) return false;
        return _autoGeoNextAllowedPlayUtcByPoi.TryGetValue(poiIndex, out var notBefore)
               && DateTime.UtcNow < notBefore;
    }

    private void RegisterAutoGeoPlaybackCompleted(int poiIndex)
    {
        if (poiIndex < 0) return;
        _autoGeoNextAllowedPlayUtcByPoi[poiIndex] = DateTime.UtcNow.AddSeconds(AutoGeoSpeechDebounceSeconds);
    }

    private void UpdateGeoStatusLabel(string status)
    {
        _currentZoneStatus = status;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lblGeoStatus.Text = $"📍 Trạng thái: {_currentZoneStatus}";
        });
    }

    private void UpdateLastPlayedLabel(string placeName, string source)
    {
        var now = DateTime.Now;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lblLastPlayedStatus.Text = $"🔊 Đã phát gần nhất: {placeName} ({source}) lúc {now:HH:mm:ss}";
        });
    }

    private void UpdateCooldownLabel(int poiIndex)
    {
        var text = "⏳ Cooldown: -";
        if (poiIndex >= 0 && _autoGeoNextAllowedPlayUtcByPoi.TryGetValue(poiIndex, out var notBeforeUtc))
        {
            var remaining = notBeforeUtc - DateTime.UtcNow;
            if (remaining.TotalSeconds > 0)
                text = $"⏳ Cooldown: còn {Math.Ceiling(remaining.TotalSeconds)}s";
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            lblCooldownStatus.Text = text;
        });
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double deg) => deg * Math.PI / 180;

    private static double GetEnterRadiusMeters(Place place)
    {
        if (place.ActivationRadiusMeters > 1)
            return place.ActivationRadiusMeters;
        return DefaultAutoGeoEnterMeters;
    }

    private static double GetExitRadiusMeters(Place place)
    {
        var enter = GetEnterRadiusMeters(place);
        return Math.Max(MinAutoGeoExitMeters, enter * AutoGeoExitMultiplier);
    }

    private async Task UpdateNearestPoiHighlightAsync(int nearestPoiIndex)
    {
        var js = $"window.setNearestPoiHighlight && window.setNearestPoiHighlight({nearestPoiIndex});";
        try
        {
            await mapView.EvaluateJavaScriptAsync(js);
        }
        catch
        {
            // WebView chưa sẵn sàng hoặc đang reload.
        }
    }

    async Task<Location?> TryGetCurrentLocationAsync()
    {
        try
        {
            if (!await EnsureLocationPermissionsForContinuousTrackingAsync())
                return null;

            var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10));
            var location = await Geolocation.Default.GetLocationAsync(request);
            if (location is not null)
                return location;

            return await Geolocation.Default.GetLastKnownLocationAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lắng nghe GPS liên tục (MAUI dùng foreground service trên Android — có thông báo hệ thống).
    /// Giữ chạy sau khi rời tab Bản đồ; cần quyền vị trí "Luôn" để cập nhật khi app ở nền.
    /// </summary>
    async Task TryStartForegroundGpsListeningAsync()
    {
        if (_isForegroundGpsListening)
            return;

        if (!await EnsureLocationPermissionsForContinuousTrackingAsync())
            return;

#if ANDROID
        // Mock / Samsung: fused listener không bắn sự kiện; poll GetLocationAsync vẫn lấy được tọa độ giả.
        StartAndroidGpsPolling();
#endif

        try
        {
            Geolocation.Default.LocationChanged += OnForegroundLocationChanged;
            Geolocation.Default.ListeningFailed += OnForegroundListeningFailed;

            await Geolocation.Default.StartListeningForegroundAsync(
                new GeolocationListeningRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(3)));

            _isForegroundGpsListening = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StartListeningForeground: {ex}");
            Geolocation.Default.LocationChanged -= OnForegroundLocationChanged;
            Geolocation.Default.ListeningFailed -= OnForegroundListeningFailed;
        }
    }

    /// <summary>Gộp đường dẫn cập nhật từ event GPS và từ poll Android (Fake GPS).</summary>
    void QueueGpsLocationFromReading(Location? location)
    {
        if (location is null)
            return;

        if (_gpsManualOverrideUntilUtc is DateTime until && DateTime.UtcNow < until)
            return;

        if (!double.IsNaN(_lastQueuedGpsLat) &&
            Math.Abs(location.Latitude - _lastQueuedGpsLat) < GpsDuplicateEpsilonDegrees &&
            Math.Abs(location.Longitude - _lastQueuedGpsLng) < GpsDuplicateEpsilonDegrees)
            return;

        if (!IsGpsAccuracyAcceptable(location))
            return;

        if (IsGpsJumpLikelyInvalid(location))
            return;

        _lastQueuedGpsLat = location.Latitude;
        _lastQueuedGpsLng = location.Longitude;
        _lastAcceptedGpsUtc = DateTime.UtcNow;

        var lat = location.Latitude;
        var lng = location.Longitude;

        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            _simulatedLat = lat;
            _simulatedLng = lng;
            _hasSimulationPosition = true;
            await SyncUserMarkerPositionOnMapAsync(panToMarker: false);
            await TrackRoutePointAsync("gps");
            await CheckProximityAndSpeakAsync();
        });
    }

    private static bool IsGpsAccuracyAcceptable(Location location)
    {
        var accuracy = location.Accuracy;
        if (!accuracy.HasValue || accuracy.Value <= 0)
            return true;
        return accuracy.Value <= MaxGpsAccuracyMeters;
    }

    private bool IsGpsJumpLikelyInvalid(Location location)
    {
        if (double.IsNaN(_lastQueuedGpsLat) || double.IsNaN(_lastQueuedGpsLng))
            return false;

        if (!_lastAcceptedGpsUtc.HasValue)
            return false;

        var now = DateTime.UtcNow;
        var dtSeconds = (now - _lastAcceptedGpsUtc.Value).TotalSeconds;
        if (dtSeconds <= 0.35)
            return false;

        var distanceMeters = CalculateDistance(_lastQueuedGpsLat, _lastQueuedGpsLng, location.Latitude, location.Longitude);
        if (distanceMeters < MinDistanceForSpeedFilterMeters)
            return false;

        // Fake GPS thường đổi vị trí theo kiểu "nhảy cụm"; nếu chặn sẽ bị kéo ngược/đứng yên sai.
        if (distanceMeters >= AllowLargeJumpMeters)
            return false;

        // Điểm có accuracy tốt thì ưu tiên tin cậy hơn speed check.
        if (location.Accuracy.HasValue && location.Accuracy.Value > 0 && location.Accuracy.Value <= 18)
            return false;

        var speedMps = distanceMeters / dtSeconds;
        return speedMps > MaxGpsSpeedMetersPerSecond;
    }

#if ANDROID
    void StartAndroidGpsPolling()
    {
        _androidGpsPollCts?.Cancel();
        _androidGpsPollCts?.Dispose();
        _androidGpsPollCts = new CancellationTokenSource();
        var ct = _androidGpsPollCts.Token;

        // Fake GPS thường đẩy vào LocationManager (GPS/Passive), không qua Fused của MAUI.
        AndroidLocationManagerBridge.Start((lat, lng) =>
            QueueGpsLocationFromReading(new Location(lat, lng)));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(4000, ct).ConfigureAwait(false);
                    var loc = await MainThread.InvokeOnMainThreadAsync(() =>
                        Geolocation.Default.GetLocationAsync(
                            new GeolocationRequest(GeolocationAccuracy.Lowest, TimeSpan.FromSeconds(12))));
                    if (loc is not null)
                        QueueGpsLocationFromReading(loc);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AndroidGpsPoll: {ex.Message}");
                }
            }
        }, ct);
    }

    void StopAndroidGpsPolling()
    {
        AndroidLocationManagerBridge.Stop();
        _androidGpsPollCts?.Cancel();
        _androidGpsPollCts?.Dispose();
        _androidGpsPollCts = null;
    }
#endif

    /// <summary>
    /// When-in-use bắt buộc; Always (Android/iOS) để hệ điều hành cho phép cập nhật khi không mở app.
    /// </summary>
    static async Task<bool> EnsureLocationPermissionsForContinuousTrackingAsync()
    {
        var whenInUse = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (whenInUse != PermissionStatus.Granted)
            return false;

#if ANDROID || IOS || MACCATALYST
        _ = await Permissions.RequestAsync<Permissions.LocationAlways>();
#endif
        return true;
    }

    void StopForegroundGpsListening()
    {
#if ANDROID
        StopAndroidGpsPolling();
#endif
        if (!_isForegroundGpsListening)
            return;

        try
        {
            Geolocation.Default.LocationChanged -= OnForegroundLocationChanged;
            Geolocation.Default.ListeningFailed -= OnForegroundListeningFailed;
            Geolocation.Default.StopListeningForeground();
        }
        catch
        {
            // Bỏ qua.
        }

        _isForegroundGpsListening = false;
    }

    void OnForegroundListeningFailed(object? sender, GeolocationListeningFailedEventArgs e)
    {
        Debug.WriteLine($"Geolocation listening failed: {e.Error}");
    }

    void OnForegroundLocationChanged(object? sender, GeolocationLocationChangedEventArgs e)
    {
        QueueGpsLocationFromReading(e.Location);
    }

    async Task TrackRoutePointAsync(string source)
    {
        try
        {
            await RouteTrackService.AppendPointAsync(_simulatedLat, _simulatedLng, source);
            var lat = _simulatedLat.ToString(CultureInfo.InvariantCulture);
            var lng = _simulatedLng.ToString(CultureInfo.InvariantCulture);
            await mapView.EvaluateJavaScriptAsync($"window.appendRoutePoint && window.appendRoutePoint({lat}, {lng});");
        }
        catch
        {
            // Bỏ qua lỗi route tracking để không ảnh hưởng luồng chính.
        }
    }

    /// <summary>Đồng bộ chấm đen trên Leaflet với <see cref="_simulatedLat"/> / <see cref="_simulatedLng"/>.</summary>
    async Task SyncUserMarkerPositionOnMapAsync(bool panToMarker)
    {
        var lat = _simulatedLat.ToString(CultureInfo.InvariantCulture);
        var lng = _simulatedLng.ToString(CultureInfo.InvariantCulture);
        var panJs = panToMarker
            ? $"if (window.appMap && typeof window.appMap.panTo === 'function') {{ window.appMap.panTo([{lat}, {lng}]); }}"
            : string.Empty;

        var js = $@"
        if (window.userMarker && typeof window.userMarker.setLatLng === 'function') {{
            window.userMarker.setLatLng([{lat}, {lng}]);
        }}
        {panJs}
        ";

        try
        {
            await mapView.EvaluateJavaScriptAsync(js);
        }
        catch
        {
            // WebView chưa sẵn sàng hoặc đang reload.
        }
    }

    async Task<string> GetImageDataUriAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        try
        {
            // fileName là tên file (ví dụ: pho-am-thuc-vinh-khanh-oc-dao-1707245308.jpg)
            await using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    async void MapView_Navigating(object sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("app://map", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            try
            {
                var uri = new Uri(e.Url);
                var query = uri.Query.TrimStart('?');
                foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2)
                        continue;
                    if (!string.Equals(kv[0], "u", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var url = Uri.UnescapeDataString(kv[1]);
                    if (Uri.TryCreate(url, UriKind.Absolute, out var openUri))
                        await Launcher.Default.OpenAsync(openUri);
                    return;
                }
            }
            catch
            {
                // Bỏ qua.
            }

            return;
        }

        if (!e.Url.StartsWith("app://poi", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;

        try
        {
            var uri = new Uri(e.Url);
            var query = uri.Query.TrimStart('?');
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            int id = -1;
            string lang = "vi";

            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var key = Uri.UnescapeDataString(kv[0]);
                var value = Uri.UnescapeDataString(kv[1]);

                if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
                    id = int.TryParse(value, out var tmp) ? tmp : -1;
                if (string.Equals(key, "lang", StringComparison.OrdinalIgnoreCase))
                    lang = value.ToLower();
            }

            if (id < 0 || id >= _pois.Count) return;

            var place = _pois[id];

            // === CẬP NHẬT LẤY TEXT CHO 4 NGÔN NGỮ ===
            var text = lang switch
            {
                "en" => place.EnglishAudioText,
                "zh" => place.ChineseAudioText,
                "ja" => place.JapaneseAudioText,
                _ => place.VietnameseAudioText
            };

            CancelProximitySpeech();
            CancelBusStopSpeech();
            var durationSeconds = await NarrationQueueService.EnqueuePoiOrTtsAsync(id, lang, text ?? "");
            UpdateLastPlayedLabel(place.Name, "Map");
            await HistoryLogService.AddAsync(place.Name, "Map", lang, durationSeconds);
        }
        catch { }
    }
}