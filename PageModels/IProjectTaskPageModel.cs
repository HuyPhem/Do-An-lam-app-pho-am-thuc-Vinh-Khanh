using CommunityToolkit.Mvvm.Input;
using TourGuideApp2.Models;

namespace TourGuideApp2.PageModels
{
    public interface IProjectTaskPageModel
    {
        IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
        bool IsBusy { get; }
    }
}