using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KaraokeMixer.Core.Audio;

/// <summary>
/// Wraps a WASAPI microphone capture device (a normal input device — the system default recording
/// device, or one explicitly picked from <c>MMDeviceEnumerator</c>) in a <see cref="BufferedWaveProvider"/>
/// and exposes it as an <see cref="ISampleProvider"/> for the DSP graph. Unlike
/// <see cref="ProcessLoopbackCapture"/> (which captures WebView2's process-loopback audio via raw
/// P/Invoke into Mmdevapi.dll — no such API is exposed by NAudio), a normal input device needs none
/// of that COM interop; NAudio's own <see cref="WasapiCapture"/> is enough.
///
/// Backlog trim: the mic capture device and the render (output) device run on independent hardware
/// clocks. Over a long singing session, small clock drift between them accumulates and the queued
/// audio in the <see cref="BufferedWaveProvider"/> slowly grows — heard as creeping mic latency
/// (you hear your own voice later and later relative to your mouth). <see cref="MaxBufferedMilliseconds"/>
/// bounds this: every <see cref="Read"/> call checks <c>BufferedDuration</c> and, if it exceeds the
/// bound, discards the OLDEST queued bytes before returning real samples — the same "trim backlog"
/// spirit as ProcessLoopbackSpike's BufferedWaveProvider usage there, generalized into an explicit,
/// proactive policy (that spike relied only on <c>DiscardOnBufferOverflow</c>, which protects
/// against the buffer filling completely but does not bound steady-state latency).
///
/// Threading note: the trim runs inside <see cref="Read"/>, which is only ever called from the
/// single consumer thread that pulls the DSP graph (in this app, the WasapiOut callback thread) —
/// the same thread that would otherwise just call <c>BufferedWaveProvider.Read()</c> directly. The
/// capture callback thread only ever calls <c>AddSamples</c> (a write). This keeps the buffer to a
/// plain single-producer/single-consumer usage — the same concurrent-thread pattern
/// BufferedWaveProvider is already relied on for elsewhere in this codebase (see
/// ProcessLoopbackCapture, where the capture thread writes and the WasapiOut thread reads) — rather
/// than adding a second, independent reader thread whose interaction with NAudio's internal circular
/// buffer locking has not been independently verified here. [Unverified]: NAudio's internal
/// BufferedWaveProvider/circular-buffer locking behavior was not inspected from source in this pass
/// (only the compiled package is available); keeping strictly to one reader thread sidesteps needing
/// that guarantee at all.
/// </summary>
public sealed class CaptureSource : ISampleProvider, IDisposable
{
    private const int DefaultMaxBufferedMilliseconds = 40;
    private const int MinMaxBufferedMilliseconds = 10;
    private const int MaxMaxBufferedMilliseconds = 500;

    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _sampleProvider;
    private byte[] _discardScratch = [];
    private bool _disposed;

    public WaveFormat WaveFormat => _sampleProvider.WaveFormat;

    public bool IsCapturing { get; private set; }

    /// <summary>Max steady-state buffered latency (milliseconds) before the oldest queued samples
    /// get dropped. Clamped to a sane range so a caller can't set 0 (would thrash, discarding on
    /// almost every callback) or something absurdly high (defeats the point of trimming).</summary>
    public int MaxBufferedMilliseconds { get; }

    /// <param name="device">Capture device to open, or null for the system default recording
    /// device (there is intentionally no hardcoded device — see KaraokeMixer.App's device picker).</param>
    /// <param name="maxBufferedMilliseconds">See <see cref="MaxBufferedMilliseconds"/>.</param>
    public CaptureSource(MMDevice? device = null, int maxBufferedMilliseconds = DefaultMaxBufferedMilliseconds)
    {
        _capture = device is null ? new WasapiCapture() : new WasapiCapture(device);
        MaxBufferedMilliseconds = Math.Clamp(maxBufferedMilliseconds, MinMaxBufferedMilliseconds, MaxMaxBufferedMilliseconds);

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        _capture.DataAvailable += OnDataAvailable;
        _sampleProvider = _buffer.ToSampleProvider();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        TrimBacklogIfNeeded();
        return _sampleProvider.Read(buffer, offset, count);
    }

    private void TrimBacklogIfNeeded()
    {
        double bufferedMs = _buffer.BufferedDuration.TotalMilliseconds;
        if (bufferedMs <= MaxBufferedMilliseconds)
        {
            return;
        }

        double excessMs = bufferedMs - MaxBufferedMilliseconds;
        int bytesPerMs = _buffer.WaveFormat.AverageBytesPerSecond / 1000;
        int bytesToDiscard = (int)(excessMs * bytesPerMs);

        // Round down to a whole number of frames so we never leave a partial, misaligned frame at
        // the front of the buffer (which would otherwise shift channel interleaving by 1+ bytes).
        bytesToDiscard -= bytesToDiscard % _buffer.WaveFormat.BlockAlign;
        if (bytesToDiscard <= 0)
        {
            return;
        }

        if (_discardScratch.Length < bytesToDiscard)
        {
            _discardScratch = new byte[bytesToDiscard];
        }

        _buffer.Read(_discardScratch, 0, bytesToDiscard);
    }

    public void Start()
    {
        if (IsCapturing)
        {
            return;
        }

        _capture.StartRecording();
        IsCapturing = true;
    }

    public void Stop()
    {
        if (!IsCapturing)
        {
            return;
        }

        try
        {
            _capture.StopRecording();
        }
        finally
        {
            IsCapturing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _disposed = true;
    }
}
