using Microsoft.UI.Xaml;

namespace WebViewLoginSpike;

/// <summary>
/// Điểm khởi động của ứng dụng spike. Chỉ tạo và kích hoạt MainWindow.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
