using NAudio.CoreAudioApi;

namespace ProcessLoopbackSpike.Audio;

internal sealed record MuteResult(bool Success, IReadOnlyList<uint> MatchedPids, string? Error);

/// <summary>
/// Mute/unmute session âm thanh (ở mức Windows Audio session, KHÔNG phải mute cả app qua API
/// riêng của Chromium) cho toàn bộ cây process của WebView2. Dùng NAudio's MMDeviceEnumerator +
/// AudioSessionManager + SimpleAudioVolume — đây LÀ phần NAudio hỗ trợ sẵn (khác với phần
/// process-loopback capture phải tự P/Invoke).
/// </summary>
internal static class SessionMuter
{
    /// <summary>
    /// Tìm mọi audio session (trên default render device) có ProcessID nằm trong <paramref name="targetPids"/>
    /// và set Mute = <paramref name="mute"/>. Trả về danh sách PID thực sự khớp được (để hiển thị debug).
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

                session.SimpleAudioVolume.Mute = mute;
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
