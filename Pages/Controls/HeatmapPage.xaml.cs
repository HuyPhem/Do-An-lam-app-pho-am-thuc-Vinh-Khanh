using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TourGuideApp2.PageModels;
using TourGuideApp2.Services;

namespace TourGuideApp2.Pages;

public partial class HeatmapPage : ContentPage
{
    private readonly HeatmapPageModel _viewModel;
    private bool _isMapReady = false;

    /// <summary>Shell dùng DataTemplate không gọi DI — resolve từ MAUI service provider.</summary>
    public HeatmapPage() : this(ResolveViewModel())
    {
    }

    private static HeatmapPageModel ResolveViewModel()
    {
        if (Application.Current?.Handler?.MauiContext?.Services.GetService<HeatmapPageModel>() is { } vm)
            return vm;
        return new HeatmapPageModel(new SimulationService());
    }

    public HeatmapPage(HeatmapPageModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadAndInitializeMap();
    }

    private void LoadAndInitializeMap()
    {
        string html = GetHeatmapHtml();
        mapWebView.Source = new HtmlWebViewSource { Html = html };

        // Chờ WebView load xong (rất quan trọng trên Android để tránh crash)
        mapWebView.Navigated += OnWebViewNavigated;
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success || _isMapReady)
            return;

        _isMapReady = true;
        mapWebView.Navigated -= OnWebViewNavigated;

        try
        {
            await Task.Delay(1500);                    // Đợi Leaflet + leaflet-heat load xong
            await _viewModel.GenerateNewDataAsync();   // Tạo dữ liệu mô phỏng
            await UpdateHeatmapOnMapAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Lỗi Heatmap", $"Không thể tải bản đồ:\n{ex.Message}", "OK");
        }
    }

    protected override void OnDisappearing()
    {
        mapWebView.Navigated -= OnWebViewNavigated;
        base.OnDisappearing();
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HeatmapPageModel.Locations) && _isMapReady)
        {
            try
            {
                await UpdateHeatmapOnMapAsync();
            }
            catch { }   // Không để lỗi JS làm văng app
        }
    }

    private async Task UpdateHeatmapOnMapAsync()
    {
        if (_viewModel.Locations == null || _viewModel.Locations.Count == 0)
            return;

        try
        {
            var jsonData = JsonSerializer.Serialize(_viewModel.Locations);
            string script = $"updateHeatmap({jsonData});";

            // ← Dòng quan trọng: phải là EvaluateJavaScriptAsync (J hoa)
            await mapWebView.EvaluateJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EvaluateJavaScriptAsync error: {ex.Message}");
        }
    }

    private string GetHeatmapHtml() => @"
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset='utf-8' />
            <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
            <title>Heatmap Vĩnh Khánh</title>
            <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
            <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
            <script src='https://unpkg.com/leaflet.heat@0.2.0/dist/leaflet-heat.js'></script>
            <style>
                html, body { margin:0; padding:0; height:100%; overflow:hidden; }
                #map { width:100%; height:100vh; }
            </style>
        </head>
        <body>
            <div id='map'></div>
            <script>
                var map = L.map('map').setView([10.7625, 106.705], 16);
                // OSM tile servers có policy yêu cầu `Referer` nên khi tải qua WebView(Html = ...) thường bị chặn.
                // Dùng provider tile khác để tránh bị Access blocked.
                L.tileLayer('https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png', {
                    attribution: '&copy; OpenStreetMap contributors &copy; CARTO'
                }).addTo(map);

                var heatLayer = null;

                window.updateHeatmap = function(data) {
                    if (heatLayer) map.removeLayer(heatLayer);
                    var points = data.map(p => [p.Latitude, p.Longitude, p.Intensity || 0.8]);
                    heatLayer = L.heatLayer(points, { 
                        radius: 25, 
                        blur: 15, 
                        maxZoom: 17,
                        gradient: {0.4: 'blue', 0.6: 'lime', 0.8: 'yellow', 1.0: 'red'}
                    }).addTo(map);
                };
            </script>
        </body>
        </html>";
}