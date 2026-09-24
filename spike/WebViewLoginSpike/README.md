# WebViewLoginSpike

Spike WinUI 3 (không đóng gói / unpackaged) để kiểm tra khả năng đăng nhập
Google/YouTube (YouTube Premium) bên trong `WebView2` nhúng, xem phiên đăng
nhập có được giữ qua các lần khởi động lại ứng dụng hay không, và đăng xuất.

Đây **chỉ là spike thử nghiệm** — không có xử lý audio/karaoke, không có
logic sản phẩm thật.

## Yêu cầu môi trường

- Windows 10/11 x64.
- .NET SDK 10.0 (đã build/test với `10.0.302`).
- Microsoft Edge WebView2 Runtime đã cài trên máy (thường có sẵn trên Windows
  10/11 mới). Nếu chưa có, ứng dụng sẽ hiện cảnh báo
  "Cần cài Microsoft Edge WebView2 Runtime" và dừng lại thay vì crash.

## Build

Từ thư mục này (`spike\WebViewLoginSpike`):

```powershell
dotnet build -c Debug -p:Platform=x64
```

Build thành công sẽ tạo file thực thi tại:

```
bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\WebViewLoginSpike.exe
```

## Chạy

Chạy trực tiếp file `.exe` ở trên (double-click hoặc từ PowerShell):

```powershell
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\WebViewLoginSpike.exe
```

Ứng dụng mở một cửa sổ ~1280x800 với thanh công cụ (Quay lại / Tiến / Tải lại
/ Trang chủ / Đăng nhập), khung `WebView2` hiển thị YouTube, và thanh trạng
thái ở dưới cùng hiển thị: phiên bản WebView2 Runtime, PID tiến trình trình
duyệt, đường dẫn thư mục dữ liệu người dùng, URL hiện tại, và trạng thái đăng
nhập.

Dữ liệu phiên (cookie, local storage, v.v.) của `WebView2` được lưu tại:

```
%LOCALAPPDATA%\KaraokeMixer\WebView2
```

## Lưu ý quan trọng về bảo mật

- Ứng dụng **không bao giờ xử lý mật khẩu của bạn**. Khi bấm "Đăng nhập", ứng
  dụng chỉ điều hướng `WebView2` tới trang đăng nhập chính thức của Google
  (`accounts.google.com`) — bạn nhập tài khoản/mật khẩu trực tiếp trên trang
  đó, giống như trong trình duyệt thật.
- Ứng dụng không đọc, không xuất, không log cookie hay bất kỳ dữ liệu phiên
  đăng nhập nào.
- Ứng dụng không chèn script vào trang `accounts.google.com` (chỉ chèn một
  script nhỏ để phát hiện trạng thái đăng nhập trên `www.youtube.com`, phục
  vụ hiển thị trạng thái trong UI).
- Điều hướng bị giới hạn theo danh sách cho phép (`www.youtube.com`,
  `accounts.google.com`, v.v). Các URL ngoài danh sách sẽ tự mở bằng trình
  duyệt mặc định của Windows thay vì trong `WebView2`.

## ⚠️ Không tự đăng nhập thay bạn

AI/agent tạo ra spike này **không được** tự nhập tài khoản/mật khẩu Google
vào ứng dụng. Việc đăng nhập, kiểm tra YouTube Premium, thử phát video, đăng
xuất, v.v. cần **bạn** tự thực hiện thủ công theo checklist bên dưới.

## Checklist kiểm thử thủ công

Vui lòng tự thực hiện các bước sau và ghi lại kết quả:

- **S1 — Đăng nhập trong app**: Bấm "Đăng nhập" → tự nhập tài khoản/mật khẩu
  Google trên trang đăng nhập hiện ra → sau khi đăng nhập xong, vào lại
  YouTube. Kiểm tra: avatar tài khoản có hiện ở góc trên không? Phát thử một
  video — có quảng cáo hay không (nếu tài khoản có YouTube Premium, kỳ vọng
  là không có quảng cáo)?
- **S2 — Giữ phiên qua khởi động lại**: Đóng hẳn ứng dụng (đóng cửa sổ), mở
  lại `WebViewLoginSpike.exe`. Kiểm tra: vào YouTube có còn hiển thị đã đăng
  nhập không (không phải đăng nhập lại)?
- **S3 — Đăng xuất**: Bấm vào nút "👤 Đã đăng nhập" → "Đăng xuất". Kiểm tra:
  phiên đăng nhập có mất không (avatar biến mất, YouTube quay về trạng thái
  chưa đăng nhập)?
- **S4 — Nếu S1 bị Google chặn**: Nếu thấy cảnh báo "Google đang chặn đăng
  nhập trong trình duyệt nhúng (WebView2)" hoặc trang báo lỗi, hãy ghi lại
  URL và tiêu đề trang đang hiển thị ở thanh trạng thái dưới cùng (để phân
  tích sau — đây là hành vi đã biết của Google, gọi là "chặn embedded
  browser").
- **S5 — Tài khoản có 2FA / passkey**: Nếu tài khoản Google của bạn có bật
  xác thực 2 lớp (2FA) hoặc passkey, hãy thử đăng nhập và ghi lại kết quả
  (thành công / thất bại / bị chặn / yêu cầu thêm bước gì).

Ghi chú lại kết quả từng mục S1–S5 (kèm ảnh chụp màn hình nếu tiện) để dùng
làm căn cứ quyết định có dùng được `WebView2` cho tính năng đăng nhập
YouTube Premium trong sản phẩm chính hay không.
