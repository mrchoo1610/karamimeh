namespace KaraokeMixer.Core.Audio;

/// <summary>
/// Cài đặt callback COM mà mmdevapi.dll gọi lại (từ 1 worker thread trong MTA) khi
/// ActivateAudioInterfaceAsync hoàn tất. Bắt buộc implement thêm IAgileObject (interface rỗng)
/// — nếu thiếu, docs Microsoft ghi rõ "the implementation must be agile", và có báo cáo cộng
/// đồng cho thấy thiếu nó sẽ khiến activation thất bại (xem ghi chú nguồn trong NativeInterop.cs).
///
/// Dùng TaskCompletionSource để chuyển callback bất đồng bộ kiểu COM thành Task chờ được bằng
/// async/await ở phía gọi — tương đương pattern ManualResetEvent trong bản C++ gốc của Microsoft,
/// nhưng idiomatic hơn cho .NET.
/// </summary>
internal sealed class ActivateAudioInterfaceCompletionHandler
    : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly TaskCompletionSource<(int HResult, IntPtr ActivatedInterfacePtr)> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(int HResult, IntPtr ActivatedInterfacePtr)> Task => _tcs.Task;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out int hresult, out IntPtr activatedInterfacePtr);
            _tcs.TrySetResult((hresult, hresult == NativeInterop.S_OK ? activatedInterfacePtr : IntPtr.Zero));
        }
        catch (Exception ex)
        {
            // GetActivateResult tự nó ném exception (hiếm, nhưng có thể xảy ra nếu marshal lỗi) —
            // không được để callback COM này ném exception xuyên qua biên native, nên bắt lại và
            // truyền qua Task để phía gọi xử lý như một lỗi kích hoạt bình thường.
            _tcs.TrySetException(ex);
        }
    }
}
