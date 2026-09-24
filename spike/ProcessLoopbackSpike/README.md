# ProcessLoopbackSpike (P1)

Spike kỹ thuật cho dự án **karamimeh**: trả lời câu hỏi — *có thể chỉ capture âm thanh của
1 process WebView2 (trình duyệt nhúng) qua Windows process-loopback, mute đường phát gốc của
process đó ở tầng OS, và phát lại qua `WasapiOut` riêng của app — mà người dùng không nghe 2 lần
hoặc không nghe gì hay không?*

Đây là spike **P1** (process loopback + mute session) trong 3 phương án đang cân nhắc cho kiến
trúc audio của karamimeh (P1 = process loopback + mute session; P2 = Web Audio tap qua
SharedBuffer; P3 = VB-Cable). Kết luận ở cuối file.

## Build & chạy

```powershell
dotnet build -c Debug -p:Platform=x64
```

Exe sau khi build (Debug, x64):

```
spike\ProcessLoopbackSpike\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ProcessLoopbackSpike.exe
```

Chạy trực tiếp file exe đó, hoặc `dotnet run -c Debug -p:Platform=x64` từ thư mục
`spike\ProcessLoopbackSpike\`.

Yêu cầu máy: đã cài Microsoft Edge WebView2 Runtime (thường có sẵn trên Windows 10/11 bản mới).
Không cần cài gì thêm cho phần audio — toàn bộ process-loopback capture dùng P/Invoke thẳng vào
`Mmdevapi.dll` có sẵn trong Windows.

## Giao diện

- Bên trái: WebView2 hiển thị trang test-tone nội bộ (nút "Phát tone 440Hz" / "Phát tone 880Hz" /
  "Dừng", chữ to "ĐANG PHÁT"/"ĐÃ DỪNG"). Đây là 1 `AudioContext` + `OscillatorNode` sine, KHÔNG
  phải video YouTube thật — chọn cách này để test xác định (deterministic), không phụ thuộc mạng,
  không vướng chính sách autoplay (tone chỉ phát khi bấm nút thật trong trang, đúng yêu cầu
  user-gesture của Web Audio API), không vướng bản quyền.
- Bên phải: bảng điều khiển spike — nút Bắt đầu/Dừng capture, Mute/Bỏ mute WebView2, trạng thái
  BrowserProcessId, cây process con, PID nào bị mute, cờ Initialize nào dùng được, đồng hồ mức tín
  hiệu (peak, cập nhật ~10 lần/giây), và log chi tiết từng bước.

## Test với video YouTube thật (bổ sung sau khi tone tổng hợp đã xác nhận sạch)

Đã thêm thanh điều khiển phía trên WebView2: nút "Tone 440Hz" (quay lại trang test-tone gốc), nút
"YouTube" (mở youtube.com), và ô địa chỉ + nút "Đi" (dán thẳng URL 1 video cụ thể). `BrowserProcessId`
không đổi khi điều hướng sang trang khác trong cùng 1 tab WebView2, nên không cần khởi động lại app
giữa các lần thử — chỉ cần bấm nút chuyển trang rồi lặp lại checklist T1-T4 bên dưới với nội dung
thật (nhạc nhiều tần số, chạy liên tục nhiều phút) thay vì tiếng bíp đơn tần.

**Lưu ý:** spike này KHÔNG có allowlist điều hướng như `WebViewLoginSpike` (đây là công cụ kỹ thuật
nội bộ, không phải giao diện cho người dùng cuối) — có thể gõ bất kỳ URL nào để test tự do.

## Checklist test thủ công (BẠN cần tự làm — agent không có tai để nghe)

Chuẩn bị: headphone/tai nghe 3.5mm có mic đang cắm sẵn — cho bài test này CHỈ cần nghe qua loa/tai
nghe bình thường, KHÔNG dùng đến mic.

- **T1**: Bấm Play trong trang WebView2 (chưa bật capture, chưa mute) → phải nghe tiếng bíp 440Hz
  bình thường.
- **T2**: Bấm "Bắt đầu capture" rồi "Mute WebView2" trong khi tone vẫn đang phát → tiếng bíp gốc
  phải biến mất, nhưng vẫn phải nghe tiếng bíp phát ra (từ đường WasapiOut riêng của app) nếu
  capture hoạt động đúng. Đối chiếu với đồng hồ mức tín hiệu trên UI.
- **T3**: Bấm "Bỏ mute WebView2" trong khi capture vẫn chạy → phải nghe tiếng bíp chồng lên 2 lần
  (bằng chứng session mute có tác dụng thật ở bước T2, không phải ngẫu nhiên im lặng).
- **T4**: Đóng và mở lại app vài lần để xem có memory leak/crash rõ rệt khi lặp lại capture nhiều
  lần không (không cần đo chính xác, chỉ quan sát).

**Lưu ý quan trọng trước khi làm T2**: session âm thanh trong Windows Audio chỉ xuất hiện SAU KHI
process đó thực sự phát ra âm thanh ít nhất 1 lần — nên phải bấm Play trong WebView2 TRƯỚC, rồi mới
bấm Mute WebView2, nếu không "Mute WebView2" sẽ không tìm thấy session nào để mute (xem mục phát
hiện thực nghiệm bên dưới).

**Kết luận P1 chỉ được coi là "dùng được"** khi T2 cho tiếng bíp liên tục không giật/rè và T3 xác
nhận mute có tác dụng thật (nghe chồng 2 tiếng). Xem thêm mục "Phát hiện quan trọng" bên dưới — đã
từng có 1 vấn đề nghiêm trọng về chu kỳ start/stop lặp lại (ảnh hưởng T4), **ĐÃ ĐIỀU TRA VÀ SỬA
XONG** (ép GC sau Stop) — chi tiết đầy đủ + bằng chứng thực nghiệm ở mục đó.

## Chi tiết kỹ thuật đã dùng (để review lại nếu cần)

### Device path cho `ActivateAudioInterfaceAsync`

Chuỗi UTF-16 cố định `"VAD\Process_Loopback"` (macro `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` trong
`audioclientactivationparams.h`), KHÔNG phải GUID device thật. Đối chiếu khớp giữa: mô tả gốc của
yêu cầu spike, mã nguồn Rust thực tế trong dự án mã nguồn mở `couchlink` (PR #70,
`crates/capture-bridge/src/audio_capture.rs`), và 1 bài viết kỹ thuật cộng đồng (dev.to). Đã chạy
thực tế và xác nhận chuỗi này được `mmdevapi.dll` chấp nhận trên máy test (Windows 10 Pro 19045).

### GUID đã dùng và mức độ tin cậy

| Tên | GUID | Nguồn đối chiếu |
|---|---|---|
| `IID_IAudioClient` | `1CB9AD4C-DBFA-4C32-B178-C2F568A703B2` | Mã nguồn NAudio (`IAudioClient.cs`) + crate `winapi-rs` (`audioclient.rs`) — 2 nguồn độc lập khớp nhau. Đã chạy thành công thực tế. |
| `IID_IAudioCaptureClient` | `C8ADBD64-E71E-48A0-A4DE-185C395CD317` | crate `winapi-rs` (`audioclient.rs`). Đã chạy thành công thực tế. |
| `IID_IActivateAudioInterfaceCompletionHandler` | `41D949AB-9862-444A-80F6-C261334DA5EB` | crate `winapi-rs` (`mmdeviceapi.rs`) + patch mingw-w64-public thêm API này vào header — 2 nguồn độc lập khớp nhau. Đã chạy thành công thực tế (native gọi lại callback đúng). |
| `IID_IActivateAudioInterfaceAsyncOperation` | `72A22D78-CDE4-431D-B8CC-843A71199B6D` | Cùng 2 nguồn trên. Đã chạy thành công thực tế. |
| `IID_IAgileObject` | `94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90` | crate `winapi-rs` (`objidlbase.rs`). Bắt buộc implement cùng `IActivateAudioInterfaceCompletionHandler` (docs Microsoft: "the implementation must be agile"), nếu không có báo cáo cộng đồng cho thấy activation thất bại. Đã chạy thành công thực tế. |

Ban đầu tôi từng nhớ nhầm `IID_IActivateAudioInterfaceCompletionHandler` = `94EA2B94-...` — hoá ra
đó là GUID của `IAgileObject`. Đã sửa lại sau khi đối chiếu 2 nguồn độc lập ở trên (xem mục "Lỗi đã
gặp" — may là lỗi này sẽ lộ ra ngay khi build/chạy chứ không "chạy sai âm thầm", vì GUID sai chỗ
này sẽ khiến callback không bao giờ được gọi hoặc activation thất bại rõ ràng).

### Struct `AUDIOCLIENT_ACTIVATION_PARAMS` / `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`

Layout và tên field lấy trực tiếp từ trang Microsoft Learn chính thức
(`learn.microsoft.com/windows/win32/api/audioclientactivationparams/*`) — KHÔNG suy đoán:

```c
typedef struct AUDIOCLIENT_ACTIVATION_PARAMS {
    AUDIOCLIENT_ACTIVATION_TYPE ActivationType; // enum: DEFAULT=0, PROCESS_LOOPBACK=1
    union { AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams; } DUMMYUNIONNAME;
} AUDIOCLIENT_ACTIVATION_PARAMS;

typedef struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
    DWORD TargetProcessId;
    PROCESS_LOOPBACK_MODE ProcessLoopbackMode; // enum: INCLUDE=0, EXCLUDE=1
} AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS;
```

Trang docs ghi yêu cầu tối thiểu **"Windows 10 Build 20348"** — con số này CAO HƠN build thật của
máy Windows 10 Pro dùng để build/test spike này (19045). Trên thực tế, activation vẫn chạy thành
công trên máy 19045 (xem log thực nghiệm bên dưới) — nghĩa là tính năng process-loopback ở tầng OS
đã có từ sớm hơn (thường được biết đến là từ Windows 10 2004 / build 19041), chỉ là struct/enum này
mới được CÔNG BỐ CHÍNH THỨC trong SDK header từ build 20348 trở đi. **[Chưa xác minh đầy đủ]**: lý
do chính xác cho sự chênh lệch này (header vs runtime availability).

### `PROPVARIANT` cho `VT_BLOB`

```csharp
[StructLayout(LayoutKind.Explicit)]
struct PROPVARIANT {
    [FieldOffset(0)]  public ushort vt;       // = VT_BLOB (0x41)
    [FieldOffset(8)]  public int blobSize;    // BLOB.cbSize
    [FieldOffset(16)] public IntPtr blobData; // BLOB.pBlobData
}
```

**[Chưa xác minh trực tiếp từ tài liệu]** — Microsoft chỉ công bố `PROPVARIANT` dạng union C++,
không liệt kê offset bằng số nguyên. Offset 0/8/16 ở trên là suy luận theo quy tắc căn lề chuẩn của
x64 (header `vt`+3 WORD reserved = 8 byte, sau đó `BLOB.cbSize` tại offset 8, `BLOB.pBlobData` được
đệm lên offset 16 vì con trỏ cần căn lề 8 byte) — ĐÃ CHẠY THÀNH CÔNG thực tế (activation nhận đúng
`TargetProcessId`, xác nhận gián tiếp layout này đúng), nhưng không tìm được nguồn liệt kê offset
tường minh để đối chiếu 100%.

### Cờ `AUDCLNT_STREAMFLAGS` cho `IAudioClient.Initialize`

Đã kiểm chứng thực nghiệm: `AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK` hoạt
động đúng ở lần activation ĐẦU TIÊN sau khi mở app (xem log). Code có cơ chế fallback tự thử lại chỉ
với `EVENTCALLBACK` (không có `LOOPBACK`) nếu lần đầu thất bại — bản fallback này thất bại với
`AUDCLNT_E_INVALID_STREAM_FLAG` (`0x88890021`) trong lần thử thứ 2 của phiên test (xem bên dưới),
xác nhận `AUDCLNT_STREAMFLAGS_LOOPBACK` LÀ BẮT BUỘC, không phải tuỳ chọn.

### Format capture: hardcode PCM 16-bit / 44100Hz / stereo

`IAudioClient` lấy được từ activation `"VAD\Process_Loopback"` trả `E_NOTIMPL` cho cả
`GetMixFormat()` lẫn `IsFormatSupported()` — xác nhận qua Microsoft Q&A #1125409 (trả lời bởi kỹ sư
Microsoft Junjie Zhu). Vì vậy code KHÔNG gọi 2 hàm đó, mà hardcode thẳng
`WAVEFORMATEX(PCM, 16-bit, 44100Hz, stereo)` — đúng giá trị được xác nhận hoạt động trong câu trả
lời đó. Đã chạy thành công thực tế trên máy test.

## Phát hiện thực nghiệm (đã tự kiểm chứng, không phải suy đoán)

Vì agent build/chạy trong môi trường không có tai nghe và không có công cụ tự động hoá UI Windows
gốc (không click được nút trong cửa sổ WinUI thật), agent đã thêm 1 hook dev-only (biến môi trường
`SPIKE_AUTOSTART_CAPTURE=1`, chỉ hoạt động trong build `#if DEBUG`, mặc định KHÔNG bật) để tự động
gọi đúng luồng code y hệt khi người dùng bấm nút, và ghi log ra file tạm để tự đọc lại. Không ảnh
hưởng hành vi app thật (người dùng bình thường không set biến môi trường này).

Kết quả từ 5 lần chạy độc lập (mỗi lần là 1 process exe mới):

1. **`BrowserProcessId` do `CoreWebView2.BrowserProcessId` trả về KHÔNG PHẢI là process duy nhất.**
   Mỗi lần chạy, cây process con của nó luôn có tổng cộng **6 PID** (gốc + 5 con/cháu), toàn bộ đều
   tên `msedgewebview2.exe` (không phân biệt được bằng tên process — muốn biết process nào là GPU/
   renderer/audio-service/network cần đọc `CommandLine` qua `Win32_Process` để xem cờ `--type=`,
   spike này CHƯA làm bước đó). `ProcessTreeHelper` (dùng WMI `Win32_Process.ParentProcessId`) dò
   đúng toàn bộ 6 PID này mỗi lần, không bị sót.
2. **`ActivateAudioInterfaceAsync` + callback COM tự viết (`IActivateAudioInterfaceCompletionHandler`
   + `IAgileObject`) hoạt động đúng** — đây là phần rủi ro cao nhất theo yêu cầu ban đầu của spike.
   HRESULT trả về là `S_OK` (0x00000000) ở LẦN ĐẦU activation sau mỗi lần mở app, 5/5 lần.
3. **Đã tìm và SỬA 1 lỗi marshaling thật** trong bản đầu: dùng
   `[MarshalAs(UnmanagedType.IUnknown)] out object` cho tham số thứ 2 của
   `IActivateAudioInterfaceAsyncOperation.GetActivateResult` khiến object trả về, dù activation báo
   `S_OK` và con trỏ khác null, lại KHÔNG QueryInterface được chính `IID_IAudioClient` vừa dùng để
   activate (`E_NOINTERFACE`, `0x80004002`) — về nguyên tắc COM, 1 object luôn phải tự QI được
   chính nó, nên đây chắc chắn là lỗi marshaling phía mình, không phải giới hạn của Windows. Sửa
   bằng cách đổi sang `out IntPtr` + tự `Marshal.GetObjectForIUnknown` (giống cách đã dùng cho
   `IAudioClient.GetService`) — sau khi sửa, cast thành công 5/5 lần.
4. **`IAudioClient.Initialize` với `LOOPBACK | EVENTCALLBACK` + format hardcode thành công ở LẦN
   ĐẦU, 5/5 lần.** `GetService(IID_IAudioCaptureClient)`, `Start()`, và pipeline phát lại qua
   `WasapiOut` (`Init` + `Play`) đều thành công theo sau, không exception.
5. **Vòng lặp capture chạy ổn định, không crash, trong toàn bộ thời gian quan sát** (~3-6 giây mỗi
   lần) — nhưng vì KHÔNG có cách nào tự động click nút Play trong trang WebView2 (Chromium chặn
   autoplay policy với gọi script tổng hợp, cần user-gesture thật), `TotalBytesCaptured` (tổng số
   byte PCM nhận qua `GetBuffer`/`ReleaseBuffer`) LUÔN LÀ 0 trong toàn bộ phiên tự động — **đây là
   kết quả PHÙ HỢP với dự đoán (không có tiếng phát ra thì không có gì để capture), NHƯNG KHÔNG PHẢI
   bằng chứng cho việc capture thực sự đọc được sample "sống". Việc xác nhận cuối cùng — có nghe
   được tiếng bíp qua đường capture hay không — BẮT BUỘC phải làm thủ công theo checklist T1-T4.**
6. **`SessionMuter` (mute/unmute qua NAudio `AudioSessionManager`) chạy không lỗi**, nhưng KHÔNG tìm
   thấy session nào khớp PID (0 session matched) — vì Windows Audio chỉ tạo session cho 1 process
   SAU KHI process đó thực sự render âm thanh ít nhất 1 lần; ở đây chưa có tiếng nào phát ra nên
   chưa có session nào tồn tại để mute. **Ý nghĩa cho checklist T2**: phải bấm Play trong WebView2
   TRƯỚC rồi mới bấm Mute WebView2, thứ tự ngược lại sẽ không mute được gì.

### Phát hiện quan trọng CẦN GIẢI QUYẾT trước khi chốt P1: chu kỳ start/stop lặp lại thất bại — ĐÃ ĐIỀU TRA SÂU, ĐÃ TÌM RA FIX

**Cập nhật (phiên điều tra thứ 2, sau khi 1 user thật tái hiện lỗi thủ công với video YouTube thật):**
mục này ban đầu nghi ngờ nguyên nhân là **navigation WebView2** (user thật báo cáo: capture 2 chu kỳ
Start/Stop sạch → navigate lại → Start thất bại 3 lần liên tiếp, HRESULT `0x8000FFFF` rồi
`0x88890021`, không tự phục hồi). Sau khi tự tái hiện bằng code (KHÔNG chỉ đọc log cũ), kết luận
**đã thay đổi hoàn toàn**: bug **KHÔNG liên quan đến navigation** — nó là 1 vấn đề GC/COM thuần tuý,
và **đã tìm được fix xác nhận hoạt động qua nhiều lần chạy độc lập**.

#### Cách tái hiện đã dùng (tự động hoá, không cần click UI thật)

Thêm 2 hook `#if DEBUG` mới trong `MainWindow.xaml.cs` (xem mục "Ghi chú thêm" bên dưới để biết đầy
đủ biến môi trường):

- `SPIKE_REPRO_TIMING_GAP=<ms>`: lặp Start→giữ 800ms→Stop→chờ gapMs, 6 chu kỳ, **KHÔNG có navigation
  nào cả** (giữ nguyên trang test-tone ban đầu) — dùng để cô lập biến số timing khỏi biến số
  navigation.
- `SPIKE_REPRO_NAV_BUG=reload|diff`: tái hiện ĐÚNG trình tự user thật báo cáo, dùng `Navigate()` tới
  video YouTube thật (`https://www.youtube.com/watch?v=dQw4w9WgXcQ` và
  `https://www.youtube.com/watch?v=jNQXAC9IVRw`), chờ đúng sự kiện `NavigationCompleted` (không đoán
  mò bằng `Task.Delay`) trước khi tiếp tục: navigate → Start→Stop (chu kỳ 1) → Start→Stop (chu kỳ 2,
  không navigate ở giữa) → navigate lại (`reload` = cùng URL, `diff` = video khác) → thử Start liên
  tiếp (3 lần ngay, rồi chờ tăng dần 1s/3s/5s/10s, tổng 7 lần thử).

#### Kết quả thực nghiệm (đã chạy nhiều lần, không phải 1 lần rồi vội kết luận)

**1. Kịch bản THUẦN TIMING (không navigation) — chạy 4 lần, mỗi lần 1 giá trị gap khác nhau:**

| gap (Stop→Start) | Chu kỳ #1 | Chu kỳ #2-6 |
|---|---|---|
| 500ms  | Thành công | **THẤT BẠI cả 5** (0x8000FFFF → 0x88890021) |
| 1000ms | Thành công | **THẤT BẠI cả 5** (giống hệt) |
| 2000ms | Thành công | **THẤT BẠI cả 5** (giống hệt) |
| 5000ms | Thành công | **THẤT BẠI cả 5** (giống hệt) |

Kết luận trực tiếp từ bảng này: **bug này không phải race-condition/thời gian ổn định** — nếu là
vậy, gap 5000ms phải khác gap 500ms. Ở đây KHÔNG khác gì cả (identical 1/6 trên cả 4 mức). Đây
cũng chính là bảng chứng cho thấy **navigation KHÔNG PHẢI nguyên nhân** — bug tái hiện 100% chỉ bằng
Start/Stop lặp lại, không hề navigate.

**2. Test 2 giả thuyết sửa lỗi KHÔNG hiệu quả (tại gap=500ms):**

- **Giả thuyết #1 (giải phóng tường minh `IActivateAudioInterfaceAsyncOperation`)**: code gốc dùng
  `out _` để nhận object COM này từ `ActivateAudioInterfaceAsync` rồi bỏ luôn — RCW đó chỉ được
  native release khi CLR finalize, không xác định khi nào. Đã sửa để giữ biến và gọi
  `Marshal.ReleaseComObject` tường minh, bật/tắt bằng cờ để so sánh A/B. **Kết quả: KHÔNG có tác
  dụng** — vẫn 1/6 chu kỳ thành công, giống hệt bản gốc. **Vẫn giữ fix này trong code** (COM hygiene
  đúng đắn, không có lý do để bỏ), nhưng đây KHÔNG PHẢI nguyên nhân chính của bug.
- **Giả thuyết #4 (`PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` thay vì `INCLUDE`)**: **Kết
  quả: KHÔNG có tác dụng** — chữ ký lỗi giống hệt (1/6 chu kỳ thành công) ở cả 2 mode. Loại trừ giả
  thuyết này; giữ `INCLUDE_TARGET_PROCESS_TREE` làm mặc định vì đó mới đúng ý nghĩa chức năng thật
  (capture audio CỦA WebView2, không phải "mọi thứ trừ WebView2").
- **Giả thuyết #3 (retry-with-backoff)** cũng thất bại khi test độc lập không kèm fix GC: trong lần
  chạy repro navigation gốc (không bật fix), cả 7 lần thử Start sau navigate lại (3 lần ngay + chờ
  tăng dần 1s/3s/5s/10s, tổng ~30 giây) đều thất bại giống hệt nhau — **không có dấu hiệu tự phục hồi
  theo thời gian** dù chờ bao lâu trong 1 phiên.
- **Giả thuyết #2 (settle-delay sau navigation)**: không test trực tiếp bằng navigation thật với giá
  trị delay khác 0, nhưng **[Suy luận, không phải đo trực tiếp]** kết quả bảng THUẦN TIMING ở trên
  (delay tới 5000ms không giúp gì) khiến giả thuyết này gần như chắc chắn cũng không đủ — cơ chế
  delay-sau-Stop và delay-sau-Navigate về bản chất chờ cùng 1 loại điều kiện trước khi Start tiếp.

**3. Giả thuyết #5 (PID render audio đổi identity qua navigation)**: dump cây process (kèm phân loại
qua `--type=`/`--utility-sub-type=` trong `CommandLine`, đọc qua WMI) trước/sau navigate lại (cùng
URL). Kết quả: **`utility (audio.mojom.AudioService)` giữ NGUYÊN 1 PID** qua suốt phiên (kể cả sau
reload) — chỉ các process `renderer` và `utility (video_capture.mojom.VideoCaptureService)` đổi PID.
**Bác bỏ giả thuyết #5 ở dạng mạnh** (không phải do 1 "audio child process" mới sinh ra không khớp
target cũ) — vì target thật ("audio service" thực sự phát âm thanh) không hề đổi PID, nhưng bug vẫn
xảy ra. [Chưa kiểm tra riêng cho trường hợp navigate sang video KHÁC HẲN xem AudioService PID có đổi
không — chỉ xác nhận cho trường hợp reload cùng URL].

**4. FIX ĐÃ XÁC NHẬN — ép `GC.Collect()` + `GC.WaitForPendingFinalizers()` + `GC.Collect()` ngay sau
`Stop()`:**

Đây là thay đổi DUY NHẤT tạo ra khác biệt. Đã xác nhận qua **3 lần chạy độc lập riêng biệt** (mỗi lần
là 1 process exe mới) ở kịch bản thuần timing (gap=500ms): **18/18 chu kỳ thành công** (so với 4/24
khi không có fix, cùng điều kiện). Sau đó xác nhận tiếp bằng kịch bản navigation THẬT (video YouTube
thật, không phải giả lập) — **4 lần chạy riêng biệt** (2× `reload`, 1× `diff` sang video khác, 1× lại
`reload` với cấu hình mặc định không cần bật cờ gì thêm): **cả 4 lần đều** — chu kỳ 1 thành công, chu
kỳ 2 thành công (trước đây LUÔN thất bại), navigate lại, và **lần thử Start ĐẦU TIÊN sau navigate lại
thành công ngay** (trước đây thất bại cả 7 lần thử trong ~30 giây). Video khác (`diff`) hoạt động
giống hệt video reload (`reload`) — cả 2 kịch bản đều được fix.

**Giải thích hợp lý nhất (không phải suy đoán vu vơ — dựa trên bằng chứng thực nghiệm ở trên):** có
ít nhất 1 COM RCW (runtime-callable wrapper phía .NET, **chưa xác định chính xác là object nào** —
đã loại trừ riêng `IActivateAudioInterfaceAsyncOperation`) chỉ thực sự gọi `Release()` xuống native
khi CLR **finalize** nó qua GC, KHÔNG PHẢI khi code gọi `Marshal.ReleaseComObject` tường minh (khả
năng cao là do RCW bị cache theo định danh COM identity và vẫn còn 1 tham chiếu ẩn khác chưa giải
phóng — ví dụ tham số `activateOperation` mà native truyền vào callback `ActivateCompleted` có thể
dùng CHUNG 1 RCW với biến cục bộ trong `StartAsync`). Khi `mmdevapi.dll` thấy vẫn còn 1 "client cũ"
gắn với cùng cặp (nguồn loopback, target process) chưa thực sự giải phóng xong, lần
activate/`Initialize` kế tiếp cho CÙNG target process đó thất bại — **hoàn toàn độc lập với có
navigation hay không**, giải thích tại sao navigation "có vẻ" liên quan (chỉ vì user thật thường
navigate xen giữa các lần Start/Stop) nhưng thực ra không phải nguyên nhân.

**[Chưa xác minh đầy đủ]**: chưa xác định được CHÍNH XÁC object C#/COM nào là thứ cần GC mới giải
phóng (chỉ xác nhận forced-GC sửa được, chưa cô lập được object cụ thể bằng cách loại trừ từng cái
một — ví dụ chưa test riêng việc bỏ `WasapiOut`/`MediaFoundationResampler` ra khỏi luồng để xem có
thay đổi kết quả không). Đây là "biện pháp thô" đã xác nhận hiệu quả, không phải fix "phẫu thuật
chính xác".

**Đánh đổi của fix**: `GC.Collect()` ép buộc (đặc biệt kèm `GC.WaitForPendingFinalizers()`) có thể
gây 1 khoảng dừng ngắn (thường vài chục ms, có thể hơn tuỳ heap) mỗi lần bấm "Dừng capture" — chấp
nhận được vì đây là hành động người dùng chủ động bấm 1 lần, không phải trong vòng lặp render/audio
nóng. Fix này **đã được áp dụng làm mặc định cho MỌI build** (Debug và Release), xem
`StopCaptureAndPlayback()` trong `MainWindow.xaml.cs`.

## Kết luận

**P1 ĐÃ ĐƯỢC XÁC NHẬN DÙNG ĐƯỢC** cho karamimeh, sau khi tìm và xác nhận fix cho vấn đề nghiêm trọng
nhất (start/stop lặp lại thất bại):

1. Phần khó nhất — activation process-loopback qua P/Invoke thủ công (không có trong NAudio) —
   **hoạt động đúng** trên máy test thật (Windows 10 Pro 19045), kể cả phần khó nhất trong phần khó
   (callback COM `IActivateAudioInterfaceCompletionHandler` + `IAgileObject`), sau khi sửa 1 lỗi
   marshaling thật đã tìm ra.
2. **Vấn đề "chu kỳ start/stop lặp lại thất bại"** (từng được coi là rủi ro nghiêm trọng chưa có lời
   giải) **ĐÃ ĐƯỢC SỬA** — ép `GC.Collect()` sau `Stop()` giải quyết triệt để, xác nhận qua 7 lần
   chạy độc lập (3 kịch bản thuần timing + 4 kịch bản navigation thật với video YouTube thật), không
   có lần nào thất bại sau khi bật fix. Đây KHÔNG PHẢI vấn đề navigation như nghi ngờ ban đầu, mà là
   vấn đề GC/COM-RCW thuần tuý — quan trọng để hiểu đúng bản chất nếu cần tiếp tục điều tra sâu hơn.
3. Việc capture có thực sự tái tạo đúng waveform 440Hz "sống" (không giật/rè, không trễ nghe được)
   **CHƯA được xác nhận bằng tai** — bắt buộc người dùng tự làm checklist T1-T4 ở trên. Đây vẫn là
   điều kiện còn thiếu duy nhất để chốt "dùng được" hoàn toàn.

**Khuyến nghị bước tiếp theo**: (a) người dùng tự chạy checklist T1-T4 thủ công để xác nhận phần âm
thanh nghe được (đặc biệt test lại T2-T3 qua NHIỀU chu kỳ Start/Stop VÀ nhiều lần navigate, để xác
nhận fix giữ được trong điều kiện dùng thực tế kéo dài, không chỉ trong kịch bản tự động hoá ngắn);
(b) nếu muốn triệt để hơn, dành thêm thời gian cô lập CHÍNH XÁC object COM nào cần GC mới release
(hiện tại mới xác nhận "có tác dụng", chưa "biết chính xác tại sao" ở mức object) — không bắt buộc
cho việc dùng P1, nhưng hữu ích nếu muốn thay `GC.Collect()` ép buộc (có chi phí hiệu năng nhỏ) bằng
1 `Marshal.Release`/`Dispose` tường minh, chính xác hơn; (c) **KHÔNG CẦN chuyển sang P2** dựa trên
bằng chứng hiện tại — rủi ro chính đã được giải quyết, P2 vẫn là phương án dự phòng hợp lý nếu (b)
phát hiện thêm vấn đề mới trong quá trình dùng thực tế kéo dài mà (a) chưa phát hiện ra.

## Ghi chú thêm

- Có 1 khối code dev-only (`#if DEBUG`, gated bởi biến môi trường, KHÔNG bật mặc định) trong
  `MainWindow.xaml.cs`, đọc 1 trong 3 biến môi trường theo thứ tự ưu tiên (chỉ 1 hook chạy mỗi lần):
  - `SPIKE_REPRO_TIMING_GAP=<số ms>`: kịch bản THUẦN TIMING, xem mục "Phát hiện quan trọng" ở trên.
  - `SPIKE_REPRO_NAV_BUG=reload|diff|1`: kịch bản tái hiện bug navigation bằng video YouTube thật,
    xem mục "Phát hiện quan trọng" ở trên. Điều khiển thêm bằng `SPIKE_FIX_NAV_SETTLE_MS=<số ms>`
    (chờ thêm sau mỗi navigation — đã xác nhận không cần thiết, giữ lại để dễ test lại nếu cần) và
    `SPIKE_FIX_LOOPBACK_MODE=exclude` (dùng `EXCLUDE_TARGET_PROCESS_TREE` thay vì `INCLUDE` mặc
    định — đã xác nhận không ảnh hưởng tới bug, chỉ đổi ý nghĩa chức năng thật).
  - `SPIKE_AUTOSTART_CAPTURE=1`: hook gốc — tự bấm Start/mute/unmute để agent kiểm chứng không cần
    click UI thật.
  Không hook nào ảnh hưởng hành vi người dùng thật (mặc định KHÔNG set biến môi trường nào). Có thể
  xoá các khối này sau khi review xong spike, hoặc giữ lại làm công cụ hồi quy nhanh cho lần sau.
- Có ghi 1 file log tạm tại `%TEMP%\ProcessLoopbackSpike.debuglog.txt` (chỉ khi build Debug) để agent
  tự đọc lại log — không phải tính năng chính thức, không dùng trong Release.
- Fix chính thức cho bug "start/stop lặp lại thất bại" (`GC.Collect()` + `GC.WaitForPendingFinalizers()`
  sau `Stop()`, xem `StopCaptureAndPlayback()` trong `MainWindow.xaml.cs`) áp dụng cho MỌI build,
  không gated bởi `#if DEBUG` hay biến môi trường — đây là fix sản phẩm thật, không phải chẩn đoán.
- Đã sửa `ProcessLoopbackCapture.StartAsync()` để nhận `PROCESS_LOOPBACK_MODE` qua tham số (mặc định
  `INCLUDE_TARGET_PROCESS_TREE`, đúng hành vi gốc) thay vì hardcode, và để giải phóng tường minh
  `IActivateAudioInterfaceAsyncOperation` qua `Marshal.ReleaseComObject` (COM hygiene đúng đắn, dù
  đã xác nhận KHÔNG phải nguyên nhân chính của bug — xem mục "Phát hiện quan trọng").
