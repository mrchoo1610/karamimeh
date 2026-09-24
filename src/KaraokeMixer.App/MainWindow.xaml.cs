using System.Diagnostics;
using System.IO;
using System.Text.Json;
using KaraokeMixer.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;

namespace KaraokeMixer.App;

/// <summary>
/// Main window: WebView2 YouTube sign-in/playback (carried over from WebViewLoginSpike, same
/// allowlist/auth-bridge/sign-in-out behavior) on the left, karaoke mixer control panel (new,
/// wired to KaraokeMixer.Core.AudioMixerCore) on the right.
///
/// This is a first functional pass, not the final UX redesign — see the task this was built
/// against for that separation of concerns.
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
        "accounts.google.com.vn",
        "www.google.com",
        "www.google.com.vn",
        "myaccount.google.com",
    };

    private const string HomeUrl = "https://www.youtube.com/";

    // Script bơm vào mọi document mới — carried over unchanged from WebViewLoginSpike. Chỉ báo cáo
    // trạng thái đăng nhập từ trang www.youtube.com ở top-level frame; không chạy trên
    // accounts.google.com.
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
                    // bỏ qua lỗi, không phải là thông tin quan trọng
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
    private uint _browserProcessId;

    private readonly AudioMixerCore _engine = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _peakMeterTimer;
    private int _peakLogTickCount;

    public MainWindow()
    {
        InitializeComponent();
        Title = "karamimeh — Karaoke Mixer";

        AppendLog("=== Khởi động KaraokeMixer.App ===");
        InitializeAppWindow();
        PopulateMicDevices();
        PopulateOutputDevices();
        StartPeakMeterTimer();
        _ = InitializeWebViewAsync();
    }

    private void InitializeAppWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow?.Resize(new Windows.Graphics.SizeInt32(1400, 860));

        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1100;
            presenter.PreferredMinimumHeight = 650;
        }
    }

    // --- Mic device picker ------------------------------------------------------------------

    private void PopulateMicDevices()
    {
        ViewModel.MicDevices.Add(new MicDeviceOption("(Mặc định hệ thống)", null));

        string? defaultId = null;
        try
        {
            defaultId = AudioMixerCore.GetDefaultMicDeviceId();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicDevices] GetDefaultMicDeviceId lỗi: {ex}");
        }

        IReadOnlyList<MicDeviceInfo> devices;
        try
        {
            devices = AudioMixerCore.EnumerateMicDevices();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicDevices] EnumerateMicDevices lỗi: {ex}");
            devices = [];
        }

        foreach (var device in devices)
        {
            ViewModel.MicDevices.Add(new MicDeviceOption(device.Name, device.Id));
        }

        // Default selection: the explicit "system default" entry — NOT any specific hardcoded
        // device. There is currently no fixed dev mic (wired headset broke; could be a laptop mic,
        // a Bluetooth device via a Creative BT-W6 dongle, or something else) so the picker must
        // never assume one.
        ViewModel.SelectedMicDevice = ViewModel.MicDevices[0];
        _ = defaultId; // only used above for a future "pre-select the actual default device" tweak

        AppendLog($"[MicDevices] {devices.Count} thiết bị + mục mặc định hệ thống.");
    }

    private void PopulateOutputDevices()
    {
        ViewModel.OutputDevices.Add(new OutputDeviceOption("(Mặc định hệ thống)", null));

        IReadOnlyList<MicDeviceInfo> devices;
        try
        {
            devices = AudioMixerCore.EnumerateOutputDevices();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OutputDevices] EnumerateOutputDevices lỗi: {ex}");
            AppendLog($"[OutputDevices] Lỗi liệt kê thiết bị: {ex.Message}");
            devices = [];
        }

        foreach (var device in devices)
        {
            ViewModel.OutputDevices.Add(new OutputDeviceOption(device.Name, device.Id));
        }

        // Same rationale as mic: default to "system default", never assume a specific speaker —
        // the user may be on AUX or a Bluetooth dongle (e.g. Creative BT-W6) at any given time.
        ViewModel.SelectedOutputDevice = ViewModel.OutputDevices[0];

        AppendLog($"[OutputDevices] {devices.Count} thiết bị + mục mặc định hệ thống.");
    }

    // --- Peak meters --------------------------------------------------------------------------

    private void StartPeakMeterTimer()
    {
        _peakMeterTimer = DispatcherQueue.CreateTimer();
        _peakMeterTimer.Interval = TimeSpan.FromMilliseconds(100);
        _peakMeterTimer.Tick += (_, _) =>
        {
            if (!ViewModel.IsEngineRunning)
            {
                return;
            }

            // Music/YouTube no longer passes through this engine at all (see AudioMixerCore's
            // class remarks — Windows mixes it natively), so there is no music peak to show here
            // anymore; only mic and the mic-only master output.
            double micPercent = Math.Clamp(_engine.MicPeakLevel, 0f, 1f) * 100.0;
            double masterPercent = Math.Clamp(_engine.MasterPeakLevel, 0f, 1f) * 100.0;

            ViewModel.MicPeakPercent = micPercent;
            ViewModel.MasterPeakPercent = masterPercent;

            // Meter track is 328px wide (360 panel width - 16*2 padding); scale by percentage.
            const double meterTrackWidth = 328;
            MicPeakBar.Width = meterTrackWidth * (micPercent / 100.0);
            MasterPeakBar.Width = meterTrackWidth * (masterPercent / 100.0);

            // Low-rate log sample, kept for future diagnosis of mic-side issues.
            _peakLogTickCount++;
            if (_peakLogTickCount % 20 == 0) // every ~2s at the timer's 100ms interval
            {
                AppendLog($"[Peak] mic={micPercent:F0}% master={masterPercent:F0}%");
            }
        };
        _peakMeterTimer.Start();
    }

    // --- WebView2 setup (from WebViewLoginSpike) ----------------------------------------------

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

        _browserProcessId = (uint)core.BrowserProcessId;

        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.ContainsFullScreenElementChanged += Core_ContainsFullScreenElementChanged;
        core.HistoryChanged += Core_HistoryChanged;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(AuthBridgeScript);

        WebView.Source = new Uri(HomeUrl);
    }

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
        if (!_hasCompletedFirstNavigation)
        {
            _hasCompletedFirstNavigation = true;
            ViewModel.IsLoading = false;
        }

        if (!e.IsSuccess)
        {
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

    // --- Mixer engine wiring -------------------------------------------------------------------

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            AppendLog("[Start/Stop] Bấm Dừng.");
            _engine.Stop();
            ViewModel.IsEngineRunning = false;
            ViewModel.EngineStatus = "Đã dừng";
            ViewModel.MicPeakPercent = 0;
            ViewModel.MasterPeakPercent = 0;
            MicPeakBar.Width = 0;
            MasterPeakBar.Width = 0;
            _peakLogTickCount = 0;
            StartStopButton.Content = "Bắt đầu";
            AppendLog("[Start/Stop] Đã dừng xong (đã khôi phục volume gốc của session YouTube).");
            return;
        }

        if (_browserProcessId == 0)
        {
            ShowNotice("WebView2 chưa khởi tạo xong — thử lại sau vài giây.", InfoBarSeverity.Warning);
            return;
        }

        if (ViewModel.IsEngineStarting)
        {
            return;
        }

        ViewModel.IsEngineStarting = true;
        ViewModel.EngineStatus = "Đang khởi động…";
        StartStopButton.IsEnabled = false;

        string? micDeviceId = ViewModel.SelectedMicDevice?.DeviceId;
        string? outputDeviceId = ViewModel.SelectedOutputDevice?.DeviceId;
        AppendLog($"[Start/Stop] Bấm Bắt đầu. mic={ViewModel.SelectedMicDevice?.DisplayName ?? "(null)"} output={ViewModel.SelectedOutputDevice?.DisplayName ?? "(null)"} browserProcessId={_browserProcessId}");

        EngineStartResult result;
        try
        {
            result = await _engine.StartAsync(micDeviceId, _browserProcessId, outputDeviceId);
        }
        catch (Exception ex)
        {
            result = EngineStartResult.Fail("StartAsync (unexpected exception)", ex.Message);
        }

        StartStopButton.IsEnabled = true;
        ViewModel.IsEngineStarting = false;

        if (!result.Success)
        {
            ViewModel.EngineStatus = $"Lỗi: {result.Stage} — {result.Detail}";
            ShowNotice($"Không thể bắt đầu bộ trộn: {result.Stage}. {result.Detail}", InfoBarSeverity.Error);
            AppendLog($"[Start/Stop] StartAsync THẤT BẠI: stage={result.Stage} detail={result.Detail}");
            return;
        }

        // Apply current UI slider values to the newly-started engine (sliders may have been moved
        // before Start was clicked).
        ApplyAllParametersToEngine();

        ViewModel.IsEngineRunning = true;
        ViewModel.EngineStatus = "Đang chạy";
        StartStopButton.Content = "Dừng";
        AppendLog($"[Start/Stop] StartAsync THÀNH CÔNG — đang chạy. Số session YouTube đã chỉnh volume: {_engine.LastMusicSessionMatchedCount} " +
            (_engine.LastMusicSessionMatchedCount == 0
                ? "(0 có thể là bình thường nếu video YouTube CHƯA phát tiếng nào tại thời điểm bấm Bắt đầu — Windows chỉ tạo session sau khi video đã phát ít nhất 1 lần; kéo lại thanh trượt Nhạc sau khi video đã phát để áp dụng)"
                : "(đã chỉnh được ít nhất 1 session)"));

        // Nhạc YouTube giờ luôn phát ra thiết bị MẶC ĐỊNH của Windows (WebView2 luôn theo mặc định
        // hệ thống — xem AudioMixerCore's class remarks). Nếu người dùng chọn 1 thiết bị output khác
        // mặc định cho riêng mic, 2 luồng sẽ ra 2 thiết bị khác nhau — cảnh báo rõ thay vì im lặng.
        string? selectedOutputId = ViewModel.SelectedOutputDevice?.DeviceId;
        if (!string.IsNullOrEmpty(selectedOutputId) && selectedOutputId != AudioMixerCore.GetDefaultOutputDeviceId())
        {
            ShowNotice(
                "Đang chọn thiết bị output khác mặc định hệ thống — nhạc YouTube vẫn phát ra thiết bị mặc định, còn mic phát ra thiết bị vừa chọn. Để nghe cả 2 cùng chỗ, chọn lại '(Mặc định hệ thống)' hoặc đổi thiết bị mặc định của Windows.",
                InfoBarSeverity.Warning);
        }
    }

    // --- Debug log (DEBUG-only, mirrors the pattern already used by both spikes) --------------
#if DEBUG
    private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "KaraokeMixer.App.debuglog.txt");
#endif

    private void AppendLog(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        Debug.WriteLine(stamped);
#if DEBUG
        try
        {
            File.AppendAllText(DebugLogPath, stamped + Environment.NewLine);
        }
        catch
        {
            // best-effort diagnostic aid only — never let logging itself break the app
        }
#endif
    }

    private void ApplyAllParametersToEngine()
    {
        _engine.MicVolume = (float)(ViewModel.MicVolumePercent / 100.0);
        _engine.MusicVolume = (float)(ViewModel.MusicVolumePercent / 100.0);
        _engine.EqLowGainDb = (float)ViewModel.EqLowDb;
        _engine.EqMidGainDb = (float)ViewModel.EqMidDb;
        _engine.EqHighGainDb = (float)ViewModel.EqHighDb;
        _engine.EchoEnabled = ViewModel.EchoEnabled;
        _engine.EchoDelayMilliseconds = (int)ViewModel.EchoDelayMs;
        _engine.EchoFeedback = (float)(ViewModel.EchoFeedbackPercent / 100.0);
        _engine.EchoWetDryMix = (float)(ViewModel.EchoMixPercent / 100.0);
    }

    private void MicVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.MicVolume = (float)(e.NewValue / 100.0);
        }
    }

    private void MusicVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.MusicVolume = (float)(e.NewValue / 100.0);
        }
    }

    private void EqLowSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EqLowGainDb = (float)e.NewValue;
        }
    }

    private void EqMidSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EqMidGainDb = (float)e.NewValue;
        }
    }

    private void EqHighSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EqHighGainDb = (float)e.NewValue;
        }
    }

    private void EchoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EchoEnabled = EchoToggle.IsOn;
        }
    }

    private void EchoDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EchoDelayMilliseconds = (int)e.NewValue;
        }
    }

    private void EchoFeedbackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EchoFeedback = (float)(e.NewValue / 100.0);
        }
    }

    private void EchoMixSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.IsEngineRunning)
        {
            _engine.EchoWetDryMix = (float)(e.NewValue / 100.0);
        }
    }

    // Hàm hỗ trợ cho x:Bind function binding.
    public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public bool HasWarning(string? warning) => !string.IsNullOrEmpty(warning);

    public string FormatStatus(string value) => $"Trạng thái: {value}";

    public string FormatPercent(string label, double value) => $"{label}: {value:F0}%";

    public string FormatDb(string label, double value) => $"{label}: {value:+0.0;-0.0;0.0} dB";

    public string FormatMs(string label, double value) => $"{label}: {value:F0} ms";
}
