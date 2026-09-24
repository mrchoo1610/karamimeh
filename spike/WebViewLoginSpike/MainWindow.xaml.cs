using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace WebViewLoginSpike;

/// <summary>
/// Spike: kiểm tra khả năng đăng nhập Google/YouTube trong WebView2 nhúng,
/// duy trì phiên đăng nhập qua các lần khởi động lại, và đăng xuất.
/// Không xử lý mật khẩu, không đọc/log cookie, không chèn script vào accounts.google.com.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.youtube.com",
        "youtube.com",
        "m.youtube.com",
        "accounts.youtube.com",
        "accounts.google.com",
        "consent.youtube.com",
        "consent.google.com",
        // Luồng đăng nhập Google có thể redirect qua các host này (SetSID, xác minh 2 bước)
        "accounts.google.com.vn",
        "www.google.com",
        "www.google.com.vn",
        "myaccount.google.com",
    };

    private const string HomeUrl = "https://www.youtube.com/";

    // Script bơm vào mọi document mới. Chỉ báo cáo trạng thái đăng nhập từ trang
    // www.youtube.com ở top-level frame; không chạy trên accounts.google.com.
    private const string AuthBridgeScript = """
        (function () {
            if (window.top !== window || location.hostname !== 'www.youtube.com') {
                return;
            }

            function reportAuthState() {
                try {
                    var loggedIn;
                    var fromConfig = (window.ytcfg && window.ytcfg.get) ? window.ytcfg.get('LOGGED_IN') : undefined;
                    if (typeof fromConfig === 'boolean') {
                        loggedIn = fromConfig;
                    } else {
                        loggedIn = !!document.querySelector('#avatar-btn');
                    }
                    window.chrome.webview.postMessage({ type: 'auth', loggedIn: loggedIn });
                } catch (e) {
                    // bỏ qua lỗi, không phải là thông tin quan trọng cho spike này
                }
            }

            window.addEventListener('yt-navigate-finish', reportAuthState);
            setTimeout(reportAuthState, 2000);
            setTimeout(reportAuthState, 5000);
        })();
        """;

    public MainViewModel ViewModel { get; } = new();

    private AppWindow? _appWindow;
    private bool _hasCompletedFirstNavigation;

    public MainWindow()
    {
        InitializeComponent();
        Title = "karamimeh — WebView2 Login Spike";

        InitializeAppWindow();
        _ = InitializeWebViewAsync();
    }

    private void InitializeAppWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow?.Resize(new SizeInt32(1280, 800));

        // Khoá kích thước tối thiểu để layout toolbar/mixer không bị vỡ (xem báo cáo UX/UI).
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1024;
            presenter.PreferredMinimumHeight = 640;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (IsWebView2RuntimeMissing(ex))
        {
            ShowNotice("Cần cài Microsoft Edge WebView2 Runtime để chạy ứng dụng này.", InfoBarSeverity.Error);
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaraokeMixer",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        };

        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
        }
        catch (Exception ex) when (IsWebView2RuntimeMissing(ex))
        {
            ShowNotice("Cần cài Microsoft Edge WebView2 Runtime để chạy ứng dụng này.", InfoBarSeverity.Error);
            return;
        }

        await WebView.EnsureCoreWebView2Async(env);

        var core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        ViewModel.RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        ViewModel.BrowserPid = core.BrowserProcessId.ToString();
        ViewModel.UserDataFolder = userDataFolder;

        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.ContainsFullScreenElementChanged += Core_ContainsFullScreenElementChanged;
        core.HistoryChanged += Core_HistoryChanged;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(AuthBridgeScript);

        WebView.Source = new Uri(HomeUrl);
    }

    // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) = 0x80070002: mã lỗi WebView2 SDK trả về
    // khi không tìm thấy WebView2 Runtime đã cài đặt trên máy.
    private static bool IsWebView2RuntimeMissing(Exception ex) => unchecked((uint)ex.HResult) == 0x80070002;

    private static bool IsAllowedUri(string uriString)
    {
        if (string.Equals(uriString, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllowedHosts.Contains(uri.Host);
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        bool allowed = IsAllowedUri(e.Uri);
        Debug.WriteLine($"[NavigationStarting] {e.Uri} -> {(allowed ? "ALLOW" : "BLOCK")}");

        if (!allowed)
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    /// Chỉ mở http/https ra trình duyệt ngoài; chặn các scheme khác (ms-settings:, file:, protocol handler tuỳ ý)
    private void OpenExternal(string uriString)
    {
        if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            ShowNotice($"Đã mở liên kết trong trình duyệt ({uri.Host}).", InfoBarSeverity.Informational);
            _ = Launcher.LaunchUriAsync(uri);
        }
        else
        {
            Debug.WriteLine($"[OpenExternal] Blocked scheme: {uriString}");
        }
    }

    private void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        ViewModel.CurrentUrl = sender.Source;

        if (!_hasCompletedFirstNavigation)
        {
            _hasCompletedFirstNavigation = true;
            ViewModel.IsLoading = false;
        }

        if (!e.IsSuccess)
        {
            // Chi tiết kỹ thuật chỉ ghi log dev; người dùng chỉ thấy thông báo ngắn gọn kèm hành động khắc phục.
            Debug.WriteLine($"[NavigationCompleted] WebErrorStatus={e.WebErrorStatus} Url={sender.Source}");
            if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                ShowNotice("Không tải được trang. Kiểm tra kết nối mạng rồi thử lại.", InfoBarSeverity.Error);
            }
        }

        if (Uri.TryCreate(sender.Source, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            string title = sender.DocumentTitle ?? string.Empty;
            bool rejected = uri.AbsolutePath.Contains("/signin/rejected", StringComparison.OrdinalIgnoreCase)
                || title.Contains("may not be secure", StringComparison.OrdinalIgnoreCase)
                || title.Contains("không an toàn", StringComparison.OrdinalIgnoreCase);

            if (rejected)
            {
                ShowNotice(
                    "Google đang tạm chặn đăng nhập trong ứng dụng. Thử lại sau hoặc đăng nhập bằng cách khác.",
                    InfoBarSeverity.Error);
            }
        }
    }

    private void Core_HistoryChanged(CoreWebView2 sender, object args)
    {
        ViewModel.CanGoBack = sender.CanGoBack;
        ViewModel.CanGoForward = sender.CanGoForward;
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (IsAllowedUri(e.Uri))
        {
            sender.Navigate(e.Uri);
        }
        else
        {
            OpenExternal(e.Uri);
        }
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var sourceUri) ||
            !string.Equals(sourceUri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String &&
                typeProp.GetString() == "auth" &&
                root.TryGetProperty("loggedIn", out var loggedInProp) &&
                (loggedInProp.ValueKind == JsonValueKind.True || loggedInProp.ValueKind == JsonValueKind.False))
            {
                ViewModel.IsSignedIn = loggedInProp.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[WebMessageReceived] Bỏ qua message không hợp lệ: {ex.Message}");
        }
    }

    private void Core_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.SetPresenter(sender.ContainsFullScreenElement
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        ViewModel.WarningSeverity = severity;
        ViewModel.Warning = message;
    }

    private void WarningInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.Warning = null;
    }

    private void ToggleDiagnostics_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.IsDiagnosticsVisible = !ViewModel.IsDiagnosticsVisible;
        args.Handled = true;
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join(Environment.NewLine,
        [
            FormatRuntime(ViewModel.RuntimeVersion),
            FormatPid(ViewModel.BrowserPid),
            FormatUdf(ViewModel.UserDataFolder),
            FormatUrl(ViewModel.CurrentUrl),
            FormatSignIn(ViewModel.IsSignedIn),
        ]);

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoBack == true)
        {
            WebView.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoForward == true)
        {
            WebView.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.Reload();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.Navigate(HomeUrl);
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is null)
        {
            return;
        }

        string continueUrl = Uri.EscapeDataString("https://www.youtube.com/");
        string signInUrl = $"https://accounts.google.com/ServiceLogin?service=youtube&continue={continueUrl}";
        WebView.CoreWebView2.Navigate(signInUrl);
    }

    private async void SignOutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is null)
        {
            return;
        }

        // Xoá toàn bộ profile (cookie, cache, lịch sử) là hành động không thể hoàn tác — xác nhận trước khi làm.
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Đăng xuất khỏi YouTube?",
            Content = "Cookie và lịch sử duyệt trong ứng dụng sẽ bị xoá.",
            PrimaryButtonText = "Đăng xuất",
            CloseButtonText = "Huỷ",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
        WebView.CoreWebView2.Navigate(HomeUrl);
        ViewModel.IsSignedIn = false;
    }

    // Hàm hỗ trợ cho x:Bind function binding (thay cho lớp converter riêng).
    public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public bool HasWarning(string? warning) => !string.IsNullOrEmpty(warning);

    public string FormatRuntime(string value) => $"WebView2 Runtime: {value}";

    public string FormatPid(string value) => $"Browser PID: {value}";

    public string FormatUdf(string value) => $"UDF: {value}";

    public string FormatUrl(string value) => $"URL: {value}";

    public string FormatSignIn(bool signedIn) => signedIn ? "Trạng thái: Đã đăng nhập" : "Trạng thái: Chưa đăng nhập";
}
