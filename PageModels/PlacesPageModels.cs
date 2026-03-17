using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TourGuideApp2.Models;
using System.Collections.ObjectModel;

namespace TourGuideApp2.PageModels;

public partial class PlacesPageModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<Place> places = new();

    public PlacesPageModel()
    {
        LoadPlaces();
    }

    private void LoadPlaces()
    {
        // Danh sách quán (hardcode)
        Places = new ObservableCollection<Place>
        {
            new Place { Name = "Ốc Oanh", Address = "534 Vĩnh Khánh", Specialty = "Ốc lạ (Michelin)", Latitude = 10.7590, Longitude = 106.7025, ImageUrl = "oc_oanh.jpg" },
            // Thêm các quán khác như trước...
            new Place { Name = "Ốc Đào", Address = "123 Vĩnh Khánh", Specialty = "Bạch tuộc chiên bơ", Latitude = 10.7582, Longitude = 106.7018, ImageUrl = "oc_dao.jpg" },
            // ... thêm đủ 8-10 quán
        };
    }

    [RelayCommand]
    private async Task GoToDetail(Place place)
    {
        var param = new Dictionary<string, object> { { "Place", place } };
        await Shell.Current.GoToAsync(nameof(ProjectDetailPage), param);
    }
}