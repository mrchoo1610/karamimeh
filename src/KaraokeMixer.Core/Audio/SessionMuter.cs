using NAudio.CoreAudioApi;

namespace KaraokeMixer.Core.Audio;

internal sealed record MuteResult(bool Success, IReadOnlyList<uint> MatchedPids, string? Error);

/// <summary>
/// Mute/unmute session âm thanh (ở mức Windows Audio session, KHÔNG phải mute cả app qua API
/// riêng của Chromium) cho toàn bộ cây process của WebView2. Dùng NAudio's MMDeviceEnumerator +
/// AudioSessionManager + SimpleAudioVolume — đây LÀ phần NAudio hỗ trợ sẵn (khác với phần
/// process-loopback capture phải tự P/Invoke).
///
/// BUG FIX — KHÔNG dùng <c>SimpleAudioVolume.Mute</c> nữa, dùng <c>SimpleAudioVolume.Volume = 0f</c>
/// thay thế (giữ <c>Mute = false</c> tường minh):
///
/// Bằng chứng thực tế (từ 1 người dùng thật, không phải suy đoán): trong lúc app đang chạy và
/// <c>AudioMixerCore.YoutubeTotalBytesCaptured</c> đứng yên ở đúng 0 suốt cả phiên (dù video YouTube
/// đang phát thật), người dùng tự mở Windows Volume Mixer và bỏ mute thủ công đúng session WebView2/
/// Edge mà <see cref="SetMuteForProcessTree"/> đã set <c>Mute = true</c> — NGAY LẬP TỨC sau đó
/// <c>YoutubeTotalBytesCaptured</c> bắt đầu tăng (music peak khác 0). Việc mute chính là nguyên nhân
/// khiến process-loopback capture nhận đúng 0 gói tin, không phải do mic capture chạy đồng thời hay
/// do thời điểm GC (2 giả thuyết đó đã bị loại bằng thực nghiệm lặp lại — xem
/// experiments/ConcurrencyRepro — không tái hiện được triệu chứng 0-byte dù đã test mic đồng thời,
/// GC trước/sau, VÀ set <c>Mute = true</c> trên chính 1 session giả lập (cả tự-capture lẫn 1 process
/// con thật khác) — thực nghiệm KHÔNG cho thấy Windows Audio Engine tự nó ngừng cấp gói tin cho
/// process-loopback tap chỉ vì session bị mute).
///
/// GIẢI THÍCH HỢP LÝ NHẤT (KHÔNG kiểm chứng được ở mức source code Chromium từ môi trường này —
/// coi là [Unverified]/[Suy luận], không phải sự thật đã xác nhận): nhiều khả năng bản thân tiến
/// trình bị capture (Chromium/WebView2, KHÔNG PHẢI Windows Audio Engine nói chung) tự lắng nghe sự
/// kiện đổi trạng thái session của chính nó (kiểu <c>IAudioSessionEvents::OnSimpleVolumeChanged</c>,
/// vốn mang cả giá trị volume mới LẪN cờ mute mới) và khi thấy <c>bNewMute == true</c>, tự tối ưu
/// bằng cách NGỪNG render audio thật sự (không gọi GetBuffer/ReleaseBuffer nữa) để tiết kiệm CPU cho
/// tab/nội dung "không ai nghe" — nghĩa là KHÔNG CÒN GÓI TIN NÀO được tạo ra cho BẤT KỲ ai tap vào,
/// kể cả process-loopback capture của app này, chứ không phải do Windows chặn riêng đường capture.
/// Set <c>Volume = 0f</c> (giữ <c>Mute = false</c>) tạo ra cùng hiệu ứng "im lặng cho người dùng"
/// nhưng (giả thuyết) không kích hoạt logic tối ưu-theo-mute đó, vì thay đổi float volume và đổi cờ
/// mute là 2 tín hiệu tách biệt trong Core Audio API.
///
/// ĐÃ KIỂM CHỨNG (xem experiments/ConcurrencyRepro): dùng <c>Volume = 0f</c> thay <c>Mute = true</c>
/// KHÔNG làm hỏng process-loopback capture trong mọi kịch bản harness đã thử (tự-capture VÀ capture
/// 1 process con thật khác, có/không có mic đồng thời) — nhưng CẢNH BÁO QUAN TRỌNG: harness đó dùng
/// NAudio WasapiOut phát 1 tone tổng hợp làm "process giả lập YouTube", bản thân nó KHÔNG có logic
/// "tự ngừng render khi bị mute" như giả thuyết trên mô tả cho Chromium thật — nên harness đó CŨNG
/// KHÔNG tái hiện được lỗi gốc ngay cả với <c>Mute = true</c> (xem comment ở trên). Vì vậy validate
/// thật sự cho fix này PHẢI đến từ người dùng thật chạy lại với WebView2 + YouTube thật, KHÔNG chỉ
/// dựa vào kết quả harness — ghi rõ điều này để không lặp lại sai lầm "tuyên bố đã sửa" chỉ dựa trên
/// 1 lần chạy hoặc 1 công cụ tổng hợp không đủ trung thực với hiện tượng gốc.
/// </summary>
internal static class SessionMuter
{
    /// <summary>Volume gốc (trước khi app này set về 0) của mỗi PID đã bị "mute" qua
    /// <see cref="SetMuteForProcessTree"/>, để <c>mute:false</c> khôi phục đúng giá trị cũ thay vì
    /// hardcode về 1.0f — tránh ghi đè volume mà chính người dùng (hoặc 1 app khác) đã tự chỉnh cho
    /// session đó trước khi app này đụng vào. Static vì <see cref="SessionMuter"/> chỉ được 1
    /// <c>AudioMixerCore</c> instance trong 1 process gọi tại 1 thời điểm (cùng giả định với
    /// <c>AudioMixerCore._mutedBrowserProcessId</c> — xem AudioMixerCore.cs).</summary>
    private static readonly Dictionary<uint, float> OriginalVolumeByPid = new();

    /// <summary>
    /// Tìm mọi audio session (trên default render device) có ProcessID nằm trong <paramref name="targetPids"/>
    /// và, khi <paramref name="mute"/>=true, set Volume=0 (Mute vẫn để false — xem big comment ở
    /// trên lý do KHÔNG dùng Mute=true nữa); khi <paramref name="mute"/>=false, khôi phục lại Volume
    /// gốc đã lưu (hoặc 1.0f nếu vì lý do gì đó không có giá trị gốc, ví dụ session mới xuất hiện sau
    /// khi mute). Trả về danh sách PID thực sự khớp được (để hiển thị debug).
    /// </summary>
    public static MuteResult SetMuteForProcessTree(HashSet<uint> targetPids, bool mute)
    {
        var matched = new List<uint>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;

            // SessionCollection/AudioSessionControl không implement IDisposable trong NAudio —
            // không bọc bằng `using`, chỉ MMDevice mới cần giải phóng tường minh.
            var sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;

                if (!targetPids.Contains(pid))
                {
                    continue;
                }

                var simpleVolume = session.SimpleAudioVolume;

                if (mute)
                {
                    if (!OriginalVolumeByPid.ContainsKey(pid))
                    {
                        OriginalVolumeByPid[pid] = simpleVolume.Volume;
                    }

                    // Tường minh đảm bảo Mute=false — nếu 1 lần chạy app cũ (trước fix này) hoặc
                    // người dùng đã set Mute=true trước đó, phải bỏ nó đi, nếu không Volume=0 sẽ
                    // cộng dồn với Mute=true và có thể vẫn kích hoạt đúng behavior muốn tránh.
                    simpleVolume.Mute = false;
                    simpleVolume.Volume = 0f;
                }
                else
                {
                    float restoreVolume = OriginalVolumeByPid.TryGetValue(pid, out float original) ? original : 1f;
                    simpleVolume.Mute = false;
                    simpleVolume.Volume = restoreVolume;
                    OriginalVolumeByPid.Remove(pid);
                }

                matched.Add(pid);
            }

            return new MuteResult(true, matched, null);
        }
        catch (Exception ex)
        {
            return new MuteResult(false, matched, ex.Message);
        }
    }
}
