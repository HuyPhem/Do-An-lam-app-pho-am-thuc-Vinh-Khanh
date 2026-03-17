namespace TourGuideApp2.Pages;

public partial class TaskDetailPage : ContentPage
{
    public TaskDetailPage()
    {
        InitializeComponent();
        BindingContext = new TaskDetailPageModel();
    }
}