using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace WebViewLoginSpike;

/// <summary>
/// ViewModel cho MainWindow. Chỉ chứa trạng thái hiển thị/chẩn đoán,
/// không xử lý logic WebView2 (nằm trong code-behind vì cần truy cập trực tiếp CoreWebView2).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Warning { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity WarningSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>
    /// Bảng chẩn đoán chỉ dành cho dev. Ẩn mặc định (kể cả bản Debug) để không lộ
    /// thông tin không cần thiết với người dùng — bấm Ctrl+Shift+D để bật khi cần gỡ lỗi
    /// (xem MainWindow.xaml.cs).
    /// </summary>
    [ObservableProperty]
    public partial bool IsDiagnosticsVisible { get; set; }

    [ObservableProperty]
    public partial string RuntimeVersion { get; set; } = "(chưa xác định)";

    [ObservableProperty]
    public partial string BrowserPid { get; set; } = "(chưa xác định)";

    [ObservableProperty]
    public partial string UserDataFolder { get; set; } = "(chưa xác định)";

    [ObservableProperty]
    public partial string CurrentUrl { get; set; } = "(chưa điều hướng)";
}
