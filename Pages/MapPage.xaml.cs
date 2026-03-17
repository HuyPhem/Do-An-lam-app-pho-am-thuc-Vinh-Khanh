using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace TourGuideApp2;

public partial class MapPage : ContentPage
{
    public MapPage()
    {
        InitializeComponent();

        var location = new Location(10.7579, 106.7043);

        map.MoveToRegion(
            MapSpan.FromCenterAndRadius(
                location,
                Distance.FromMeters(200)));

        map.Pins.Add(new Pin
        {
            Label = "Phố Vĩnh Khánh",
            Location = location
        });
    }
}