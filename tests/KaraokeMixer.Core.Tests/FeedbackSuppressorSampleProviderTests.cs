using KaraokeMixer.Core.Dsp;
using KaraokeMixer.Core.Tests.TestSupport;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

public class FeedbackSuppressorSampleProviderTests
{
    [Fact]
    public void Read_Disabled_PassesThroughUnchanged_AndNeverDetects()
    {
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var source = new ArraySampleProvider(input, 44100);
        var suppressor = new FeedbackSuppressorSampleProvider(source) { Enabled = false };

        var output = new float[input.Length];
        suppressor.Read(output, 0, output.Length);

        Assert.Equal(input, output);
        Assert.Equal(0, suppressor.ActiveNotchCount);
    }

    [Fact]
    public void Read_DoesNotThrow_AndPreservesBufferLengthAndFormat_WhenEnabled()
    {
        const int sampleRate = 44100;
        var signal = new SignalGenerator(sampleRate, 1) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.3 };
        var suppressor = new FeedbackSuppressorSampleProvider(signal) { Enabled = true };

        Assert.Equal(1, suppressor.WaveFormat.Channels);
        Assert.Equal(sampleRate, suppressor.WaveFormat.SampleRate);

        var buffer = new float[8192];
        int samplesRead = suppressor.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, samplesRead);
        foreach (float sample in buffer)
        {
            Assert.False(float.IsNaN(sample), "Suppressor output must never be NaN");
            Assert.False(float.IsInfinity(sample), "Suppressor output must never be infinite");
        }
    }

    [Fact]
    public void Sensitivity_And_SuppressionDepthDb_Setters_ClampToDocumentedRange()
    {
        var signal = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = 1000 };
        var suppressor = new FeedbackSuppressorSampleProvider(signal);

        suppressor.Sensitivity = -1f;
        Assert.Equal(0f, suppressor.Sensitivity);
        suppressor.Sensitivity = 5f;
        Assert.Equal(1f, suppressor.Sensitivity);

        suppressor.SuppressionDepthDb = -100f;
        Assert.Equal(FeedbackSuppressorSampleProvider.MinDepthDb, suppressor.SuppressionDepthDb);
        suppressor.SuppressionDepthDb = 1000f;
        Assert.Equal(FeedbackSuppressorSampleProvider.MaxDepthDb, suppressor.SuppressionDepthDb);
    }

    [Fact]
    public void Read_SustainedPureToneOnACandidateFrequency_EngagesAndHoldsANotch_ThatMeasurablyAttenuatesIt()
    {
        const int sampleRate = 44100;
        const int candidateIndex = 12; // arbitrary mid-range candidate, comfortably inside 150-8000Hz
        float targetFrequency = ComputeCandidateFrequency(candidateIndex, sampleRate);

        var signal = new SignalGenerator(sampleRate, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = targetFrequency,
            Gain = 0.5,
        };

        var suppressor = new FeedbackSuppressorSampleProvider(signal)
        {
            Enabled = true,
            Sensitivity = 1.0f,
            SuppressionDepthDb = 20f,
        };

        // Enough analysis blocks (BlockSize=1024) for the confirm streak, full attack ramp, and a
        // bit of hold time before measuring — a real howl builds up over a comparable timescale.
        var warmup = new float[1024 * 10];
        suppressor.Read(warmup, 0, warmup.Length);

        Assert.True(suppressor.ActiveNotchCount > 0, "Expected a feedback notch to engage on a sustained pure tone at a candidate frequency");

        var suppressed = new float[4096];
        suppressor.Read(suppressed, 0, suppressed.Length);

        var reference = new SignalGenerator(sampleRate, 1) { Type = SignalGeneratorType.Sin, Frequency = targetFrequency, Gain = 0.5 };
        var referenceBuffer = new float[4096];
        reference.Read(referenceBuffer, 0, referenceBuffer.Length);

        Assert.True(
            Rms(suppressed) < Rms(referenceBuffer) * 0.7,
            "An engaged, held notch should measurably reduce energy at the suppressed frequency");
    }

    /// <summary>Reimplements <c>FeedbackSuppressorSampleProvider.BuildCandidateFrequencies</c>'s log
    /// spacing exactly (same private min/max Hz constants) so a test can target an exact, on-bin
    /// frequency instead of relying on how much Goertzel spectral leakage an off-bin tone would
    /// produce — keeps the detection test deterministic. Must be kept in sync if that private
    /// method's constants ever change.</summary>
    private static float ComputeCandidateFrequency(int index, float sampleRate)
    {
        const float minHz = 150f;
        const float maxHzCap = 8000f;
        float maxHz = Math.Min(maxHzCap, sampleRate * 0.45f);
        double logMin = Math.Log(minHz);
        double logMax = Math.Log(Math.Max(minHz + 1f, maxHz));
        double t = index / (double)(FeedbackSuppressorSampleProvider.CandidateFrequencyCount - 1);
        return (float)Math.Exp(logMin + (t * (logMax - logMin)));
    }

    private static double Rms(float[] data)
    {
        double sumSquares = 0;
        foreach (float sample in data)
        {
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / data.Length);
    }
}
