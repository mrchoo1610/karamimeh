# karamimeh

Karaoke mixer chạy trên Windows: hát karaoke với nhạc lấy trực tiếp từ YouTube (mở ngay trong app,
đăng nhập được YouTube Premium), trộn với mic qua EQ 3 dải + Echo, xuất ra loa/tai nghe bất kỳ.

Sub-app trong hệ sinh thái **MimeHub** (karaoke + mimehub = karamimeh).

> **Đang trong giai đoạn beta.** Đã hoạt động: đăng nhập YouTube, mic qua EQ 3 dải + Echo, chọn thiết
> bị mic/output. Nhạc YouTube **phát trực tiếp, không qua xử lý** — xem mục "Vì sao không capture lại
> tiếng YouTube" bên dưới. Chưa có: chống hú, Reverb, giao diện chính thức theo bản thiết kế UX đầy đủ.

## Vì sao không capture lại tiếng YouTube

Bản đầu có thử bắt riêng tiếng YouTube (qua kỹ thuật "process-loopback capture" của Windows) để trộn
cùng mic bằng phần mềm. Sau nhiều vòng điều tra lỗi (xem lịch sử commit và các comment lớn trong
`ProcessLoopbackCapture.cs`/`AudioMixerCore.cs`), phát hiện: **bất kỳ cách nào làm im lặng session
gốc của YouTube (Mute=true hay Volume=0) đều khiến việc capture nhận đúng 0 gói tin** — nhiều khả
năng chính Chromium tự ngừng render khi thấy session của nó bị im lặng.

Vì thiết kế DSP gốc vốn chỉ áp EQ/Echo cho mic (nhạc chỉ có volume), giải pháp đơn giản hơn hẳn: để
YouTube phát bình thường, không đụng vào; app chỉ phát mic (đã qua EQ/Echo) ra **cùng thiết bị**;
Windows tự trộn 2 luồng (đúng bản chất "Shared mode" của Windows Audio) — không cần capture, không
lỗi COM/GC, độ trễ nhạc = 0. "Âm lượng nhạc" trong UI giờ chỉnh thẳng vào Volume Mixer của Windows
cho session YouTube. Code capture cũ (`Audio/ProcessLoopbackCapture.cs`) vẫn hoạt động đúng và được
giữ lại làm tham khảo, chỉ không dùng trong pipeline mặc định nữa.

## Cấu trúc

```
karamimeh/
├─ src/
│  ├─ KaraokeMixer.Core/   thư viện engine âm thanh (capture, DSP, mixer) — không phụ thuộc UI
│  └─ KaraokeMixer.App/    app WinUI 3 chạy được (karamimeh.exe)
├─ tests/
│  └─ KaraokeMixer.Core.Tests/   unit test cho engine (xUnit)
├─ spike/    các bản thử nghiệm kỹ thuật trong quá trình phát triển (giữ lại làm tài liệu tham khảo)
└─ windows_karaoke_mixer_architecture_specification.md   tài liệu thiết kế gốc
```

## Build & chạy (dev)

Cần .NET SDK 10+ trên Windows.

```powershell
dotnet build KaraokeMixer.sln -c Debug -p:Platform=x64
dotnet test tests/KaraokeMixer.Core.Tests/KaraokeMixer.Core.Tests.csproj -c Debug -p:Platform=x64
```

Chạy app (Debug):

```
src\KaraokeMixer.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\karamimeh.exe
```

## Cài đặt (người dùng)

Vào [Releases](../../releases), tải bản cài đặt mới nhất (`karamimehSetup.exe`), chạy để cài. App
tự kiểm tra và cài bản mới mỗi lần mở (qua GitHub Releases), không cần tải lại thủ công.

## Đóng gói & phát hành (cho người duy trì dự án)

Phát hành tự động qua GitHub Actions (`.github/workflows/release.yml`) — chỉ cần đẩy 1 git tag:

```bash
git tag v0.1.0-beta1
git push origin v0.1.0-beta1
```

Action sẽ tự: build + chạy test → publish → đóng gói bằng [Velopack](https://velopack.io) →
đăng lên GitHub Releases (tag có `-` trong tên, ví dụ `-beta1`, tự động được đánh dấu pre-release).

App (qua `Velopack.UpdateManager` trong `App.xaml.cs`) tự kiểm tra bản mới mỗi lần mở, tải và cài
lại nếu có — không cần làm gì thêm phía người dùng.

**Đóng gói thử ở máy local (không đăng lên GitHub)**, để kiểm tra trước khi tag:

```powershell
dotnet publish src/KaraokeMixer.App/KaraokeMixer.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o publish
vpk pack --packId karamimeh --packVersion 0.1.0-beta1 --packDir publish --mainExe karamimeh.exe --icon karamimeh.ico --packTitle karamimeh -o Releases
```

(Cần cài CLI: `dotnet tool install -g vpk`.)
