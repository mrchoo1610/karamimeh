using KaraokeMixer.Core.Dsp;
using KaraokeMixer.Core.Tests.TestSupport;

namespace KaraokeMixer.Core.Tests;

public class SoftClipSampleProviderTests
{
    [Fact]
    public void Read_SignalBelowThreshold_PassesThroughUnchanged()
    {
        const int sampleRate = 44100;
        var input = new float[] { 0.1f, -0.2f, 0.5f, -0.5f, 0.79f, -0.79f };
        var source = new ArraySampleProvider(input, sampleRate);
        var softClip = new SoftClipSampleProvider(source) { Threshold = 0.8f };

        var output = new float[input.Length];
        softClip.Read(output, 0, output.Length);

        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i], precision: 5);
        }
    }

    [Fact]
    public void Read_SignalAboveThreshold_NeverExceedsUnityMagnitude()
    {
        const int sampleRate = 44100;
        // Deliberately unbounded values a mic+music sum could produce (e.g. both at full volume).
        var input = new float[] { 1.0f, 1.5f, 2.0f, 5.0f, -1.2f, -3.0f, -10f };
        var source = new ArraySampleProvider(input, sampleRate);
        var softClip = new SoftClipSampleProvider(source) { Threshold = 0.8f };

        var output = new float[input.Length];
        softClip.Read(output, 0, output.Length);

        foreach (float sample in output)
        {
            // Mathematically tanh(x) < 1 for any finite x, but at extreme input magnitudes (e.g.
            // the -10.0/10.0 samples in this test) the shaped value rounds to exactly 1.0f once
            // cast to float — so the real, float-precision-safe guarantee is "never exceeds", not
            // "strictly less than". <= 1.0 is also what actually matters for the purpose of this
            // limiter (preventing values from going past the float/DAC-safe range), which is what
            // the task asked to verify.
            Assert.True(Math.Abs(sample) <= 1.0f, $"Soft-clipped sample {sample} must never exceed 1.0 in magnitude");
        }
    }

    [Fact]
    public void Read_PreservesSignOfInput()
    {
        const int sampleRate = 44100;
        var input = new float[] { 2.0f, -2.0f };
        var source = new ArraySampleProvider(input, sampleRate);
        var softClip = new SoftClipSampleProvider(source);

        var output = new float[input.Length];
        softClip.Read(output, 0, output.Length);

        Assert.True(output[0] > 0, "Positive input must stay positive after soft-clipping");
        Assert.True(output[1] < 0, "Negative input must stay negative after soft-clipping");
    }
}
