using NAudio.CoreAudioApi;

namespace KaraokeMixer.Core.Audio;

internal sealed record MuteResult(bool Success, IReadOnlyList<uint> MatchedPids, string? Error);

/// <summary>
/// Điều khiển âm lượng session Windows Audio (KHÔNG phải mute cả app qua API riêng của Chromium)
/// cho toàn bộ cây process của WebView2. Dùng NAudio's MMDeviceEnumerator + AudioSessionManager +
/// SimpleAudioVolume.
///
/// ĐỔI KIẾN TRÚC (2026-09-24) — file này KHÔNG còn "mute rồi capture lại" nữa:
///
/// Lịch sử: bản trước dùng cơ chế này để làm im lặng session gốc của YouTube (qua Mute=true, sau đó
/// qua Volume=0 khi phát hiện Mute=true làm hỏng process-loopback capture), rồi capture riêng tiếng
/// YouTube qua <c>ProcessLoopbackCapture</c> và phát lại qua bộ trộn phần mềm của app.
///
/// PHÁT HIỆN THỰC TẾ (từ 1 người dùng thật, không phải suy đoán): CẢ Mute=true LẪN Volume=0 đều
/// làm <c>ProcessLoopbackCapture.TotalBytesCaptured</c> đứng yên ở đúng 0 suốt phiên — nghĩa là bất
/// kỳ cách nào làm session "im lặng" đều khiến process bị capture (rất có thể là chính Chromium, tự
/// phản ứng với thông báo đổi trạng thái session của chính nó) ngừng render audio thật sự, không chỉ
/// ngừng phát ra tai người nghe. Không tìm được cách nào vừa im lặng được tiếng gốc vừa giữ capture
/// sống trong thời gian hợp lý.
///
/// QUYẾT ĐỊNH KIẾN TRÚC MỚI: bỏ hẳn việc capture lại YouTube. Để YouTube phát bình thường (native,
/// không đụng vào), và app chỉ phát riêng nhánh mic (đã qua EQ/Echo) ra CÙNG 1 thiết bị output —
/// Windows Audio Engine tự trộn (mix) mọi app đang phát chung 1 thiết bị ở chế độ Shared, nên người
/// dùng nghe được cả 2 mà không cần app tự làm việc mixing đó. Ưu điểm: không còn phụ thuộc vào cơ
/// chế process-loopback capture (vốn đã tốn rất nhiều công sức điều tra lỗi COM/GC — xem lịch sử
/// trong ProcessLoopbackCapture.cs/README các bản spike — và giờ phát hiện thêm giới hạn nói trên),
/// độ trễ nhạc bằng 0 (không qua vòng capture→phát lại nào), và KHÔNG mất tính năng nào — thiết kế
/// gốc từ đầu vốn chỉ cho mic đi qua EQ/Echo, nhạc chỉ có volume.
///
/// Vai trò MỚI của file này: "Music Volume" trong UI giờ chỉnh THẲNG vào Volume thật của session
/// Windows của YouTube (0.0–1.0, đúng thang đo thật của Windows — khác với MicVolume trong
/// AudioMixerCore vốn là hệ số khuếch đại phần mềm, có thể >1.0 để boost). Không còn khái niệm
/// "mute" nữa — luôn đảm bảo Mute=false (dọn sạch nếu có sót từ phiên bản cũ), chỉ chỉnh Volume.
/// </summary>
internal static class SessionMuter
{
    /// <summary>Volume gốc (trước khi app này đụng vào) của mỗi PID, để khi Stop() có thể khôi phục
    /// đúng giá trị cũ thay vì hardcode về 1.0f — tránh ghi đè volume mà chính người dùng (hoặc 1 app
    /// khác) đã tự chỉnh cho session đó trước khi app này chạm vào. Static vì chỉ 1 <c>AudioMixerCore</c>
    /// instance trong 1 process gọi tại 1 thời điểm.</summary>
    private static readonly Dictionary<uint, float> OriginalVolumeByPid = new();

    /// <summary>
    /// Tìm mọi audio session (trên default render device) có ProcessID nằm trong
    /// <paramref name="targetPids"/> và set Volume = <paramref name="volume"/> (luôn đảm bảo
    /// Mute=false). Trả về danh sách PID thực sự khớp được (để hiển thị debug). Lưu volume gốc lần
    /// đầu chạm vào mỗi PID, để <see cref="RestoreOriginalVolume"/> khôi phục đúng.
    /// </summary>
    public static MuteResult SetVolumeForProcessTree(HashSet<uint> targetPids, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
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

                if (!OriginalVolumeByPid.ContainsKey(pid))
                {
                    OriginalVolumeByPid[pid] = simpleVolume.Volume;
                }

                // Đảm bảo không còn sót Mute=true từ 1 phiên chạy trước (bản cũ hơn của app này, hoặc
                // người dùng tự mute thủ công) — chỉ Volume là kênh điều khiển hợp lệ ở đây.
                simpleVolume.Mute = false;
                simpleVolume.Volume = volume;

                matched.Add(pid);
            }

            return new MuteResult(true, matched, null);
        }
        catch (Exception ex)
        {
            return new MuteResult(false, matched, ex.Message);
        }
    }

    /// <summary>Khôi phục Volume gốc (đã lưu từ lần đầu <see cref="SetVolumeForProcessTree"/> chạm
    /// vào mỗi PID) — hoặc 1.0f nếu không có giá trị gốc (vd. session xuất hiện sau khi app đã set).
    /// Gọi khi Stop() để trả lại đúng trạng thái âm lượng YouTube cho người dùng.</summary>
    public static MuteResult RestoreOriginalVolume(HashSet<uint> targetPids)
    {
        var matched = new List<uint>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;

            var sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;

                if (!targetPids.Contains(pid))
                {
                    continue;
                }

                float restoreVolume = OriginalVolumeByPid.TryGetValue(pid, out float original) ? original : 1f;
                var simpleVolume = session.SimpleAudioVolume;
                simpleVolume.Mute = false;
                simpleVolume.Volume = restoreVolume;
                OriginalVolumeByPid.Remove(pid);

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
