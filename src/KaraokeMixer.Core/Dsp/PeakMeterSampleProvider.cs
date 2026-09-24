using System.Threading;
using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Non-intrusive peak-level tap: passes samples through completely unchanged, tracks the running
/// peak magnitude, and surfaces it in two UI-polling-friendly ways — an event throttled to roughly
/// every 100ms (same interval as <c>ProcessLoopbackCapture.PeakLevelUpdated</c> in the
/// ProcessLoopbackSpike this project ports from), and a plain <see cref="CurrentPeak"/> property a
/// UI can poll on a DispatcherTimer instead of subscribing. Keeping this as its own ISampleProvider
/// (rather than baking metering into AudioMixerCore) means the DSP graph itself has zero UI
/// dependency — only KaraokeMixer.App needs to know this event/property exists.
/// </summary>
public sealed class PeakMeterSampleProvider : ISampleProvider
{
    private const double MeterIntervalMs = 100;

    private readonly ISampleProvider _source;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private float _runningPeak;
    private float _lastPublishedPeak;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Raised roughly every 100ms with the peak magnitude seen since the previous event.
    /// Invoked synchronously from whichever thread is pulling the audio graph (the WasapiOut
    /// callback thread) — subscribers that touch UI must marshal to the UI thread themselves, same
    /// as the spike's existing peak-meter usage in MainWindow.xaml.cs.</summary>
    public event Action<float>? PeakUpdated;

    /// <summary>Latest published peak value (0..~1, can exceed 1 slightly pre-SoftClip). Safe to
    /// poll from any thread without subscribing to <see cref="PeakUpdated"/>.</summary>
    public float CurrentPeak => Volatile.Read(ref _lastPublishedPeak);

    public PeakMeterSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        float peak = _runningPeak;
        for (int i = 0; i < samplesRead; i++)
        {
            float magnitude = Math.Abs(buffer[offset + i]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }
        _runningPeak = peak;

        if (_stopwatch.Elapsed.TotalMilliseconds >= MeterIntervalMs)
        {
            Volatile.Write(ref _lastPublishedPeak, peak);
            PeakUpdated?.Invoke(peak);
            _runningPeak = 0f;
            _stopwatch.Restart();
        }

        return samplesRead;
    }
}
