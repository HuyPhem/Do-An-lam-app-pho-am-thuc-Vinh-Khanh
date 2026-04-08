using Microsoft.Maui.ApplicationModel;
using TourGuideApp2.Services;

namespace TourGuideApp2;

public partial class AppShell : Shell
{
    private bool _isShowingLoginGuardAlert;

    public AppShell()
    {
        InitializeComponent();
    }

    protected override void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);

        if (AuthService.IsLoggedIn)
            return;

        var target = args.Target?.Location.OriginalString ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
            return;

        var normalizedTarget = target.ToLowerInvariant();
        var isRestrictedTab =
            normalizedTarget.Contains("//map") ||
            normalizedTarget.Contains("//intro") ||
            normalizedTarget.Contains("//history") ||
            normalizedTarget.Contains("//heatmap") ||
            normalizedTarget.Contains("//qrdemo");

        if (!isRestrictedTab)
            return;

        args.Cancel();
        if (_isShowingLoginGuardAlert)
            return;

        _isShowingLoginGuardAlert = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("Yêu cầu đăng nhập", "Vui lòng đăng nhập trước khi dùng chức năng này.", "OK");
            _isShowingLoginGuardAlert = false;
        });
    }
}