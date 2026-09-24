using System.Management;
using System.Text.RegularExpressions;

namespace ProcessLoopbackSpike.Audio;

/// <summary>
/// Duyệt toàn bộ cây process con (con, cháu, chắt...) bắt đầu từ 1 PID gốc, dùng WMI
/// (Win32_Process.ParentProcessId) qua System.Management. Cần thiết vì WebView2.BrowserProcessId
/// (process "browser" chính của Chromium) hầu như KHÔNG PHẢI là process thực sự render audio —
/// Chromium/WebView2 tách nhiều loại process con (renderer, GPU, audio service, network...), và
/// audio session trong Windows Audio thường gắn với 1 trong các process con đó, không phải PID gốc.
/// </summary>
internal static class ProcessTreeHelper
{
    /// <summary>
    /// Trả về tập hợp gồm rootPid + toàn bộ PID con cháu (đệ quy) tại thời điểm gọi.
    /// Đây là snapshot một lần — process con của WebView2 có thể sinh/chết liên tục, nên gọi lại
    /// hàm này mỗi lần cần match session thay vì cache lâu dài.
    /// </summary>
    public static HashSet<uint> GetProcessTreePids(uint rootPid)
    {
        var result = new HashSet<uint> { rootPid };

        // Đọc toàn bộ Win32_Process 1 lần rồi tự dựng map con->cha trong bộ nhớ, tránh N truy vấn
        // WMI riêng lẻ (rất chậm nếu Chromium có 15-20 process con).
        var parentOf = new Dictionary<uint, uint>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId FROM Win32_Process");
            using var results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    uint pid = Convert.ToUInt32(obj["ProcessId"]);
                    uint ppid = Convert.ToUInt32(obj["ParentProcessId"]);
                    parentOf[pid] = ppid;
                }
            }
        }
        catch (Exception)
        {
            // WMI có thể không khả dụng trong vài môi trường hạn chế — trả về ít nhất rootPid,
            // không throw, để phần còn lại của app vẫn chạy được (chỉ là mute kém chính xác hơn).
            return result;
        }

        // Với mỗi process trong toàn hệ thống, đi ngược lên chuỗi cha của nó; nếu chuỗi đó chạm
        // rootPid tại bất kỳ điểm nào, toàn bộ chuỗi (từ pid đó xuống) đều là con cháu của rootPid.
        foreach (uint pid in parentOf.Keys)
        {
            if (result.Contains(pid))
            {
                continue;
            }

            var chain = new List<uint>();
            uint current = pid;
            var visited = new HashSet<uint>();

            while (parentOf.TryGetValue(current, out uint parent) && visited.Add(current))
            {
                chain.Add(current);
                if (result.Contains(parent) || parent == rootPid)
                {
                    result.Add(parent);
                    foreach (uint c in chain)
                    {
                        result.Add(c);
                    }

                    break;
                }

                current = parent;
            }
        }

        return result;
    }

    /// <summary>
    /// Đọc <c>CommandLine</c> qua WMI cho từng PID trong <paramref name="pids"/> và trích cờ
    /// <c>--type=</c> (và <c>--utility-sub-type=</c> nếu có) để phân biệt renderer/gpu-process/
    /// utility(audio service)/... — Chromium/WebView2 không phân biệt được các process con chỉ
    /// bằng tên (đều là <c>msedgewebview2.exe</c>). Dùng cho mục đích chẩn đoán (spike điều tra
    /// bug navigation) — xem README.md, mục "Phát hiện quan trọng". Không dùng trong luồng chức
    /// năng chính (mute/capture không cần phân loại này để hoạt động).
    /// </summary>
    public static Dictionary<uint, string> GetProcessTypes(IEnumerable<uint> pids)
    {
        var pidSet = new HashSet<uint>(pids);
        var result = new Dictionary<uint, string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process");
            using var results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    uint pid = Convert.ToUInt32(obj["ProcessId"]);
                    if (!pidSet.Contains(pid))
                    {
                        continue;
                    }

                    string commandLine = obj["CommandLine"] as string ?? "";
                    string label = "(main/browser — không có --type=)";

                    var typeMatch = Regex.Match(commandLine, @"--type=([^\s]+)");
                    if (typeMatch.Success)
                    {
                        label = typeMatch.Groups[1].Value;
                        var subMatch = Regex.Match(commandLine, @"--utility-sub-type=([^\s]+)");
                        if (subMatch.Success)
                        {
                            label += $" ({subMatch.Groups[1].Value})";
                        }
                    }

                    result[pid] = label;
                }
            }
        }
        catch (Exception)
        {
            // Chỉ phục vụ chẩn đoán — không được làm hỏng luồng chính nếu WMI lỗi.
        }

        return result;
    }
}
