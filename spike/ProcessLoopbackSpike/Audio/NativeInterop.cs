using System.Runtime.InteropServices;

namespace ProcessLoopbackSpike.Audio;

/// <summary>
/// P/Invoke đáy (bottom layer) cho Windows process-loopback capture (Windows 10 2004+ / build 19041+).
/// Đây là API KHÔNG có trong NAudio — phải tự khai báo COM interop.
///
/// Nguồn đối chiếu khi viết file này (không đoán mò GUID/struct layout, xem README.md phần
/// "Chi tiết kỹ thuật đã dùng" để biết mức độ tin cậy từng giá trị):
///   - Microsoft Learn (chính thức): AUDIOCLIENT_ACTIVATION_PARAMS,
///     AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, AUDIOCLIENT_ACTIVATION_TYPE,
///     PROCESS_LOOPBACK_MODE — layout và tên field/enum lấy trực tiếp từ trang docs
///     learn.microsoft.com/windows/win32/api/audioclientactivationparams/*.
///   - Microsoft sample "ApplicationLoopback" (github.com/microsoft/windows-classic-samples,
///     Samples/ApplicationLoopback/cpp/LoopbackCapture.cpp) — xác nhận cách gọi
///     ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient),
///     &activateParams, this, &asyncOp) và cách set PROPVARIANT.vt = VT_BLOB.
///   - winapi-rs (retep998/winapi-rs, um/audioclient.rs, um/mmdeviceapi.rs, um/objidlbase.rs) —
///     GUID của IAudioClient, IAudioCaptureClient, IActivateAudioInterfaceCompletionHandler,
///     IActivateAudioInterfaceAsyncOperation, IAgileObject. Đây là crate transcribe trực tiếp
///     từ Windows SDK header, đối chiếu khớp với NAudio (IAudioClient) và với patch mingw-w64
///     (IActivateAudioInterfaceCompletionHandler/AsyncOperation) — 2 nguồn độc lập khớp nhau.
///   - NAudio nguồn (naudio/NAudio, NAudio.Wasapi/CoreAudioApi/Interfaces/IAudioClient.cs) —
///     xác nhận lại GUID IAudioClient = 1CB9AD4C-DBFA-4c32-B178-C2F568A703B2.
///   - Bài viết cộng đồng (dev.to/davutakca, Microsoft Q&A #1125409 trả lời bởi kỹ sư Microsoft
///     Junjie Zhu) — xác nhận 2 điều KHÔNG có trong C++ sample gốc nhưng cực kỳ quan trọng:
///       1) IAudioClient trả về từ activation "VAD\Process_Loopback" có GetMixFormat() và
///          IsFormatSupported() trả E_NOTIMPL — phải tự hardcode WAVEFORMATEX, không được gọi
///          GetMixFormat().
///       2) Completion-handler COM object PHẢI implement thêm IAgileObject (marker interface
///          rỗng), nếu không ActivateAudioInterfaceAsync sẽ thất bại vì handler "not agile".
/// </summary>
internal static class NativeInterop
{
    /// <summary>
    /// Device interface path đặc biệt cho process-loopback activation.
    /// Giá trị chuỗi CHÍNH XÁC "VAD\Process_Loopback" (không phải GUID) — đây là macro
    /// VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK trong audioclientactivationparams.h, không phải
    /// device ID thật của một endpoint. Đối chiếu khớp giữa: mô tả trong yêu cầu spike gốc,
    /// mã nguồn Rust thực tế trong dự án couchlink (jrb00013/couchlink PR #70,
    /// crates/capture-bridge/src/audio_capture.rs), và bài viết dev.to đã dẫn ở trên.
    /// </summary>
    internal const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    // IID_IAudioClient = 1CB9AD4C-DBFA-4c32-B178-C2F568A703B2
    // Xác nhận: NAudio nguồn (IAudioClient.cs) + winapi-rs (audioclient.rs). Độ tin cậy cao.
    internal static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    // IID_IAudioCaptureClient = C8ADBD64-E71E-48a0-A4DE-185C395CD317
    // Xác nhận: winapi-rs (audioclient.rs). Độ tin cậy cao (GUID này ổn định, dùng khắp nơi).
    internal static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    // IID_IActivateAudioInterfaceCompletionHandler = 41D949AB-9862-444A-80F6-C261334DA5EB
    // Xác nhận: winapi-rs (mmdeviceapi.rs) + patch mingw-w64-public gửi lên mailing list
    // (thêm ActivateAudioInterfaceAsync API vào header mmdeviceapi.h) — 2 nguồn ĐỘC LẬP khớp
    // nhau chính xác từng byte. Đây là GIÁ TRỊ QUAN TRỌNG NHẤT phải đúng, vì đây là GUID mà
    // .NET COM interop dùng để trả lời QueryInterface khi mmdevapi.dll hỏi lại completion
    // handler managed object của chúng ta.
    internal static readonly Guid IID_IActivateAudioInterfaceCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");

    // IID_IActivateAudioInterfaceAsyncOperation = 72A22D78-CDE4-431D-B8CC-843A71199B6D
    // Xác nhận: cùng 2 nguồn như trên.
    internal static readonly Guid IID_IActivateAudioInterfaceAsyncOperation = new("72A22D78-CDE4-431D-B8CC-843A71199B6D");

    // IID_IAgileObject = 94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90
    // Xác nhận: winapi-rs (objidlbase.rs). Đây là marker interface rỗng (không có method nào
    // ngoài IUnknown) — bắt buộc phải "lộ diện" qua QueryInterface trên completion handler của
    // chúng ta, nếu không ActivateAudioInterfaceAsync có thể fail vì handler bị coi là không
    // an toàn gọi xuyên-apartment (xem ghi chú ở trên, nguồn dev.to).
    internal static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    internal const ushort VT_BLOB = 0x41;

    /// <summary>
    /// PROPVARIANT rút gọn, CHỈ đủ dùng cho trường hợp VT_BLOB (không phải PROPVARIANT tổng quát).
    /// Layout (x64): vt tại offset 0 (2 byte) + 3 WORD reserved (offset 2,4,6) = header 8 byte,
    /// theo sau là union bắt đầu tại offset 8. Trong union, BLOB {ULONG cbSize; BYTE* pBlobData;}
    /// tự thân có padding để con trỏ pBlobData căn lề 8 byte -> cbSize tại offset 8, pBlobData tại
    /// offset 16. Đây là pattern PROPVARIANT-cho-VT_BLOB được dùng lặp lại trong nhiều bản port
    /// C# công khai của ApplicationLoopback; KHÔNG tìm được nguồn Microsoft chính thức liệt kê
    /// offset bằng số nguyên (Microsoft chỉ công bố struct PROPVARIANT dạng union C++, trình biên
    /// dịch tự tính offset) — coi phần offset cụ thể này là suy luận hợp lý theo quy tắc căn lề
    /// chuẩn của C/C++ trên x64, không phải trích dẫn trực tiếp từ tài liệu.
    /// [UNVERIFIED-BY-DIRECT-SOURCE, nhưng suy luận từ quy tắc alignment chuẩn — xem README.md]
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public int blobSize;

        [FieldOffset(16)]
        public IntPtr blobData;
    }

    // --- AUDIOCLIENT_ACTIVATION_PARAMS family ------------------------------------------------
    // Layout xác nhận trực tiếp từ Microsoft Learn:
    //   learn.microsoft.com/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params
    //   learn.microsoft.com/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
    //   learn.microsoft.com/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type
    //   learn.microsoft.com/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
    // Yêu cầu tối thiểu: Windows 10 Build 20348 theo trang docs (ghi chú: máy Win10 19045 dùng để
    // build/test spike này CŨ HƠN con số đó — xem README.md mục kết quả thực nghiệm).

    internal enum AUDIOCLIENT_ACTIVATION_TYPE : int
    {
        AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
        AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1,
    }

    internal enum PROCESS_LOOPBACK_MODE : int
    {
        PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
        PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    // --- WAVEFORMATEX -------------------------------------------------------------------------
    // Layout chuẩn Win32, ổn định từ Windows 3.1 — độ tin cậy cao, không cần trích nguồn thêm.
    internal const ushort WAVE_FORMAT_PCM = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;

        public static WAVEFORMATEX CreatePcm16(int sampleRate, int channels)
        {
            var blockAlign = (ushort)(channels * 2);
            return new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = blockAlign,
                nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
                cbSize = 0,
            };
        }
    }

    // --- AUDCLNT flags/enums (chuẩn WASAPI, không riêng cho process-loopback) ------------------
    internal enum AUDCLNT_SHAREMODE : int
    {
        Shared = 0,
        Exclusive = 1,
    }

    internal const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    internal const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    internal const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;

    // --- HRESULT tiện ích ----------------------------------------------------------------------
    internal const int S_OK = 0;
}

/// <summary>
/// COM callback interface mà mmdevapi.dll gọi lại khi ActivateAudioInterfaceAsync hoàn tất.
/// GUID phải khớp IID_IActivateAudioInterfaceCompletionHandler thật thì QueryInterface từ phía
/// native mới thành công — xem ghi chú độ tin cậy trong NativeInterop.
/// </summary>
[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    // Cố tình dùng out IntPtr (thay vì [MarshalAs(UnmanagedType.IUnknown)] out object) rồi tự
    // Marshal.GetObjectForIUnknown ở nơi gọi — cùng 1 cách làm với IAudioClient.GetService bên
    // dưới (đã xác nhận hoạt động đúng qua smoke-test thực tế). Lý do đổi: bản đầu dùng "out
    // object" tự động marshal đã cho ra 1 __ComObject mà QueryInterface lại chính IID vừa dùng để
    // activate cũng bị E_NOINTERFACE — điều này về logic COM là bất thường (1 interface luôn phải
    // tự QI được chính nó), nên nghi ngờ marshaling tự động ở dạng "out object" bị sai lệch, thử
    // lại bằng con trỏ thô để loại trừ khả năng đó.
    void GetActivateResult(
        out int activateResult,
        out IntPtr activatedInterface);
}

/// <summary>
/// Marker interface rỗng — bắt buộc phải cùng implement với IActivateAudioInterfaceCompletionHandler
/// trên cùng 1 class để CCW (COM-callable wrapper) của .NET trả lời QueryInterface(IID_IAgileObject)
/// thành công, báo cho native biết object này an toàn gọi từ bất kỳ thread/apartment nào.
/// </summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

/// <summary>
/// IAudioClient rút gọn — chỉ khai báo lại đúng thứ tự vtable chuẩn (Initialize..GetService),
/// không thêm/bớt method nào (nếu thêm/bớt sai vị trí, mọi lệnh gọi sau đó sẽ gọi nhầm slot vtable
/// và có khả năng crash native, không phải lỗi .NET-catchable). Thứ tự đối chiếu winapi-rs.
/// PreserveSig=true trên toàn bộ để tự đọc HRESULT thay vì để CLR ném exception mất thông tin.
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        NativeInterop.AUDCLNT_SHAREMODE shareMode,
        uint streamFlags,
        long bufferDuration,
        long periodicity,
        ref NativeInterop.WAVEFORMATEX format,
        IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint currentPadding);

    [PreserveSig]
    int IsFormatSupported(
        NativeInterop.AUDCLNT_SHAREMODE shareMode,
        ref NativeInterop.WAVEFORMATEX format,
        IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, out IntPtr service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr dataBuffer,
        out uint numFramesToRead,
        out uint bufferFlags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}
