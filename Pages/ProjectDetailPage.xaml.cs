using System.ComponentModel;
using TourGuideApp2.Models;  // namespace Models chứa Place.cs

namespace TourGuideApp2.Pages;

[QueryProperty(nameof(SelectedPlace), "Place")]
public partial class ProjectDetailPage : ContentPage, INotifyPropertyChanged
{
    private Place _selectedPlace = new Place();

    public Place SelectedPlace
    {
        get => _selectedPlace;
        set
        {
            if (_selectedPlace != value)
            {
                _selectedPlace = value;
                OnPropertyChanged(nameof(SelectedPlace));
            }
        }
    }

    public ProjectDetailPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    // Sửa warning: dùng 'new' để hide event của base class (BindableObject)
    public new event PropertyChangedEventHandler? PropertyChanged;

    // Sửa warning: dùng 'new' cho method OnPropertyChanged
    protected new void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}