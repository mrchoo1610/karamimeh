using CommunityToolkit.Mvvm.ComponentModel;

namespace ProcessLoopbackSpike;

/// <summary>
/// ViewModel cho MainWindow. Chỉ chứa trạng thái hiển thị — logic WebView2/audio nằm trong
/// code-behind (cần truy cập trực tiếp CoreWebView2 và các đối tượng COM/native trong Audio/).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string BrowserProcessId { get; set; } = "(chưa xác định)";

    [ObservableProperty]
    public partial string ProcessTreeInfo { get; set; } = "(chưa dò)";

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial string CaptureStatus { get; set; } = "Chưa bắt đầu";

    [ObservableProperty]
    public partial string MuteStatus { get; set; } = "Chưa mute";

    [ObservableProperty]
    public partial string MutedPidsInfo { get; set; } = "(chưa mute lần nào)";

    [ObservableProperty]
    public partial double PeakLevelPercent { get; set; }

    [ObservableProperty]
    public partial string PeakLevelText { get; set; } = "0%";

    [ObservableProperty]
    public partial string InitializeFlagsInfo { get; set; } = "(chưa Initialize)";

    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;
}
