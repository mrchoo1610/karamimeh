using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace KaraokeMixer.Core.Audio;

/// <summary>
/// Kết quả của một lần thử StartAsync — luôn trả về, KHÔNG ném exception cho các lỗi HRESULT
/// đã biết trước (đúng yêu cầu spike: nếu activation thất bại, phải báo cáo rõ ràng HRESULT +
/// bước nào thất bại, không được nuốt lỗi hay giả vờ thành công).
/// </summary>
internal sealed record CaptureStartResult(bool Success, string Stage, int HResult, string? Detail)
{
    public string HResultHex => $"0x{HResult:X8}";

    public static CaptureStartResult Ok(string stage) => new(true, stage, NativeInterop.S_OK, null);

    public static CaptureStartResult Fail(string stage, int hresult, string? detail = null)
    {
        string? sysMsg = null;
        try
        {
            sysMsg = Marshal.GetExceptionForHR(hresult)?.Message;
        }
        catch
        {
            // bỏ qua — chỉ là chú thích thêm cho dễ đọc, không quan trọng bằng mã HRESULT thô
        }

        string combined = string.Join(
            " — ",
            new[] { detail, sysMsg }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new CaptureStartResult(false, stage, hresult, string.IsNullOrEmpty(combined) ? null : combined);
    }
}

/// <summary>
/// Lớp chính thực hiện process-loopback capture qua raw P/Invoke (xem NativeInterop.cs).
/// Luồng xử lý: ActivateAudioInterfaceAsync -> chờ callback -> Initialize IAudioClient (hardcode
/// WAVEFORMATEX vì GetMixFormat trả E_NOTIMPL trên client loại này) -> GetService IAudioCaptureClient
/// -> Start -> vòng lặp đọc buffer trên 1 thread nền, đẩy PCM byte vào BufferedWaveProvider để
/// WasapiOut ở phía khác đọc ra loa.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    // Format hardcode — 44.1kHz/16-bit/stereo PCM. Đây CHÍNH XÁC là format một kỹ sư Microsoft
    // (Junjie Zhu) xác nhận hoạt động ổn định cho client loại "VAD\Process_Loopback" trên
    // Microsoft Q&A #1125409, vì GetMixFormat()/IsFormatSupported() trên client này trả
    // E_NOTIMPL nên không thể tự dò định dạng — phải hardcode.
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int BitsPerSample = 16;

    public WaveFormat WaveFormat { get; } = new(SampleRate, BitsPerSample, Channels);

    public BufferedWaveProvider Buffer { get; }

    public bool IsCapturing { get; private set; }

    /// <summary>Tổng số byte PCM đã nhận qua GetBuffer/ReleaseBuffer kể từ lúc Start (kể cả byte
    /// im lặng) — dùng để agent tự kiểm chứng vòng lặp capture có thực sự chạy hay không, độc lập
    /// với BufferedWaveProvider.BufferedDuration (giá trị đó có thể về 0 chỉ vì WasapiOut đọc kịp).</summary>
    public long TotalBytesCaptured => Interlocked.Read(ref _totalBytesCaptured);

    /// <summary>Số byte nhận được mà WASAPI tự đánh dấu cờ <c>AUDCLNT_BUFFERFLAGS_SILENT</c> (tức
    /// là engine audio của Windows tự xác định "im lặng", không phải do app tự tính từ giá trị mẫu
    /// = 0). Thêm để chẩn đoán 1 câu hỏi cụ thể: nếu <see cref="TotalBytesCaptured"/> tăng đều
    /// nhưng gần như toàn bộ rơi vào đây, nghĩa là capture đang CHẠY THẬT (nhận gói tin đều đặn)
    /// nhưng Windows tự báo im lặng — khác hẳn với việc vòng lặp capture không chạy/không nhận
    /// được gói tin nào.</summary>
    public long SilentBytesCaptured => Interlocked.Read(ref _silentBytesCaptured);

    private long _totalBytesCaptured;
    private long _silentBytesCaptured;

    /// <summary>Số lần <c>_bufferReadyEvent.WaitOne(200)</c> trả về true (event thực sự được báo
    /// hiệu) kể từ lúc Start. Thêm để chẩn đoán trực tiếp câu hỏi "vòng lặp capture có bao giờ được
    /// native báo hiệu có dữ liệu hay không" — độc lập với <see cref="TotalBytesCaptured"/>, vốn chỉ
    /// tăng SAU KHI GetNextPacketSize/GetBuffer cũng thành công. Nếu giá trị này tăng đều nhưng
    /// TotalBytesCaptured vẫn = 0, nghĩa là event được báo hiệu (client audio "thức dậy" đúng nhịp)
    /// nhưng GetNextPacketSize/GetBuffer sau đó không bao giờ trả về gói tin nào — khác hẳn với việc
    /// event không bao giờ được báo hiệu (client audio dường như "ngủ luôn"), vốn cần tìm nguyên nhân
    /// ở một lớp khác (SetEventHandle/luồng render).</summary>
    public long WaitSignaledCount => Interlocked.Read(ref _waitSignaledCount);

    /// <summary>Số lần WaitOne(200) hết hạn (timeout, event KHÔNG được báo hiệu trong 200ms) kể từ
    /// lúc Start. Xem <see cref="WaitSignaledCount"/>.</summary>
    public long WaitTimeoutCount => Interlocked.Read(ref _waitTimeoutCount);

    /// <summary>Số lần GetNextPacketSize trả về S_OK nhưng packetFrames=0 (nghĩa là: event được báo
    /// hiệu, client audio thức dậy đúng nhịp, nhưng lúc poll thì chưa/không còn gói tin nào sẵn sàng
    /// — khác với GetNextPacketSize thất bại hẳn, xem <see cref="PacketSizeErrorCount"/>).</summary>
    public long ZeroPacketPollCount => Interlocked.Read(ref _zeroPacketPollCount);

    /// <summary>Số lần GetNextPacketSize trả về HRESULT khác S_OK (lỗi thật sự, không phải "chưa có
    /// gói tin"). <see cref="LastPacketSizeErrorHResult"/> giữ HRESULT gần nhất trong số này.</summary>
    public long PacketSizeErrorCount => Interlocked.Read(ref _packetSizeErrorCount);

    public int LastPacketSizeErrorHResult { get; private set; }

    private long _waitSignaledCount;
    private long _waitTimeoutCount;
    private long _zeroPacketPollCount;
    private long _packetSizeErrorCount;

    /// <summary>Bước Initialize nào thực sự thành công — LOOPBACK|EVENTCALLBACK hay chỉ EVENTCALLBACK.</summary>
    public string? InitializeFlagsUsed { get; private set; }

    public event Action<float>? PeakLevelUpdated;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private AutoResetEvent? _bufferReadyEvent;
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private IntPtr _activationParamsPtr;

    public ProcessLoopbackCapture()
    {
        Buffer = new BufferedWaveProvider(WaveFormat)
        {
            // Không cho phép backlog quá lớn (spike, không cần lưu lịch sử) và không được block
            // khi ghi — nếu WasapiOut phía đầu ra tụt lại (underrun), thà mất vài frame còn hơn
            // treo thread capture.
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
    }

    /// <summary>
    /// Giải phóng tường minh <c>IActivateAudioInterfaceAsyncOperation</c> (đối tượng COM trả về từ
    /// chính lệnh gọi <c>ActivateAudioInterfaceAsync</c>, KHÔNG phải <c>IAudioClient</c> lấy được từ
    /// nó) bằng <c>Marshal.ReleaseComObject</c> ngay sau khi đã đọc xong kết quả activation. Bản gốc
    /// dùng <c>out _</c> để nhận tham số này rồi bỏ luôn — RCW (runtime-callable wrapper) đó chỉ
    /// được native release khi CLR finalize/GC nó, KHÔNG xác định thời điểm. Đây là hành vi COM
    /// đúng đắn nên LUÔN bật (không gate theo build/env var).
    ///
    /// ĐÃ KIỂM CHỨNG THỰC NGHIỆM (xem README.md mục "Phát hiện quan trọng"): bật RIÊNG cờ này
    /// (không có gì khác) KHÔNG sửa được bug "Start capture thất bại sau khi navigate/lặp Start-Stop
    /// nhanh" — test A/B trực tiếp cho kết quả giống hệt bản gốc (1/6 chu kỳ thành công). Root cause
    /// thật sự nằm ở chỗ khác (xem <c>ForceGcAfterStop</c> trong MainWindow.xaml.cs — GC.Collect() +
    /// WaitForPendingFinalizers() sau Stop() mới là fix thật, đã kiểm chứng 5+ lần chạy sạch). Vẫn
    /// giữ lại fix NÀY vì nó là COM hygiene đúng đắn độc lập với bug kia, không phải vì nó tự sửa
    /// được bug — không nên bỏ chỉ vì phát hiện nó "không phải nguyên nhân chính".
    /// </summary>
    private const bool ReleaseAsyncOpFixEnabled = true;

    public async Task<CaptureStartResult> StartAsync(
        uint targetProcessId,
        NativeInterop.PROCESS_LOOPBACK_MODE mode = NativeInterop.PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE)
    {
        if (IsCapturing)
        {
            return CaptureStartResult.Fail("StartAsync", NativeInterop.S_OK, "Đã đang capture, gọi Stop() trước.");
        }

        // Bước 1: chuẩn bị AUDIOCLIENT_ACTIVATION_PARAMS + PROPVARIANT(VT_BLOB) và gọi
        // ActivateAudioInterfaceAsync. Bộ nhớ unmanaged chỉ cần sống trong lúc gọi hàm này
        // (API đọc đồng bộ trong lúc gọi, không giữ con trỏ về sau) nên free ngay sau khi gọi xong.
        var activationParams = new NativeInterop.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = NativeInterop.AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new NativeInterop.AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = targetProcessId,
                ProcessLoopbackMode = mode,
            },
        };

        int paramsSize = Marshal.SizeOf<NativeInterop.AUDIOCLIENT_ACTIVATION_PARAMS>();
        _activationParamsPtr = Marshal.AllocCoTaskMem(paramsSize);
        Marshal.StructureToPtr(activationParams, _activationParamsPtr, false);

        var propvariant = new NativeInterop.PROPVARIANT
        {
            vt = NativeInterop.VT_BLOB,
            blobSize = paramsSize,
            blobData = _activationParamsPtr,
        };

        var handler = new ActivateAudioInterfaceCompletionHandler();
        var riid = NativeInterop.IID_IAudioClient;

        // QUAN TRỌNG (xem ghi chú ReleaseAsyncOpFixEnabled ở trên): bản gốc dùng `out _` ở đây,
        // nghĩa là RCW bọc con trỏ IActivateAudioInterfaceAsyncOperation bị vứt bỏ ngay, KHÔNG bao
        // giờ được Marshal.ReleaseComObject tường minh — chỉ được giải phóng khi CLR GC/finalize nó
        // (thời điểm không xác định). Giữ lại biến để có thể release tường minh bên dưới.
        IActivateAudioInterfaceAsyncOperation? asyncOperation = null;

        int initialHr;
        try
        {
            initialHr = NativeInterop.ActivateAudioInterfaceAsync(
                NativeInterop.VirtualAudioDeviceProcessLoopback,
                ref riid,
                ref propvariant,
                handler,
                out asyncOperation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            FreeActivationParams();
            return CaptureStartResult.Fail("ActivateAudioInterfaceAsync (P/Invoke call)", unchecked((int)0x80004005), ex.Message);
        }

        if (initialHr != NativeInterop.S_OK)
        {
            FreeActivationParams();
            ReleaseAsyncOperationIfEnabled(asyncOperation);
            return CaptureStartResult.Fail("ActivateAudioInterfaceAsync (lệnh gọi ban đầu)", initialHr);
        }

        (int activateHr, IntPtr activatedInterfacePtr) result;
        try
        {
            var completed = await Task.WhenAny(handler.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != handler.Task)
            {
                FreeActivationParams();
                ReleaseAsyncOperationIfEnabled(asyncOperation);
                return CaptureStartResult.Fail(
                    "ActivateAudioInterfaceAsync (chờ callback ActivateCompleted)",
                    unchecked((int)0x80004004),
                    "Timeout 5s — callback không bao giờ được gọi (có thể do IAgileObject/QueryInterface thất bại phía native).");
            }

            result = await handler.Task;
        }
        finally
        {
            // Chỉ free sau khi chắc chắn ActivateAudioInterfaceAsync đã đọc xong blob (đồng bộ
            // trong lệnh gọi ở trên) — free ở đây là an toàn vì Task đã hoàn tất (hoặc timeout,
            // trường hợp đó native có thể vẫn đang giữ tham chiếu blob trong 1 khoảng ngắn, chấp
            // nhận rủi ro nhỏ này cho 1 spike vì timeout tức là coi như thất bại rồi).
            FreeActivationParams();
        }

        // Đã đọc xong kết quả activation qua handler.Task (dữ liệu đã được copy ra HRESULT +
        // con trỏ interface) — object activation-operation này không còn cần thiết nữa, giải
        // phóng tường minh (nếu bật cờ) thay vì chờ GC.
        ReleaseAsyncOperationIfEnabled(asyncOperation);

        if (result.activateHr != NativeInterop.S_OK || result.activatedInterfacePtr == IntPtr.Zero)
        {
            return CaptureStartResult.Fail("ActivateCompleted (kết quả activation)", result.activateHr);
        }

        // Cùng cách làm với GetService(IID_IAudioCaptureClient) bên dưới: nhận con trỏ thô rồi tự
        // GetObjectForIUnknown, thay vì để CLR tự marshal "out object" (xem ghi chú trong
        // NativeInterop.cs / ActivateAudioInterfaceCompletionHandler.cs lý do đổi cách này).
        object rcw = Marshal.GetObjectForIUnknown(result.activatedInterfacePtr);
        Marshal.Release(result.activatedInterfacePtr); // GetObjectForIUnknown đã tự AddRef qua RCW

        try
        {
            _audioClient = (IAudioClient)rcw;
        }
        catch (InvalidCastException ex)
        {
            return CaptureStartResult.Fail(
                "QueryInterface(IID_IAudioClient) trên object trả về từ GetActivateResult",
                unchecked((int)0x80004002),
                $"Activation báo S_OK và trả về 1 interface pointer khác-null, nhưng QueryInterface lại chính IID đã dùng để activate lại thất bại — bất thường theo quy tắc COM (1 object luôn phải tự QI được chính nó). Chi tiết: {ex.Message}");
        }

        var format = NativeInterop.WAVEFORMATEX.CreatePcm16(SampleRate, Channels);
        _bufferReadyEvent = new AutoResetEvent(false);

        // Thử Initialize với AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK trước (đúng như mô tả
        // API chính thức). Nếu thất bại, thử lại KHÔNG có cờ LOOPBACK (chỉ EVENTCALLBACK) — vì
        // có khả năng client activate qua "VAD\Process_Loopback" đã ngầm định là loopback stream
        // rồi và không chấp nhận cờ LOOPBACK tường minh (task mô tả đây là điều cần kiểm chứng
        // thực nghiệm, tài liệu công khai không nói rõ 100%).
        const long bufferDuration100ns = 2_000_000; // 200ms
        int initHr = _audioClient.Initialize(
            NativeInterop.AUDCLNT_SHAREMODE.Shared,
            NativeInterop.AUDCLNT_STREAMFLAGS_LOOPBACK | NativeInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            bufferDuration100ns,
            0,
            ref format,
            IntPtr.Zero);

        if (initHr == NativeInterop.S_OK)
        {
            InitializeFlagsUsed = "AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK";
        }
        else
        {
            int initHrNoLoopback = _audioClient.Initialize(
                NativeInterop.AUDCLNT_SHAREMODE.Shared,
                NativeInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                bufferDuration100ns,
                0,
                ref format,
                IntPtr.Zero);

            if (initHrNoLoopback == NativeInterop.S_OK)
            {
                InitializeFlagsUsed = "AUDCLNT_STREAMFLAGS_EVENTCALLBACK (không có LOOPBACK — bản có LOOPBACK thất bại)";
            }
            else
            {
                CleanupComObjects();
                return CaptureStartResult.Fail(
                    "IAudioClient.Initialize",
                    initHr,
                    $"Thử với LOOPBACK|EVENTCALLBACK: 0x{initHr:X8}. Thử lại chỉ EVENTCALLBACK: 0x{initHrNoLoopback:X8}. Cả hai đều thất bại.");
            }
        }

        int setEventHr = _audioClient.SetEventHandle(_bufferReadyEvent.SafeWaitHandle.DangerousGetHandle());
        if (setEventHr != NativeInterop.S_OK)
        {
            CleanupComObjects();
            return CaptureStartResult.Fail("IAudioClient.SetEventHandle", setEventHr);
        }

        var captureClientRiid = NativeInterop.IID_IAudioCaptureClient;
        int getServiceHr = _audioClient.GetService(ref captureClientRiid, out IntPtr captureClientPtr);
        if (getServiceHr != NativeInterop.S_OK || captureClientPtr == IntPtr.Zero)
        {
            CleanupComObjects();
            return CaptureStartResult.Fail("IAudioClient.GetService(IID_IAudioCaptureClient)", getServiceHr);
        }

        _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPtr);
        Marshal.Release(captureClientPtr); // GetObjectForIUnknown đã AddRef qua RCW, trả lại ref thô

        int startHr = _audioClient.Start();
        if (startHr != NativeInterop.S_OK)
        {
            CleanupComObjects();
            return CaptureStartResult.Fail("IAudioClient.Start", startHr);
        }

        _stopRequested = false;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ProcessLoopbackCapture",
        };
        _captureThread.Start();

        IsCapturing = true;
        return CaptureStartResult.Ok("Capture đã bắt đầu");
    }

    private void FreeActivationParams()
    {
        if (_activationParamsPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_activationParamsPtr);
            _activationParamsPtr = IntPtr.Zero;
        }
    }

    private static void ReleaseAsyncOperationIfEnabled(IActivateAudioInterfaceAsyncOperation? asyncOperation)
    {
        if (asyncOperation is null || !ReleaseAsyncOpFixEnabled)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(asyncOperation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoopbackCapture] ReleaseComObject(asyncOperation): {ex}");
        }
    }

    private void CaptureLoop()
    {
        // Vòng lặp event-driven chuẩn WASAPI: chờ event, đọc hết các packet có sẵn, lặp lại.
        // Cập nhật peak-level tối đa ~10 lần/giây (không cập nhật mỗi packet để tránh spam UI).
        var meterStopwatch = Stopwatch.StartNew();
        const double meterIntervalMs = 100;
        float runningPeak = 0f;

        try
        {
            while (!_stopRequested)
            {
                if (_bufferReadyEvent is null || !_bufferReadyEvent.WaitOne(200))
                {
                    Interlocked.Increment(ref _waitTimeoutCount);
                    continue;
                }

                Interlocked.Increment(ref _waitSignaledCount);

                if (_captureClient is null)
                {
                    break;
                }

                while (true)
                {
                    int packetHr = _captureClient.GetNextPacketSize(out uint packetFrames);
                    if (packetHr != NativeInterop.S_OK)
                    {
                        Interlocked.Increment(ref _packetSizeErrorCount);
                        LastPacketSizeErrorHResult = packetHr;
                        break;
                    }

                    if (packetFrames == 0)
                    {
                        Interlocked.Increment(ref _zeroPacketPollCount);
                        break;
                    }

                    int getBufferHr = _captureClient.GetBuffer(
                        out IntPtr dataPtr,
                        out uint numFrames,
                        out uint bufferFlags,
                        out _,
                        out _);

                    if (getBufferHr != NativeInterop.S_OK)
                    {
                        break;
                    }

                    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                    int byteCount = (int)numFrames * Channels * (BitsPerSample / 8);

                    if (byteCount > 0)
                    {
                        var managedBytes = new byte[byteCount];
                        bool isSilent = (bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (!isSilent && dataPtr != IntPtr.Zero)
                        {
                            Marshal.Copy(dataPtr, managedBytes, 0, byteCount);

                            int sampleCount = byteCount / 2;
                            for (int i = 0; i < sampleCount; i++)
                            {
                                short sample = (short)(managedBytes[i * 2] | (managedBytes[i * 2 + 1] << 8));
                                float normalized = Math.Abs(sample) / 32768f;
                                if (normalized > runningPeak)
                                {
                                    runningPeak = normalized;
                                }
                            }
                        }
                        else
                        {
                            Interlocked.Add(ref _silentBytesCaptured, byteCount);
                        }

                        Buffer.AddSamples(managedBytes, 0, byteCount);
                        Interlocked.Add(ref _totalBytesCaptured, byteCount);
                    }

                    _captureClient.ReleaseBuffer(numFrames);
                }

                if (meterStopwatch.Elapsed.TotalMilliseconds >= meterIntervalMs)
                {
                    PeakLevelUpdated?.Invoke(runningPeak);
                    runningPeak = 0f;
                    meterStopwatch.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoopbackCapture] Capture loop lỗi: {ex}");
        }
    }

    public void Stop()
    {
        if (!IsCapturing)
        {
            return;
        }

        _stopRequested = true;
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;

        try
        {
            _audioClient?.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoopbackCapture] Lỗi khi Stop IAudioClient: {ex}");
        }

        CleanupComObjects();
        IsCapturing = false;
    }

    private void CleanupComObjects()
    {
        if (_captureClient is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_captureClient);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLoopbackCapture] ReleaseComObject(_captureClient): {ex}");
            }

            _captureClient = null;
        }

        if (_audioClient is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_audioClient);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLoopbackCapture] ReleaseComObject(_audioClient): {ex}");
            }

            _audioClient = null;
        }

        _bufferReadyEvent?.Dispose();
        _bufferReadyEvent = null;
    }

    public void Dispose()
    {
        Stop();
        FreeActivationParams();
    }
}
