using System.Reflection;
using NAudio.Dsp;
using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// 3-band EQ (low shelf / mid peaking / high shelf) built directly on
/// <see cref="NAudio.Dsp.BiQuadFilter"/> — the filter math itself is NAudio's own (already tested
/// upstream), this class only wires up the 3-band chain and the live-gain-change plumbing.
///
/// One filter chain (low shelf + peaking + high shelf = 3 BiQuadFilter instances) per audio
/// channel, because a BiQuadFilter carries per-instance state (its previous input/output samples)
/// — running left and right channel samples through a single shared instance would let one
/// channel's history bleed into the other's next sample.
///
/// <para>
/// [Deviation from the original plan, found and worked around during implementation]: the plan
/// called for changing gains live via <c>SetLowShelf</c>/<c>SetPeakingEq</c>/<c>SetHighShelf</c>
/// instance methods. Checked by reflecting over <c>NAudio.Dsp.BiQuadFilter</c> in the actual
/// installed package (NAudio 2.2.1): <c>SetPeakingEq(sampleRate, centreFrequency, q, dbGain)</c> IS
/// public and is used directly below for the mid band. <c>SetLowShelf</c>/<c>SetHighShelf</c> do
/// NOT exist as public instance methods in this NAudio version — only the public static factories
/// <c>BiQuadFilter.LowShelf(...)</c>/<c>HighShelf(...)</c> (which construct a brand-new instance,
/// resetting its internal state) and a <c>private</c> <c>SetCoefficients(...)</c>. Constructing a
/// brand-new filter on every gain change would reset its state (x1/x2/y1/y2) to zero and produce
/// an audible click each time a low/high gain slider moves — unacceptable for a live mixer control.
/// </para>
/// <para>
/// Workaround: for the low/high shelf bands, a short-lived <c>BiQuadFilter</c> is constructed via
/// NAudio's own public static factory purely to obtain NAudio's own correctly-computed coefficients
/// for the new gain, then just those coefficient fields are copied onto the long-lived per-channel
/// filter instance via reflection (see <see cref="CopyCoefficients"/>) — its state fields are never
/// touched, so there is no click. This still relies entirely on NAudio's own filter-coefficient
/// math (never reimplemented here), only using reflection to move already-correct numbers between
/// two instances of the same NAudio type. The mid (peaking) band needs none of this, since its
/// public <c>SetPeakingEq</c> instance method already updates coefficients in place.
/// </para>
/// </summary>
public sealed class ThreeBandEqSampleProvider : ISampleProvider
{
    public const float MinGainDb = -12f;
    public const float MaxGainDb = 12f;

    private const float LowShelfFrequencyHz = 200f;
    private const float PeakingFrequencyHz = 1000f;
    private const float HighShelfFrequencyHz = 4000f;
    private const float ShelfSlope = 1.0f;
    private const float PeakingQ = 0.8f;

    // BiQuadFilter's coefficient fields (a0..a4) are private — see class remarks. Reflected once
    // and cached rather than looked up on every gain change.
    private static readonly FieldInfo[] CoefficientFields =
    [
        typeof(BiQuadFilter).GetField("a0", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(BiQuadFilter), "a0"),
        typeof(BiQuadFilter).GetField("a1", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(BiQuadFilter), "a1"),
        typeof(BiQuadFilter).GetField("a2", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(BiQuadFilter), "a2"),
        typeof(BiQuadFilter).GetField("a3", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(BiQuadFilter), "a3"),
        typeof(BiQuadFilter).GetField("a4", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(BiQuadFilter), "a4"),
    ];

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;
    private readonly BiQuadFilter[] _lowShelf;
    private readonly BiQuadFilter[] _peaking;
    private readonly BiQuadFilter[] _highShelf;

    private readonly object _coefficientLock = new();
    private volatile float _lowGainDb;
    private volatile float _midGainDb;
    private volatile float _highGainDb;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float LowGainDb
    {
        get => _lowGainDb;
        set => SetLowGain(value);
    }

    public float MidGainDb
    {
        get => _midGainDb;
        set => SetMidGain(value);
    }

    public float HighGainDb
    {
        get => _highGainDb;
        set => SetHighGain(value);
    }

    public ThreeBandEqSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;

        _lowShelf = new BiQuadFilter[_channels];
        _peaking = new BiQuadFilter[_channels];
        _highShelf = new BiQuadFilter[_channels];

        for (int ch = 0; ch < _channels; ch++)
        {
            _lowShelf[ch] = BiQuadFilter.LowShelf(_sampleRate, LowShelfFrequencyHz, ShelfSlope, 0f);
            _peaking[ch] = BiQuadFilter.PeakingEQ(_sampleRate, PeakingFrequencyHz, PeakingQ, 0f);
            _highShelf[ch] = BiQuadFilter.HighShelf(_sampleRate, HighShelfFrequencyHz, ShelfSlope, 0f);
        }
    }

    /// <summary>Copies only the 5 private coefficient fields from <paramref name="source"/> (a
    /// short-lived filter built purely to compute them) onto <paramref name="destination"/> (the
    /// long-lived, per-channel filter actually used in Read()) — destination's x1/x2/y1/y2 state
    /// fields are untouched, so switching coefficients mid-stream does not click.</summary>
    private static void CopyCoefficients(BiQuadFilter source, BiQuadFilter destination)
    {
        foreach (var field in CoefficientFields)
        {
            field.SetValue(destination, field.GetValue(source));
        }
    }

    private void SetLowGain(float dbGain)
    {
        dbGain = Math.Clamp(dbGain, MinGainDb, MaxGainDb);
        _lowGainDb = dbGain;

        // See class remarks: SetLowShelf doesn't exist publicly in this NAudio version, so a
        // throwaway filter is built solely to get NAudio's own coefficients for the new gain.
        lock (_coefficientLock)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                var recomputed = BiQuadFilter.LowShelf(_sampleRate, LowShelfFrequencyHz, ShelfSlope, dbGain);
                CopyCoefficients(recomputed, _lowShelf[ch]);
            }
        }
    }

    private void SetMidGain(float dbGain)
    {
        dbGain = Math.Clamp(dbGain, MinGainDb, MaxGainDb);
        _midGainDb = dbGain;

        // SetPeakingEq IS public on this NAudio version — updates coefficients in place, no
        // reflection needed.
        lock (_coefficientLock)
        {
            foreach (var filter in _peaking)
            {
                filter.SetPeakingEq(_sampleRate, PeakingFrequencyHz, PeakingQ, dbGain);
            }
        }
    }

    private void SetHighGain(float dbGain)
    {
        dbGain = Math.Clamp(dbGain, MinGainDb, MaxGainDb);
        _highGainDb = dbGain;

        lock (_coefficientLock)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                var recomputed = BiQuadFilter.HighShelf(_sampleRate, HighShelfFrequencyHz, ShelfSlope, dbGain);
                CopyCoefficients(recomputed, _highShelf[ch]);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        // Coefficient recalculation touches 5 fields that Read() reads individually inside
        // Transform() — this lock keeps the audio thread from ever observing a torn mix of
        // old/new coefficients while a gain slider is being dragged. The critical section on the
        // writer side (SetLowGain/SetMidGain/SetHighGain) is only a handful of field copies/float
        // multiplies at UI rate, so contention here is negligible.
        lock (_coefficientLock)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                int channel = i % _channels;
                float sample = buffer[offset + i];
                sample = _lowShelf[channel].Transform(sample);
                sample = _peaking[channel].Transform(sample);
                sample = _highShelf[channel].Transform(sample);
                buffer[offset + i] = sample;
            }
        }

        return samplesRead;
    }
}
