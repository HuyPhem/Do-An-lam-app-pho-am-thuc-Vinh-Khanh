using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using TourGuideApp2.PageModels;
using TourGuideApp2.Services;
using ZXing.Net.Maui.Controls;

namespace TourGuideApp2;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddSingleton<ISimulationService, SimulationService>();
        builder.Services.AddSingleton<HeatmapPageModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}