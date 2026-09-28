using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Room reverb for the mic branch, using the classic "Freeverb" network topology (8 parallel
/// lowpass-feedback comb filters summed together, then run in series through 4 allpass filters) —
/// a public-domain algorithm design (originally by Jezar at Dreampoint) reimplemented here from its
/// well-published structure and tuning constants, not copied from any specific licensed source.
///
/// One full comb+allpass filter bank per channel (state is not shareable across channels — same
/// reasoning as <see cref="ThreeBandEqSampleProvider"/>). Delay-line lengths are specified in the
/// original design as sample counts at 44100Hz; they are rescaled proportionally to whatever
/// <see cref="ISampleProvider.WaveFormat"/> sample rate is actually in use so the reverb's character
/// stays consistent across capture devices with different native rates (e.g. 44100 vs 48000).
/// </summary>
public sealed class ReverbSampleProvider : ISampleProvider
{
    private const float FixedInputGain = 0.015f;
    private const float ScaleRoom = 0.28f;
    private const float OffsetRoom = 0.7f;
    private const float ScaleDamp = 0.4f;
    private const float AllpassFeedback = 0.5f;

    private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTuning = [556, 441, 341, 225];

    private sealed class CombFilter(int delaySamples)
    {
        private readonly float[] _buffer = new float[Math.Max(1, delaySamples)];
        private int _position;
        private float _filterStore;

        public float Feedback;
        public float Damp1;
        public float Damp2 = 1f;

        public float Process(float input)
        {
            float output = _buffer[_position];
            _filterStore = (output * Damp2) + (_filterStore * Damp1);
            _buffer[_position] = input + (_filterStore * Feedback);

            _position++;
            if (_position >= _buffer.Length)
            {
                _position = 0;
            }

            return output;
        }
    }

    private sealed class AllpassFilter(int delaySamples)
    {
        private readonly float[] _buffer = new float[Math.Max(1, delaySamples)];
        private int _position;

        public float Process(float input)
        {
            float bufferedValue = _buffer[_position];
            float output = -input + bufferedValue;
            _buffer[_position] = input + (bufferedValue * AllpassFeedback);

            _position++;
            if (_position >= _buffer.Length)
            {
                _position = 0;
            }

            return output;
        }
    }

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly CombFilter[][] _combs; // [channel][combIndex]
    private readonly AllpassFilter[][] _allpasses; // [channel][allpassIndex]
    private readonly object _paramLock = new();

    private volatile bool _enabled;
    private volatile float _roomSize = 0.5f;
    private volatile float _damping = 0.5f;
    private volatile float _wetDryMix = 0.3f;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>When false, Read() passes audio through untouched — same instant-bypass semantics
    /// as <see cref="EchoSampleProvider.Enabled"/>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>0..1 — larger values mean a longer decay tail (bigger virtual room).</summary>
    public float RoomSize
    {
        get => _roomSize;
        set
        {
            _roomSize = Math.Clamp(value, 0f, 1f);
            ApplyCombParameters();
        }
    }

    /// <summary>0..1 — higher damping absorbs high frequencies faster, giving a darker, shorter tail.</summary>
    public float Damping
    {
        get => _damping;
        set
        {
            _damping = Math.Clamp(value, 0f, 1f);
            ApplyCombParameters();
        }
    }

    /// <summary>0 = fully dry, 1 = fully wet.</summary>
    public float WetDryMix
    {
        get => _wetDryMix;
        set => _wetDryMix = Math.Clamp(value, 0f, 1f);
    }

    public ReverbSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);

        double rateScale = source.WaveFormat.SampleRate / 44100.0;

        _combs = new CombFilter[_channels][];
        _allpasses = new AllpassFilter[_channels][];

        for (int ch = 0; ch < _channels; ch++)
        {
            _combs[ch] = new CombFilter[CombTuning.Length];
            for (int i = 0; i < CombTuning.Length; i++)
            {
                _combs[ch][i] = new CombFilter((int)Math.Round(CombTuning[i] * rateScale));
            }

            _allpasses[ch] = new AllpassFilter[AllpassTuning.Length];
            for (int i = 0; i < AllpassTuning.Length; i++)
            {
                _allpasses[ch][i] = new AllpassFilter((int)Math.Round(AllpassTuning[i] * rateScale));
            }
        }

        ApplyCombParameters();
    }

    private void ApplyCombParameters()
    {
        float feedback = (_roomSize * ScaleRoom) + OffsetRoom;
        float damp1 = _damping * ScaleDamp;
        float damp2 = 1f - damp1;

        // Guards every comb filter's 3 fields being set as one consistent group — a UI slider can
        // call RoomSize/Damping from a different thread than Read() is running on.
        lock (_paramLock)
        {
            foreach (var channelCombs in _combs)
            {
                foreach (var comb in channelCombs)
                {
                    comb.Feedback = feedback;
                    comb.Damp1 = damp1;
                    comb.Damp2 = damp2;
                }
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        if (!_enabled || samplesRead <= 0)
        {
            return samplesRead;
        }

        float mix = _wetDryMix;

        lock (_paramLock)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                int channel = i % _channels;
                float dry = buffer[offset + i];
                float combInput = dry * FixedInputGain;

                float wet = 0f;
                var channelCombs = _combs[channel];
                for (int c = 0; c < channelCombs.Length; c++)
                {
                    wet += channelCombs[c].Process(combInput);
                }

                var channelAllpasses = _allpasses[channel];
                for (int a = 0; a < channelAllpasses.Length; a++)
                {
                    wet = channelAllpasses[a].Process(wet);
                }

                buffer[offset + i] = (dry * (1f - mix)) + (wet * mix);
            }
        }

        return samplesRead;
    }
}
