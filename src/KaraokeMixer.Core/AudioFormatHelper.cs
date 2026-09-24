using NAudio.Wave;

namespace KaraokeMixer.Core;

/// <summary>
/// Normalizes an <see cref="ISampleProvider"/> to a target <see cref="WaveFormat"/> so multiple
/// sources (mic chain, YouTube capture) can be safely handed to a single
/// <c>NAudio.Wave.SampleProviders.MixingSampleProvider</c>. <c>MixingSampleProvider</c> throws
/// <c>ArgumentException</c> if an input's format doesn't exactly match its own — it does not
/// resample or remix channels silently — so every source must go through this helper first.
///
/// Resampling reuses the same pattern already validated in
/// ProcessLoopbackSpike/MainWindow.xaml.cs's <c>StartWasapiOutPlayback</c>: wrap with
/// <see cref="MediaFoundationResampler"/> when the format differs, targeting the output device's
/// own mix format (<c>MMDevice.AudioClient.MixFormat</c>) so <c>WasapiOut</c> in shared mode never
/// has to negotiate/reject an unfamiliar format.
/// </summary>
public static class AudioFormatHelper
{
    /// <summary>
    /// [Bug fix — found via a real repro, not anticipated in the original design] Many real output
    /// devices (confirmed with a Creative BT-W6 dongle) report their WASAPI Shared-mode
    /// <c>AudioClient.MixFormat</c> as <see cref="WaveFormatExtensible"/> (tag <c>WAVE_FORMAT_EXTENSIBLE</c>,
    /// 0xFFFE) rather than a plain PCM/IEEE-float <see cref="WaveFormat"/>. NAudio's
    /// <see cref="MediaFoundationResampler"/> throws <c>ArgumentException("Unsupported source encoding")</c>
    /// when asked to resample to (or from) a format whose <see cref="WaveFormat.Encoding"/> is
    /// <see cref="WaveFormatEncoding.Extensible"/> — it only recognizes plain
    /// <see cref="WaveFormatEncoding.Pcm"/>/<see cref="WaveFormatEncoding.IeeeFloat"/>. Call this on
    /// any format obtained from <c>AudioClient.MixFormat</c> before using it as a
    /// <see cref="NormalizeToFormat"/> target (or a <c>MixingSampleProvider</c>'s format).
    ///
    /// Heuristic used instead of reading <see cref="WaveFormatExtensible"/>'s SubFormat GUID
    /// directly: Windows' WASAPI Shared-mode audio engine mix format is virtually always 32-bit
    /// IEEE float regardless of device (a long-standing, widely-documented Windows behavior since
    /// Vista) — so bits-per-sample 32 is treated as IeeeFloat, anything else as Pcm. [Unverified]
    /// against every possible device/driver; if a future device's mix format turns out to genuinely
    /// be 32-bit integer PCM reported as Extensible, this heuristic would misclassify it — revisit
    /// with an explicit SubFormat GUID check if that's ever observed.
    /// </summary>
    public static WaveFormat ToResamplerSafeFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format.Encoding != WaveFormatEncoding.Extensible)
        {
            return format;
        }

        return format.BitsPerSample == 32
            ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
            : new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
    }

    /// <summary>
    /// Returns <paramref name="source"/> unchanged if its format already matches
    /// <paramref name="targetFormat"/> exactly (sample rate, channel count, encoding, bit depth);
    /// otherwise returns a resampled/reformatted wrapper that outputs exactly
    /// <paramref name="targetFormat"/>. <paramref name="targetFormat"/> should already have been
    /// passed through <see cref="ToResamplerSafeFormat"/> if it came from <c>AudioClient.MixFormat</c>.
    /// </summary>
    public static ISampleProvider NormalizeToFormat(ISampleProvider source, WaveFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetFormat);

        if (FormatsMatch(source.WaveFormat, targetFormat))
        {
            return source;
        }

        // MediaFoundationResampler operates on IWaveProvider, not ISampleProvider — round-trip
        // through IWaveProvider and back, same as the spike's StartWasapiOutPlayback.
        IWaveProvider waveProvider = source.ToWaveProvider();
        var resampler = new MediaFoundationResampler(waveProvider, targetFormat)
        {
            ResamplerQuality = 60,
        };

        return resampler.ToSampleProvider();
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate
            && a.Channels == b.Channels
            && a.BitsPerSample == b.BitsPerSample
            && a.Encoding == b.Encoding;
    }
}
