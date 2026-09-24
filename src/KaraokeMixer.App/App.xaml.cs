using Microsoft.UI.Xaml;
using Velopack;
using Velopack.Sources;

namespace KaraokeMixer.App;

/// <summary>
/// Application entry point. Creates and activates MainWindow — same shape as both spikes.
/// </summary>
public partial class App : Application
{
    // Đặt đúng theo repo GitHub dùng để phát hành bản cài đặt (xem README.md mục "Đóng gói & phát
    // hành"). GithubSource chỉ đọc GitHub Releases công khai của repo này — không cần token.
    private const string UpdateRepoUrl = "https://github.com/mrchoo1610/karamimeh";

    private Window? _window;

    public App()
    {
        // PHẢI chạy TRƯỚC MỌI THỨ khác, kể cả InitializeComponent(). Velopack gọi lại chính exe
        // này với các tham số dòng lệnh đặc biệt trong lúc cài đặt/gỡ/cập nhật (vd. tạo shortcut
        // Start Menu, dọn file cũ) — nếu code này chạy sau khi UI đã khởi tạo, các bước đó sẽ
        // không chạy đúng lúc hoặc app sẽ hiện cửa sổ không cần thiết trong các bước đó.
        VelopackApp.Build().Run();

        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Kiểm tra bản mới trên GitHub Releases, tải và áp dụng nếu có, rồi tự khởi động lại app.
    /// Không báo lỗi ra UI nếu thất bại (không mạng, GitHub tạm lỗi...) — việc kiểm tra update
    /// không bao giờ được làm hỏng trải nghiệm mở app bình thường.
    /// </summary>
    private static async Task CheckForUpdatesAsync()
    {
        try
        {
            // prerelease: true — bản hiện tại đang gắn nhãn "beta", nên phải nhận cả các bản
            // GitHub Release được đánh dấu "pre-release" thì mới thấy được bản beta mới hơn.
            var manager = new UpdateManager(new GithubSource(UpdateRepoUrl, accessToken: null, prerelease: true));

            // Chạy từ build Debug/chưa cài qua trình cài đặt Velopack (vd. F5 trong Visual Studio,
            // hoặc chạy thẳng .exe từ thư mục bin) — IsInstalled sẽ là false, bỏ qua kiểm tra update
            // vì UpdateManager cần biết thư mục cài đặt thật để áp dụng bản vá.
            if (!manager.IsInstalled)
            {
                return;
            }

            UpdateInfo? newVersion = await manager.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                return; // đã là bản mới nhất
            }

            await manager.DownloadUpdatesAsync(newVersion);
            manager.ApplyUpdatesAndRestart(newVersion);
        }
        catch
        {
            // Cố ý nuốt mọi lỗi ở đây — xem tóm tắt ở trên.
        }
    }
}
