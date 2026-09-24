using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Delay-with-feedback echo effect, implemented as a standard NAudio ISampleProvider wrapper
/// (Read(float[], offset, count), no per-block heap allocation).
///
/// This supersedes the draft EchoSampleProvider in
/// windows_karaoke_mixer_architecture_specification.md (section 4): that draft's
/// <c>DelayMilliseconds</c> setter had no clamp, while the delay ring buffer was sized once, at
/// construction time, for a fixed "2 sec max delay". Setting DelayMilliseconds above that fixed
/// capacity at runtime (e.g. from a UI slider) produced <c>delaySamples &gt;= _delayBuffer.Length</c>,
/// which fed a negative dividend into C#'s <c>%</c> operator inside Read() and threw
/// IndexOutOfRangeException — reachable simply by dragging a delay slider too far, not a corner
/// case. Here the ring buffer is sized once for <see cref="MaxDelayMilliseconds"/> plus a safety
/// margin, every parameter setter clamps into a range that always stays strictly inside that
/// capacity, and the index math avoids the modulo-of-negative pitfall entirely (a plain
/// subtract-then-wrap-if-negative, since delaySamples is guaranteed less than the buffer length).
///
/// Live parameter changes: DelayMilliseconds/Feedback/WetDryMix/Enabled are backed by
/// <c>volatile</c> fields so a UI thread can adjust them at any time while the audio thread is
/// inside Read(); Read() snapshots each parameter exactly once at the top of the call so the
/// entire block is processed with one consistent set of parameters (no locks in the hot path).
/// </summary>
public sealed class EchoSampleProvider : ISampleProvider
{
    /// <summary>Upper bound for <see cref="DelayMilliseconds"/>. The ring buffer is sized larger
    /// than this (see <see cref="SafetyMarginMilliseconds"/>) so a delay at this exact value can
    /// never collide with the write cursor.</summary>
    public const int MaxDelayMilliseconds = 2000;

    private const int MinDelayMilliseconds = 1;
    private const int SafetyMarginMilliseconds = 100;
    private const float MaxFeedback = 0.95f;

    private readonly ISampleProvider _source;
    private readonly float[] _delayBuffer;
    private readonly int _channels;
    private int _bufferPosition;

    private volatile bool _enabled = true;
    private volatile int _delayMilliseconds = 200;
    private volatile float _feedback = 0.35f;
    private volatile float _wetDryMix = 0.3f;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>When false, Read() passes audio through untouched (dry, no delay tail at all —
    /// not even the tail already in flight, since that matches user expectation of an instant
    /// on/off toggle for a mixer effect).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public int DelayMilliseconds
    {
        get => _delayMilliseconds;
        set => _delayMilliseconds = Math.Clamp(value, MinDelayMilliseconds, MaxDelayMilliseconds);
    }

    public float Feedback
    {
        get => _feedback;
        set => _feedback = Math.Clamp(value, 0f, MaxFeedback);
    }

    /// <summary>0 = fully dry, 1 = fully wet.</summary>
    public float WetDryMix
    {
        get => _wetDryMix;
        set => _wetDryMix = Math.Clamp(value, 0f, 1f);
    }

    public EchoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);

        int capacityMs = MaxDelayMilliseconds + SafetyMarginMilliseconds;
        int capacityFrames = (int)Math.Ceiling(source.WaveFormat.SampleRate * (capacityMs / 1000.0));
        _delayBuffer = new float[Math.Max(1, capacityFrames) * _channels];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        if (!_enabled || samplesRead <= 0)
        {
            return samplesRead;
        }

        // Snapshot once per Read() call — see class remarks on why (no locks in the hot path,
        // but still a single consistent parameter set for this whole block).
        int delayMs = _delayMilliseconds;
        float feedback = _feedback;
        float mix = _wetDryMix;

        int delayFrames = (int)(delayMs / 1000.0 * WaveFormat.SampleRate);
        int delaySamples = delayFrames * _channels;
        int bufferLength = _delayBuffer.Length;

        if (delaySamples <= 0 || delaySamples >= bufferLength)
        {
            // Should be unreachable given the clamps on DelayMilliseconds plus how _delayBuffer is
            // sized in the constructor — guarded anyway so a future change to either can never
            // resurrect the negative-modulo bug this class was written to eliminate.
            return samplesRead;
        }

        int position = _bufferPosition;

        for (int i = 0; i < samplesRead; i++)
        {
            int delayIndex = position - delaySamples;
            if (delayIndex < 0)
            {
                delayIndex += bufferLength;
            }

            float drySample = buffer[offset + i];
            float wetSample = _delayBuffer[delayIndex];

            buffer[offset + i] = (drySample * (1.0f - mix)) + (wetSample * mix);

            _delayBuffer[position] = drySample + (wetSample * feedback);
            position++;
            if (position >= bufferLength)
            {
                position = 0;
            }
        }

        _bufferPosition = position;
        return samplesRead;
    }
}
