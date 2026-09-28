using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Automatic acoustic-feedback (howling) suppressor for the mic branch. Runs a bank of Goertzel
/// tone-energy detectors (one per candidate frequency, log-spaced across ~150Hz-8kHz) over
/// successive fixed-size analysis blocks of the incoming mic signal. When one candidate's energy
/// spikes far above that block's average across all candidates and stays elevated for several
/// consecutive blocks — the signature of a narrowband feedback tone building up, as opposed to
/// normal broadband vocal/instrument content — a narrow dynamic-EQ dip ("notch") is smoothly ramped
/// in at that exact frequency. The dip is released the same way once that frequency's energy has
/// settled back down for a while.
///
/// The dip is implemented as <see cref="BiQuadFilter.PeakingEQ"/> with a negative gain rather than a
/// hard <see cref="BiQuadFilter.NotchFilter"/>, specifically so its depth can be ramped 0→target→0
/// via the public <c>SetPeakingEq</c> instance method without ever resetting the filter's internal
/// state — the same click-avoidance concern already documented on
/// <see cref="ThreeBandEqSampleProvider"/>. A slot's centre frequency is only ever changed while its
/// gain is at (or ramping through) 0dB, so a frequency change is never itself audible as a
/// discontinuity.
///
/// This is a heuristic detector, not a certified anti-feedback product: the trigger/release ratios,
/// hold times and dip depth below are starting defaults, not values verified against real howling on
/// real hardware — expect to retune them once tested with an actual mic/speaker pair that can be
/// made to howl on demand. A sustained, unusually loud single vocal note could in principle also
/// trip it (false positive); <see cref="Sensitivity"/> exists to trade this off.
///
/// Up to <see cref="MaxSimultaneousNotches"/> independent notches run per channel at once, since a
/// real howl can occur at more than one frequency simultaneously.
/// </summary>
public sealed class FeedbackSuppressorSampleProvider : ISampleProvider
{
    public const int MaxSimultaneousNotches = 3;
    public const int CandidateFrequencyCount = 24;
    public const float MinDepthDb = 3f;
    public const float MaxDepthDb = 30f;

    private const float MinCandidateHz = 150f;
    private const float MaxCandidateHz = 8000f;
    private const int BlockSize = 1024;
    private const float NotchQ = 14f;
    private const int ConfirmBlocks = 3;
    private const int ReleaseHoldBlocks = 90;
    private const float ReleaseRatio = 1.6f;
    private const float AttackDbStepPerBlock = 6f;
    private const float ReleaseDbStepPerBlock = 3f;

    // Heuristic absolute floor (raw Goertzel sum-of-squares power) below which a candidate is never
    // allowed to trigger, even if its ratio to the block average looks high — guards against
    // near-silence where floating-point noise alone can produce a large but meaningless ratio.
    // [Unverified]: not calibrated against any real microphone's noise floor.
    private const float MinTriggerPower = 1e-6f;

    private enum SlotState { Idle, Attacking, Holding, Releasing }

    private sealed class NotchSlot(BiQuadFilter filter)
    {
        public SlotState State = SlotState.Idle;
        public int CandidateIndex = -1;
        public float CurrentDepthDb;
        public int BelowStreak;
        public readonly BiQuadFilter Filter = filter;
    }

    private struct GoertzelState
    {
        public float Coeff;
        public float Q1;
        public float Q2;
    }

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;
    private readonly float[] _candidateFrequencies;
    private readonly GoertzelState[][] _goertzel; // [channel][candidate]
    private readonly float[][] _blockPower; // [channel][candidate] — scratch, reused every block
    private readonly int[][] _triggerStreak; // [channel][candidate]
    private readonly NotchSlot[][] _slots; // [channel][slot]
    private readonly int[] _blockSampleCount; // [channel]

    private volatile bool _enabled;
    private volatile float _sensitivity = 0.5f;
    private volatile float _targetDepthDb = 15f;
    private int _publishedActiveNotchCount;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>When false, Read() passes audio through untouched and no detection runs at all —
    /// same instant-bypass semantics as <see cref="EchoSampleProvider.Enabled"/>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>0..1 — higher triggers a suppression notch more easily (maps to a lower
    /// energy-spike ratio required before engaging).</summary>
    public float Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>How much an engaged notch attenuates its target frequency, in dB.</summary>
    public float SuppressionDepthDb
    {
        get => _targetDepthDb;
        set => _targetDepthDb = Math.Clamp(value, MinDepthDb, MaxDepthDb);
    }

    /// <summary>Number of notches currently engaged (attacking/holding/releasing) on channel 0 —
    /// polling-friendly diagnostic for a UI, same pattern as
    /// <see cref="PeakMeterSampleProvider.CurrentPeak"/>.</summary>
    public int ActiveNotchCount => Volatile.Read(ref _publishedActiveNotchCount);

    public FeedbackSuppressorSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;

        _candidateFrequencies = BuildCandidateFrequencies(_sampleRate);

        _goertzel = new GoertzelState[_channels][];
        _blockPower = new float[_channels][];
        _triggerStreak = new int[_channels][];
        _slots = new NotchSlot[_channels][];
        _blockSampleCount = new int[_channels];

        for (int ch = 0; ch < _channels; ch++)
        {
            _goertzel[ch] = new GoertzelState[CandidateFrequencyCount];
            for (int k = 0; k < CandidateFrequencyCount; k++)
            {
                _goertzel[ch][k].Coeff = 2f * MathF.Cos(2f * MathF.PI * _candidateFrequencies[k] / _sampleRate);
            }

            _blockPower[ch] = new float[CandidateFrequencyCount];
            _triggerStreak[ch] = new int[CandidateFrequencyCount];

            _slots[ch] = new NotchSlot[MaxSimultaneousNotches];
            for (int s = 0; s < MaxSimultaneousNotches; s++)
            {
                _slots[ch][s] = new NotchSlot(BiQuadFilter.PeakingEQ(_sampleRate, _candidateFrequencies[0], NotchQ, 0f));
            }
        }
    }

    private static float[] BuildCandidateFrequencies(float sampleRate)
    {
        float maxHz = Math.Min(MaxCandidateHz, sampleRate * 0.45f);
        var freqs = new float[CandidateFrequencyCount];
        double logMin = Math.Log(MinCandidateHz);
        double logMax = Math.Log(Math.Max(MinCandidateHz + 1f, maxHz));

        for (int i = 0; i < CandidateFrequencyCount; i++)
        {
            double t = i / (double)(CandidateFrequencyCount - 1);
            freqs[i] = (float)Math.Exp(logMin + (t * (logMax - logMin)));
        }

        return freqs;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        if (!_enabled || samplesRead <= 0)
        {
            return samplesRead;
        }

        for (int i = 0; i < samplesRead; i++)
        {
            int channel = i % _channels;
            float sample = buffer[offset + i];

            var goertzel = _goertzel[channel];
            for (int k = 0; k < CandidateFrequencyCount; k++)
            {
                float q0 = (goertzel[k].Coeff * goertzel[k].Q1) - goertzel[k].Q2 + sample;
                goertzel[k].Q2 = goertzel[k].Q1;
                goertzel[k].Q1 = q0;
            }

            var slots = _slots[channel];
            for (int s = 0; s < slots.Length; s++)
            {
                if (slots[s].State != SlotState.Idle)
                {
                    sample = slots[s].Filter.Transform(sample);
                }
            }

            buffer[offset + i] = sample;

            _blockSampleCount[channel]++;
            if (_blockSampleCount[channel] >= BlockSize)
            {
                _blockSampleCount[channel] = 0;
                ProcessBlock(channel);
            }
        }

        return samplesRead;
    }

    private void ProcessBlock(int channel)
    {
        var goertzel = _goertzel[channel];
        var power = _blockPower[channel];

        float sum = 0f;
        for (int k = 0; k < CandidateFrequencyCount; k++)
        {
            float q1 = goertzel[k].Q1;
            float q2 = goertzel[k].Q2;
            float p = (q1 * q1) + (q2 * q2) - (q1 * q2 * goertzel[k].Coeff);
            power[k] = p;
            sum += p;

            goertzel[k].Q1 = 0f;
            goertzel[k].Q2 = 0f;
        }

        float meanPower = sum / CandidateFrequencyCount;
        float triggerRatio = float.Lerp(8f, 3f, _sensitivity);
        var slots = _slots[channel];
        var streaks = _triggerStreak[channel];
        float targetDepthDb = _targetDepthDb;

        // 1. Advance existing slots' attack/hold/release state machine.
        for (int s = 0; s < slots.Length; s++)
        {
            var slot = slots[s];
            switch (slot.State)
            {
                case SlotState.Attacking:
                    slot.CurrentDepthDb -= AttackDbStepPerBlock;
                    if (slot.CurrentDepthDb <= -targetDepthDb)
                    {
                        slot.CurrentDepthDb = -targetDepthDb;
                        slot.State = SlotState.Holding;
                        slot.BelowStreak = 0;
                    }
                    slot.Filter.SetPeakingEq(_sampleRate, _candidateFrequencies[slot.CandidateIndex], NotchQ, slot.CurrentDepthDb);
                    break;

                case SlotState.Holding:
                    float holdRatio = power[slot.CandidateIndex] / (meanPower + MinTriggerPower);
                    if (holdRatio < ReleaseRatio)
                    {
                        slot.BelowStreak++;
                        if (slot.BelowStreak >= ReleaseHoldBlocks)
                        {
                            slot.State = SlotState.Releasing;
                        }
                    }
                    else
                    {
                        slot.BelowStreak = 0;
                    }
                    break;

                case SlotState.Releasing:
                    slot.CurrentDepthDb += ReleaseDbStepPerBlock;
                    if (slot.CurrentDepthDb >= 0f)
                    {
                        slot.CurrentDepthDb = 0f;
                        slot.State = SlotState.Idle;
                        slot.CandidateIndex = -1;
                    }
                    else
                    {
                        slot.Filter.SetPeakingEq(_sampleRate, _candidateFrequencies[slot.CandidateIndex], NotchQ, slot.CurrentDepthDb);
                    }
                    break;
            }
        }

        // 2. Look for a new howl on any frequency not already claimed by a slot.
        for (int k = 0; k < CandidateFrequencyCount; k++)
        {
            if (IsCandidateAssigned(slots, k))
            {
                streaks[k] = 0;
                continue;
            }

            float ratio = power[k] / (meanPower + MinTriggerPower);
            streaks[k] = (ratio > triggerRatio && power[k] > MinTriggerPower) ? streaks[k] + 1 : 0;

            if (streaks[k] >= ConfirmBlocks)
            {
                var freeSlot = FindIdleSlot(slots);
                if (freeSlot is null)
                {
                    continue;
                }

                freeSlot.CandidateIndex = k;
                freeSlot.CurrentDepthDb = 0f;
                freeSlot.State = SlotState.Attacking;
                freeSlot.Filter.SetPeakingEq(_sampleRate, _candidateFrequencies[k], NotchQ, 0f);
                streaks[k] = 0;
            }
        }

        if (channel == 0)
        {
            int active = 0;
            foreach (var slot in slots)
            {
                if (slot.State != SlotState.Idle)
                {
                    active++;
                }
            }

            Volatile.Write(ref _publishedActiveNotchCount, active);
        }
    }

    private static bool IsCandidateAssigned(NotchSlot[] slots, int candidateIndex)
    {
        foreach (var slot in slots)
        {
            if (slot.State != SlotState.Idle && slot.CandidateIndex == candidateIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static NotchSlot? FindIdleSlot(NotchSlot[] slots)
    {
        foreach (var slot in slots)
        {
            if (slot.State == SlotState.Idle)
            {
                return slot;
            }
        }

        return null;
    }
}
