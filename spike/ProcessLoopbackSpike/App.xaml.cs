using Microsoft.UI.Xaml;

namespace ProcessLoopbackSpike;

/// <summary>
/// Điểm khởi động của ứng dụng spike P1 (process-loopback capture + mute session).
/// Chỉ tạo và kích hoạt MainWindow, không có logic nghiệp vụ nào khác.
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
