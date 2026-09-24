using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Master soft-clip limiter: passes the signal through unchanged below <see cref="Threshold"/>,
/// and applies a tanh-based soft knee above it, so that mic-volume + music-volume summing in the
/// mixer can't exceed the float range and produce harsh hard-clipping. Intended as the very last
/// DSP node before <c>WasapiOut</c>.
///
/// Sign is preserved (the shaping curve is applied to the magnitude, then the original sign is
/// re-applied) so the waveform doesn't get rectified. As <c>magnitude</c> grows without bound,
/// <c>tanh</c> approaches (but mathematically never reaches) 1 — in practice, at extreme input
/// magnitudes the result rounds to exactly 1.0f once cast to <c>float</c>, so the guarantee this
/// class actually provides is output magnitude never EXCEEDS 1.0 (not a strict "always less
/// than"). No allocation in Read().
/// </summary>
public sealed class SoftClipSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _threshold = 0.8f;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Magnitude below which the signal passes through unmodified. Clamped away from the
    /// extremes: 0 would soft-clip everything (including silence-adjacent noise), 1 would leave no
    /// headroom for the knee to do anything before hitting the float ceiling.</summary>
    public float Threshold
    {
        get => _threshold;
        set => _threshold = Math.Clamp(value, 0.1f, 0.99f);
    }

    public SoftClipSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        float threshold = _threshold;
        float headroom = 1f - threshold;

        for (int i = 0; i < samplesRead; i++)
        {
            float sample = buffer[offset + i];
            float magnitude = Math.Abs(sample);

            if (magnitude > threshold)
            {
                float sign = sample < 0f ? -1f : 1f;
                float over = (magnitude - threshold) / headroom;
                float shaped = threshold + headroom * (float)Math.Tanh(over);
                buffer[offset + i] = sign * shaped;
            }
        }

        return samplesRead;
    }
}
