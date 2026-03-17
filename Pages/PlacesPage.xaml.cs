namespace TourGuideApp2.Pages;

public partial class PlacesPage : ContentPage
{
    public PlacesPage(PlacesPageModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}