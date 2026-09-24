using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;
using ProcessLoopbackSpike.Audio;
using ProcessLoopbackSpike.Web;
using WinRT.Interop;

namespace ProcessLoopbackSpike;

/// <summary>
/// Spike P1: process-loopback capture (WebView2) + mute session gốc + phát lại qua WasapiOut riêng.
/// Xem README.md để biết checklist test thủ công (T1-T4) và kết luận.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private AppWindow? _appWindow;
    private uint _browserProcessId;
    private ProcessLoopbackCapture? _capture;
    private WasapiOut? _wasapiOut;

    public MainWindow()
    {
        InitializeComponent();
        Title = "karamimeh — ProcessLoopbackSpike (P1)";

        InitializeAppWindow();
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
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 600;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        AppendLog("Đang khởi tạo WebView2...");

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaraokeMixer",
            "ProcessLoopbackSpike",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            // Không cần --autoplay-policy=no-user-gesture-required vì trang test-tone của spike
            // này CỐ Ý yêu cầu user click nút Play thật (đúng thiết kế Web Audio API), để việc
            // start/stop tone là hành động rõ ràng, dễ so khớp với checklist T1-T4 trong README.
        };

        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
        }
        catch (Exception ex) when (unchecked((uint)ex.HResult) == 0x80070002)
        {
            AppendLog("LỖI: Cần cài Microsoft Edge WebView2 Runtime để chạy spike này.");
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
        ViewModel.BrowserProcessId = _browserProcessId.ToString();
        AppendLog($"CoreWebView2.BrowserProcessId = {_browserProcessId}");

        DumpProcessTree();

        core.NavigateToString(TestTonePage.Html);
        core.NavigationCompleted += (s, e) => AppendLog($"[Nav] Đã tải xong: {s.Source} (success={e.IsSuccess})");

#if DEBUG
        // 3 hook dev-only, TẮT mặc định (đều đọc biến môi trường, không hook nào bật nếu không set)
        // — tương hỗ lẫn nhau (chỉ 1 chạy, ưu tiên theo thứ tự bên dưới) để tránh 2 luồng capture
        // chồng nhau trong cùng 1 lần chạy app:
        //   1) SPIKE_REPRO_TIMING_GAP=<số ms>: kịch bản THUẦN TIMING (không navigation) — lặp
        //      Start->giữ 800ms->Stop->chờ gapMs, N chu kỳ. ĐÃ XÁC NHẬN bằng hook này: bug "Start
        //      thất bại sau navigate" KHÔNG liên quan navigation — chu kỳ #1 luôn thành công, MỌI
        //      chu kỳ sau đó luôn thất bại 100%, giống hệt nhau ở gap=500/1000/2000/5000ms (xem
        //      README.md). Fix thật (GC.Collect() sau Stop, xem StopCaptureAndPlayback()) đã được
        //      áp dụng mặc định cho MỌI build — hook này giữ lại làm công cụ hồi quy nhanh, không
        //      cần navigation/mạng.
        //   2) SPIKE_REPRO_NAV_BUG=reload|diff|1: kịch bản tái hiện bug "Start capture thất bại sau
        //      khi navigate lại WebView2" (xem README.md mục "Phát hiện quan trọng"). "reload" =
        //      điều hướng lại ĐÚNG url cũ (giống repro gốc của user thật); "diff" = điều hướng sang
        //      1 video khác hẳn (phân biệt xem bug có phụ thuộc reload đúng URL hay không). "1"
        //      tương đương "reload".
        //   3) SPIKE_AUTOSTART_CAPTURE=1: hook gốc — tự bấm Start/mute/unmute để agent kiểm chứng
        //      không cần click UI thật (xem README.md mục "Ghi chú thêm").
        string? timingGapStr = Environment.GetEnvironmentVariable("SPIKE_REPRO_TIMING_GAP");
        string? reproMode = Environment.GetEnvironmentVariable("SPIKE_REPRO_NAV_BUG");
        bool autostartCapture = Environment.GetEnvironmentVariable("SPIKE_AUTOSTART_CAPTURE") == "1";

        if (!string.IsNullOrEmpty(timingGapStr) && int.TryParse(timingGapStr, out int timingGapMs))
        {
            AppendLog($"[REPRO-TIMING] SPIKE_REPRO_TIMING_GAP={timingGapMs}ms — chạy kịch bản Start/Stop lặp nhanh, KHÔNG navigation.");
            await Task.Delay(500);
            await RunTimingGapReproAsync(timingGapMs, cycles: 6);
        }
        else if (!string.IsNullOrEmpty(reproMode))
        {
            string scenario = reproMode == "1" ? "reload" : reproMode;
            AppendLog($"[REPRO] SPIKE_REPRO_NAV_BUG={reproMode} — chạy kịch bản tái hiện bug (scenario={scenario}).");
            await Task.Delay(500);
            await RunNavBugReproAsync(scenario);
        }
        else if (autostartCapture)
        {
            AppendLog("[AUTOSTART] SPIKE_AUTOSTART_CAPTURE=1 — tự động bấm Bắt đầu capture để kiểm chứng.");
            await Task.Delay(500);

            AppendLog("[AUTOSTART] Kiểm chứng nhánh mute/unmute session (SessionMuter) trước...");
            MuteButton_Click(this, new RoutedEventArgs());
            await Task.Delay(300);
            UnmuteButton_Click(this, new RoutedEventArgs());
            await Task.Delay(300);

            await TryStartCaptureAsync();

            // Log định kỳ min/max/RMS + độ đầy buffer trong vài giây, CHỈ để agent tự kiểm chứng
            // vòng lặp capture thật sự sống liên tục (không bị treo sau gói đầu tiên) — không có
            // tone nào đang phát (không thể tự động click nút Play trong WebView2 vì autoplay
            // policy yêu cầu user-gesture thật), nên kỳ vọng đúng đắn là im lặng (peak ~ 0), KHÔNG
            // phải bằng chứng cho non-silence — việc đó phải test thủ công theo README.md.
            if (_capture is not null)
            {
                for (int i = 0; i < 6; i++)
                {
                    await Task.Delay(500);
                    AppendLog($"[AUTOSTART METER] peak%={ViewModel.PeakLevelText} bufferedMs={_capture.Buffer.BufferedDuration.TotalMilliseconds:F0} totalBytesCaptured={_capture.TotalBytesCaptured} isCapturing={_capture.IsCapturing}");
                }
            }

            // Test luôn đường Stop/cleanup rồi Start lại 1 lần nữa trong cùng phiên chạy này, để
            // agent tự kiểm tra không có exception/hang khi lặp lại chu kỳ start-stop (đúng tinh
            // thần checklist T4 trong README, dù không thay thế test thủ công thật của người dùng).
            AppendLog("[AUTOSTART] Gọi StopCaptureAndPlayback() lần 1...");
            StopCaptureAndPlayback();
            AppendLog("[AUTOSTART] Stop lần 1 xong. Chờ 3000ms rồi Start lại lần 2 để test lặp chu kỳ (chờ lâu hơn để loại trừ khả năng lỗi chỉ do race-condition khi restart quá nhanh)...");
            await Task.Delay(3000);
            await TryStartCaptureAsync();
            await Task.Delay(1000);
            AppendLog($"[AUTOSTART] Sau lần Start thứ 2: isCapturing={ViewModel.IsCapturing}, totalBytesCaptured={_capture?.TotalBytesCaptured}");
            AppendLog("[AUTOSTART] Gọi StopCaptureAndPlayback() lần 2...");
            StopCaptureAndPlayback();
            AppendLog("[AUTOSTART] Hoàn tất kiểm chứng tự động.");
        }
#endif
    }

    /// <summary>
    /// Ghi lại toàn bộ cây process con của BrowserProcessId ra log + ViewModel, để biết thực tế
    /// BrowserProcessId nằm ở đâu trong cây process Chromium/WebView2 (đây là 1 phát hiện quan
    /// trọng của spike — xem README.md).
    /// </summary>
    private void DumpProcessTree()
    {
        try
        {
            var pids = ProcessTreeHelper.GetProcessTreePids(_browserProcessId);
            var sb = new StringBuilder();
            sb.Append($"Cây process ({pids.Count} PID, gồm cả gốc {_browserProcessId}): ");
            sb.Append(string.Join(", ", pids.OrderBy(p => p)));

            ViewModel.ProcessTreeInfo = sb.ToString();
            AppendLog(sb.ToString());

            foreach (uint pid in pids.OrderBy(p => p))
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    AppendLog($"  PID {pid}: {proc.ProcessName}");
                }
                catch
                {
                    AppendLog($"  PID {pid}: (đã thoát hoặc không truy vấn được)");
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ProcessTreeInfo = $"Lỗi khi dò cây process: {ex.Message}";
            AppendLog(ViewModel.ProcessTreeInfo);
        }
    }

    // Điều hướng để test P1 với nội dung thật (video YouTube), thay vì chỉ tone tổng hợp.
    // Ghi chú: spike này KHÔNG có allowlist điều hướng như WebViewLoginSpike — đây là công cụ kỹ
    // thuật nội bộ để tự test, không phải giao diện cho người dùng cuối, nên cho điều hướng tự do.
    private void GoTestToneButton_Click(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.NavigateToString(TestTonePage.Html);
    }

    private void GoYouTubeButton_Click(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.Navigate("https://www.youtube.com/");
    }

    private void GoUrlButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        WebView.CoreWebView2?.Navigate(url);
    }

#if DEBUG
    // URL video YouTube thật dùng cho kịch bản tái hiện bug navigation (xem RunNavBugReproAsync).
    // A = video rất phổ biến/ổn định lâu dài (Rick Astley "Never Gonna Give You Up") — chọn vì khả
    // năng còn tồn tại trên YouTube trong nhiều năm tới gần như chắc chắn. B = "Me at the zoo", video
    // đầu tiên từng đăng lên YouTube — cũng gần như chắc chắn được giữ vĩnh viễn vì giá trị lịch sử.
    // Cả 2 đều là video công khai, có audio track thật (không phải màn hình câm) — đúng yêu cầu
    // "real audio-generating navigation" của kịch bản điều tra.
    private const string ReproUrlA = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string ReproUrlB = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    /// <summary>
    /// [DEBUG-only] Điều hướng CoreWebView2 tới <paramref name="url"/> và CHỜ đúng sự kiện
    /// NavigationCompleted của riêng lần điều hướng này (không phải chỉ Task.Delay đoán mò) —
    /// dùng TaskCompletionSource + handler tạm, gỡ handler ngay sau khi xong. Ghi lại thời gian
    /// thực tế đã chờ (elapsed) để tương quan với các mốc log khác.
    /// </summary>
    private async Task<(bool Success, double ElapsedMs)> NavigateAndAwaitCompletionAsync(string url, TimeSpan timeout)
    {
        var core = WebView.CoreWebView2;
        if (core is null)
        {
            AppendLog("[REPRO] CoreWebView2 null — không thể điều hướng.");
            return (false, 0);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);

        var sw = Stopwatch.StartNew();
        core.NavigationCompleted += Handler;
        try
        {
            AppendLog($"[REPRO] Navigate({url})...");
            core.Navigate(url);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            sw.Stop();

            if (completed != tcs.Task)
            {
                AppendLog($"[REPRO] TIMEOUT chờ NavigationCompleted sau {sw.Elapsed.TotalMilliseconds:F0}ms cho {url}.");
                return (false, sw.Elapsed.TotalMilliseconds);
            }

            bool ok = await tcs.Task;
            AppendLog($"[REPRO] NavigationCompleted success={ok}, elapsed={sw.Elapsed.TotalMilliseconds:F0}ms, url={url}");
            return (ok, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    /// <summary>
    /// [DEBUG-only] Ghi log cây process hiện tại + phân loại từng PID (renderer/gpu-process/audio
    /// service/...) qua CommandLine — dùng để kiểm chứng giả thuyết sửa lỗi #5 trong README.md (PID
    /// render audio có đổi identity qua navigation hay không).
    /// </summary>
    private HashSet<uint> LogProcessTreeSnapshot(string label)
    {
        var pids = ProcessTreeHelper.GetProcessTreePids(_browserProcessId);
        var types = ProcessTreeHelper.GetProcessTypes(pids);
        var sb = new StringBuilder();
        sb.Append($"[REPRO] Cây process [{label}] ({pids.Count} PID): ");
        sb.Append(string.Join(", ", pids.OrderBy(p => p).Select(p => types.TryGetValue(p, out var t) ? $"{p}[{t}]" : $"{p}[?]")));
        AppendLog(sb.ToString());
        return pids;
    }

    private void LogProcessTreeDiff(string label, HashSet<uint> before, HashSet<uint> after)
    {
        var added = after.Except(before).OrderBy(p => p).ToList();
        var removed = before.Except(after).OrderBy(p => p).ToList();
        AppendLog($"[REPRO] Diff cây process [{label}]: +mới={{{string.Join(",", added)}}} -mất={{{string.Join(",", removed)}}}");
    }

    /// <summary>
    /// [DEBUG-only] Kịch bản tái hiện bug "Start capture thất bại sau khi navigate lại WebView2"
    /// đúng theo trình tự người dùng thật đã làm thủ công (xem README.md mục "Phát hiện quan
    /// trọng"): navigate video thật -> Start->Stop (chu kỳ 1) -> Start->Stop (chu kỳ 2, KHÔNG
    /// navigate ở giữa) -> navigate lại (reload cùng URL, hoặc video khác tuỳ scenario) -> thử
    /// Start nhiều lần liên tiếp rồi có độ trễ tăng dần để xem có tự phục hồi không.
    ///
    /// KẾT LUẬN (sau khi điều tra — xem README.md): bug này hoá ra KHÔNG đặc thù cho navigation,
    /// mà là 1 vấn đề GC/COM-RCW thuần tuý (xem fix chính thức trong StopCaptureAndPlayback()).
    /// Kịch bản này vẫn được giữ lại vì nó tái hiện ĐÚNG trình tự người dùng thật đã báo cáo bằng
    /// video YouTube thật, dùng để xác nhận fix có giữ được qua navigation thật hay không (đã xác
    /// nhận CÓ — xem README.md).
    ///
    /// Điều khiển thêm bằng biến môi trường (đều DEBUG-only, không ảnh hưởng người dùng thật):
    ///   - SPIKE_FIX_NAV_SETTLE_MS=<số ms>: chờ thêm sau mỗi NavigationCompleted trước khi cho phép
    ///     Start (giả thuyết sửa lỗi #2 — ĐÃ BÁC BỎ, xem README.md: delay không ảnh hưởng kết quả).
    ///   - SPIKE_FIX_LOOPBACK_MODE=exclude: đọc trong TryStartCaptureAsync — dùng
    ///     EXCLUDE_TARGET_PROCESS_TREE thay vì INCLUDE (giả thuyết sửa lỗi #4 — ĐÃ BÁC BỎ, cùng
    ///     chữ ký lỗi xảy ra ở cả 2 mode).
    /// </summary>
    private async Task RunNavBugReproAsync(string scenario)
    {
        int navSettleMs = 0;
        if (int.TryParse(Environment.GetEnvironmentVariable("SPIKE_FIX_NAV_SETTLE_MS"), out int parsedSettle))
        {
            navSettleMs = parsedSettle;
        }

        AppendLog($"[REPRO] === BẮT ĐẦU kịch bản (scenario={scenario}, navSettleMs={navSettleMs}, " +
                  $"SPIKE_FIX_LOOPBACK_MODE={Environment.GetEnvironmentVariable("SPIKE_FIX_LOOPBACK_MODE") ?? "(không set, dùng INCLUDE)"}) ===");

        var treeBeforeNavA = LogProcessTreeSnapshot("trước nav A");

        var (navAOk, navAms) = await NavigateAndAwaitCompletionAsync(ReproUrlA, TimeSpan.FromSeconds(25));
        var treeAfterNavA = LogProcessTreeSnapshot("sau nav A");
        LogProcessTreeDiff("nav A", treeBeforeNavA, treeAfterNavA);

        if (navSettleMs > 0)
        {
            AppendLog($"[REPRO] Chờ settle {navSettleMs}ms sau nav A...");
            await Task.Delay(navSettleMs);
        }

        // Chu kỳ 1: Start -> Stop (khớp bước 2-3 của repro gốc).
        AppendLog("[REPRO] --- Chu kỳ 1: Start ---");
        await TryStartCaptureAsync();
        AppendLog($"[REPRO] Chu kỳ 1 Start: isCapturing={ViewModel.IsCapturing}, status={ViewModel.CaptureStatus}");
        await Task.Delay(1500);
        AppendLog("[REPRO] --- Chu kỳ 1: Stop ---");
        StopCaptureAndPlayback();
        await Task.Delay(500);

        // Chu kỳ 2: Start -> Stop, KHÔNG có navigation ở giữa (khớp bước 4-5 của repro gốc).
        AppendLog("[REPRO] --- Chu kỳ 2: Start (không navigate ở giữa) ---");
        await TryStartCaptureAsync();
        AppendLog($"[REPRO] Chu kỳ 2 Start: isCapturing={ViewModel.IsCapturing}, status={ViewModel.CaptureStatus}");
        await Task.Delay(1500);
        AppendLog("[REPRO] --- Chu kỳ 2: Stop ---");
        StopCaptureAndPlayback();
        await Task.Delay(500);

        // Navigate lại — "reload" = đúng URL cũ (khớp repro gốc của user thật); "diff" = video khác
        // hẳn (để phân biệt: bug do reload cùng URL, hay do BẤT KỲ navigation thật nào).
        string secondUrl = scenario == "diff" ? ReproUrlB : ReproUrlA;
        AppendLog($"[REPRO] --- Navigate lại (scenario={scenario}): {secondUrl} ---");
        var treeBeforeNav2 = LogProcessTreeSnapshot("trước nav 2");

        var (nav2Ok, nav2ms) = await NavigateAndAwaitCompletionAsync(secondUrl, TimeSpan.FromSeconds(25));
        var treeAfterNav2 = LogProcessTreeSnapshot("sau nav 2");
        LogProcessTreeDiff("nav 2 (so với trước nav 2)", treeBeforeNav2, treeAfterNav2);
        LogProcessTreeDiff("nav 2 (so với sau nav A, toàn bộ phiên)", treeAfterNavA, treeAfterNav2);

        if (navSettleMs > 0)
        {
            AppendLog($"[REPRO] Chờ settle {navSettleMs}ms sau nav 2...");
            await Task.Delay(navSettleMs);
        }

        // Bước 7-8 của repro gốc: thử Start nhiều lần liên tiếp, độ trễ tăng dần, để xem có tự
        // phục hồi không nếu không sửa gì (hoặc để xác nhận 1 fix có giữ được qua nhiều lần thử).
        int[] retryDelaysMs = { 0, 0, 0, 1000, 3000, 5000, 10000 };
        bool recovered = false;
        for (int attempt = 1; attempt <= retryDelaysMs.Length; attempt++)
        {
            if (retryDelaysMs[attempt - 1] > 0)
            {
                AppendLog($"[REPRO] Chờ {retryDelaysMs[attempt - 1]}ms trước lần thử Start #{attempt}...");
                await Task.Delay(retryDelaysMs[attempt - 1]);
            }

            AppendLog($"[REPRO] >>> Lần thử Start #{attempt} sau navigation lại...");
            await TryStartCaptureAsync();
            await Task.Delay(500);

            bool ok = ViewModel.IsCapturing;
            AppendLog($"[REPRO] <<< Lần thử #{attempt}: isCapturing={ok}, status={ViewModel.CaptureStatus}, initFlags={ViewModel.InitializeFlagsInfo}");

            if (ok)
            {
                AppendLog($"[REPRO] *** THÀNH CÔNG ở lần thử #{attempt} (sau navigation lại) ***");
                recovered = true;
                await Task.Delay(500);
                StopCaptureAndPlayback();
                break;
            }
        }

        if (!recovered)
        {
            AppendLog($"[REPRO] *** KHÔNG tự phục hồi sau {retryDelaysMs.Length} lần thử (kể cả với độ trễ tăng dần tới {retryDelaysMs[^1]}ms) ***");
        }

        AppendLog("[REPRO] === KẾT THÚC kịch bản. ===");
    }

    /// <summary>
    /// [DEBUG-only] Kịch bản kiểm tra giả thuyết THUẦN TIMING/race-condition: lặp Start->giữ
    /// 800ms->Stop->chờ <paramref name="gapMs"/>->lặp lại, đúng <paramref name="cycles"/> lần,
    /// KHÔNG có navigation nào xen giữa (giữ nguyên trang test-tone đã load từ đầu). Đếm số
    /// thành công/thất bại để so sánh giữa các giá trị gapMs khác nhau (dò ngưỡng an toàn).
    /// </summary>
    private async Task RunTimingGapReproAsync(int gapMs, int cycles)
    {
        AppendLog($"[REPRO-TIMING] === BẮT ĐẦU: gap={gapMs}ms, cycles={cycles}, KHÔNG navigation ===");
        int successCount = 0;
        var failedAtCycle = new List<int>();

        for (int i = 1; i <= cycles; i++)
        {
            AppendLog($"[REPRO-TIMING] --- Chu kỳ #{i}/{cycles}: Start (gap trước đó={(i == 1 ? "N/A (lần đầu)" : gapMs + "ms")}) ---");
            await TryStartCaptureAsync();
            bool ok = ViewModel.IsCapturing;
            AppendLog($"[REPRO-TIMING] Chu kỳ #{i} Start: isCapturing={ok}, status={ViewModel.CaptureStatus}, initFlags={ViewModel.InitializeFlagsInfo}");

            if (ok)
            {
                successCount++;
            }
            else
            {
                failedAtCycle.Add(i);
            }

            // Giữ capture "chạy" 1 khoảng ngắn trước khi Stop, mô phỏng người dùng thật không bấm
            // Stop ngay lập tức sau Start (không quan trọng bằng khoảng gap SAU Stop, nhưng giữ cố
            // định để chỉ có 1 biến số thay đổi giữa các lần chạy là gapMs).
            await Task.Delay(800);

            AppendLog($"[REPRO-TIMING] --- Chu kỳ #{i}: Stop ---");
            StopCaptureAndPlayback();

            if (i < cycles)
            {
                AppendLog($"[REPRO-TIMING] Chờ gap={gapMs}ms trước chu kỳ kế tiếp...");
                await Task.Delay(gapMs);
            }
        }

        AppendLog($"[REPRO-TIMING] === KẾT THÚC: gap={gapMs}ms — {successCount}/{cycles} chu kỳ THÀNH CÔNG, thất bại ở chu kỳ #: [{string.Join(",", failedAtCycle)}] ===");
    }
#endif

    private async void StartCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await TryStartCaptureAsync();
    }

    private async Task TryStartCaptureAsync()
    {
        if (_browserProcessId == 0)
        {
            AppendLog("Chưa có BrowserProcessId (WebView2 chưa khởi tạo xong) — không thể capture.");
            return;
        }

        // Dọn instance THẤT BẠI của lần thử trước (nếu có) trước khi tạo instance mới — bản gốc để
        // _capture trỏ thẳng sang instance mới mà không Dispose() cái cũ khi StartAsync() thất bại,
        // nghĩa là COM refs của lần thử thất bại trước có thể sống tới tận khi GC dọn instance C#
        // (không xác định khi nào). Việc này ảnh hưởng trực tiếp tới độ chính xác của kịch bản
        // retry-nhiều-lần dùng để điều tra bug navigation (xem RunNavBugReproAsync) — nếu không dọn,
        // mỗi lần retry thất bại lại chồng thêm 1 instance leak, làm nhiễu kết quả.
        if (_capture is not null)
        {
            _capture.PeakLevelUpdated -= OnPeakLevelUpdated;
            _capture.Dispose();
            _capture = null;
        }

#if DEBUG
        // [Chẩn đoán, DEBUG-only] Cho phép chọn PROCESS_LOOPBACK_MODE qua biến môi trường để so
        // sánh INCLUDE_TARGET_PROCESS_TREE (mặc định, đúng ý nghĩa chức năng thật — capture audio
        // của WebView2) với EXCLUDE_TARGET_PROCESS_TREE (capture MỌI THỨ TRỪ WebView2) khi điều tra
        // bug navigation — xem README.md mục "Phát hiện quan trọng", giả thuyết sửa lỗi #4.
        var loopbackMode = Environment.GetEnvironmentVariable("SPIKE_FIX_LOOPBACK_MODE") == "exclude"
            ? NativeInterop.PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
            : NativeInterop.PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
#else
        var loopbackMode = NativeInterop.PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
#endif

        StartCaptureButton.IsEnabled = false;
        AppendLog($"Bắt đầu ActivateAudioInterfaceAsync cho PID {_browserProcessId} (mode={loopbackMode})...");

        _capture = new ProcessLoopbackCapture();
        _capture.PeakLevelUpdated += OnPeakLevelUpdated;

        CaptureStartResult result;
        try
        {
            result = await _capture.StartAsync(_browserProcessId, loopbackMode);
        }
        catch (Exception ex)
        {
            AppendLog($"NGOẠI LỆ không mong đợi khi capture: {ex}");
            ViewModel.CaptureStatus = $"Lỗi ngoại lệ: {ex.Message}";
            StartCaptureButton.IsEnabled = true;
            return;
        }

        if (!result.Success)
        {
            AppendLog($"THẤT BẠI ở bước [{result.Stage}] — HRESULT={result.HResultHex}. Chi tiết: {result.Detail}");
            ViewModel.CaptureStatus = $"Thất bại: {result.Stage} (HRESULT={result.HResultHex})";
            StartCaptureButton.IsEnabled = true;
            return;
        }

        AppendLog($"Activation + Initialize THÀNH CÔNG. Cờ Initialize dùng được: {_capture.InitializeFlagsUsed}");
        ViewModel.InitializeFlagsInfo = _capture.InitializeFlagsUsed ?? "(không rõ)";
        ViewModel.CaptureStatus = "Đang capture";
        ViewModel.IsCapturing = true;

        StartWasapiOutPlayback();

        StopCaptureButton.IsEnabled = true;
    }

    private void StartWasapiOutPlayback()
    {
        if (_capture is null)
        {
            return;
        }

        try
        {
            _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 40);

            // Chuẩn hoá format capture (44.1kHz/16-bit/stereo PCM, hardcode do GetMixFormat trên
            // client loopback trả E_NOTIMPL) về đúng mix-format của thiết bị phát mặc định, để
            // WasapiOut không phải tự đoán/từ chối format lạ ở shared mode.
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var defaultRenderDevice = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var deviceMixFormat = defaultRenderDevice.AudioClient.MixFormat;

            IWaveProvider provider = _capture.Buffer;
            if (!Equals(deviceMixFormat, _capture.WaveFormat))
            {
                var resampler = new MediaFoundationResampler(_capture.Buffer, deviceMixFormat)
                {
                    ResamplerQuality = 60,
                };
                provider = resampler;
            }

            _wasapiOut.Init(provider);
            _wasapiOut.Play();
            AppendLog("WasapiOut đã Init + Play (phát lại bản capture qua loa mặc định).");
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khi khởi tạo WasapiOut phát lại: {ex}");
        }
    }

    private void OnPeakLevelUpdated(float peak)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.PeakLevelPercent = peak * 100.0;
            ViewModel.PeakLevelText = $"{peak * 100:F0}%";
            PeakLevelBar.Width = Math.Max(0, Math.Min(1, peak)) * 360;
        });
    }

    private void StopCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StopCaptureAndPlayback();
        StartCaptureButton.IsEnabled = true;
        StopCaptureButton.IsEnabled = false;
    }

    private void StopCaptureAndPlayback()
    {
        try
        {
            _wasapiOut?.Stop();
            _wasapiOut?.Dispose();
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khi dừng WasapiOut: {ex}");
        }

        _wasapiOut = null;

        try
        {
            if (_capture is not null)
            {
                _capture.PeakLevelUpdated -= OnPeakLevelUpdated;
                _capture.Stop();
                _capture.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khi dừng capture: {ex}");
        }

        _capture = null;
        ViewModel.IsCapturing = false;
        ViewModel.CaptureStatus = "Đã dừng";
        ViewModel.PeakLevelPercent = 0;
        ViewModel.PeakLevelText = "0%";
        PeakLevelBar.Width = 0;
        AppendLog("Đã dừng capture + playback.");

        // *** FIX CHÍNH THỨC cho bug "Start capture thất bại sau khi Stop rồi Start lại" ***
        // (xem README.md mục "Phát hiện quan trọng" để biết đầy đủ bằng chứng thực nghiệm).
        //
        // TÓM TẮT PHÁT HIỆN: bug KHÔNG phụ thuộc vào navigation WebView2 như nghi ngờ ban đầu — nó
        // là 1 vấn đề THUẦN TIMING/GC độc lập hoàn toàn với navigation. Đã chứng minh bằng cách lặp
        // Start->Stop KHÔNG hề có navigation nào: chu kỳ #1 luôn thành công, MỌI chu kỳ sau đó luôn
        // thất bại 100% — bất kể chờ 500ms, 1000ms, 2000ms hay 5000ms giữa Stop và Start (kết quả
        // giống hệt nhau ở cả 4 mức, tức KHÔNG PHẢI race-condition kiểu "chưa đủ thời gian ổn định").
        // Chỉ có 1 thứ thực sự tạo ra khác biệt: ép GC.Collect() + GC.WaitForPendingFinalizers() ngay
        // sau Stop() — làm vậy thì MỌI chu kỳ sau đó đều thành công (đã xác nhận 3 lần chạy sạch
        // liên tiếp, 18/18 chu kỳ, cả ở kịch bản thuần timing lẫn kịch bản tái hiện navigation thật
        // với video YouTube thật — 2 lần "reload" + 1 lần "video khác" đều thành công ngay lần thử
        // Start đầu tiên sau navigate lại).
        //
        // GIẢI THÍCH: kết luận hợp lý nhất là có ít nhất 1 COM RCW (runtime-callable wrapper, có thể
        // KHÔNG PHẢI riêng IActivateAudioInterfaceAsyncOperation — đã test giải phóng tường minh
        // object đó riêng lẻ, KHÔNG đủ để sửa bug) chỉ thực sự gọi Release() xuống native khi CLR
        // finalize nó, KHÔNG phải khi code gọi Marshal.ReleaseComObject tường minh (có thể do RCW đó
        // vẫn còn 1 tham chiếu ẩn khác — vd .NET cache RCW theo định danh IUnknown nên tham số
        // activateOperation nhận trong callback ActivateCompleted dùng CHUNG RCW với biến cục bộ ở
        // StartAsync). Khi native (mmdevapi.dll) thấy vẫn còn 1 client cũ chưa thực sự release xong
        // cho cùng cặp (nguồn loopback, target process), lần activate/Initialize kế tiếp cho CÙNG
        // target process đó thất bại với đúng chữ ký lỗi 0x8000FFFF/0x88890021 — độc lập với có
        // navigation hay không, vì bản chất KHÔNG liên quan navigation.
        //
        // ĐÁNH ĐỔI: GC.Collect() ép buộc là "biện pháp thô" (không xác định chính xác object nào
        // leak), và có thể gây 1 khoảng dừng ngắn (thường vài-vài chục ms) mỗi lần bấm "Dừng capture"
        // — chấp nhận được vì đây là hành động người dùng chủ động, không phải vòng lặp nóng.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#if DEBUG
        AppendLog("[FIX] Đã ép GC.Collect()+WaitForPendingFinalizers() sau Stop (xem README.md — fix chính thức cho bug Start/Stop lặp lại).");
#endif
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserProcessId == 0)
        {
            return;
        }

        var pids = ProcessTreeHelper.GetProcessTreePids(_browserProcessId);
        var result = SessionMuter.SetMuteForProcessTree(pids, mute: true);

        if (result.Success)
        {
            ViewModel.IsMuted = true;
            ViewModel.MuteStatus = "Đã mute";
            ViewModel.MutedPidsInfo = result.MatchedPids.Count > 0
                ? $"Đã mute {result.MatchedPids.Count} session, PID: {string.Join(", ", result.MatchedPids)}"
                : "Không tìm thấy session nào khớp PID trong cây process (xem log để biết PID nào đang phát audio thực sự).";
            AppendLog(ViewModel.MutedPidsInfo);
        }
        else
        {
            ViewModel.MuteStatus = $"Lỗi mute: {result.Error}";
            AppendLog(ViewModel.MuteStatus);
        }
    }

    private void UnmuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserProcessId == 0)
        {
            return;
        }

        var pids = ProcessTreeHelper.GetProcessTreePids(_browserProcessId);
        var result = SessionMuter.SetMuteForProcessTree(pids, mute: false);

        if (result.Success)
        {
            ViewModel.IsMuted = false;
            ViewModel.MuteStatus = "Đã bỏ mute";
            ViewModel.MutedPidsInfo = result.MatchedPids.Count > 0
                ? $"Đã bỏ mute {result.MatchedPids.Count} session, PID: {string.Join(", ", result.MatchedPids)}"
                : "Không tìm thấy session nào khớp PID trong cây process.";
            AppendLog(ViewModel.MutedPidsInfo);
        }
        else
        {
            ViewModel.MuteStatus = $"Lỗi bỏ mute: {result.Error}";
            AppendLog(ViewModel.MuteStatus);
        }
    }

    // Hàm hỗ trợ cho x:Bind function binding trong MainWindow.xaml (thay cho converter riêng).
    public string FormatBrowserPid(string value) => $"BrowserProcessId: {value}";

    public string FormatProcessTree(string value) => value;

    public string FormatCapture(string value) => $"Capture: {value}";

    public string FormatInitFlags(string value) => $"IAudioClient.Initialize dùng cờ: {value}";

    public string FormatMute(string value) => $"Mute: {value}";

    public string FormatMutedPids(string value) => value;

#if DEBUG
    // Log file tạm CHỈ dùng để agent tự kiểm tra smoke-test lúc build spike này (không có tai để
    // nghe được audio thật). Ghi ra %TEMP% để đọc lại sau khi kill process. Có thể xoá file này
    // (và khối #if DEBUG này) khi spike đã được review — không phải phần chức năng chính thức.
    private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "ProcessLoopbackSpike.debuglog.txt");
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
            // bỏ qua — chỉ là log phụ trợ cho smoke-test, không được làm hỏng luồng chính
        }
#endif

        void Append() => ViewModel.LogText = ViewModel.LogText + stamped + Environment.NewLine;

        if (DispatcherQueue.HasThreadAccess)
        {
            Append();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Append);
        }
    }
}
