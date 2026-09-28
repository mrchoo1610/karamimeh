using KaraokeMixer.Core.Dsp;
using KaraokeMixer.Core.Tests.TestSupport;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

public class ReverbSampleProviderTests
{
    [Fact]
    public void Read_Disabled_PassesThroughUnchanged()
    {
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f };
        var source = new ArraySampleProvider(input, 44100);
        var reverb = new ReverbSampleProvider(source) { Enabled = false };

        var output = new float[input.Length];
        reverb.Read(output, 0, output.Length);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Read_Enabled_ImpulseInput_IsSilentUntilShortestCombDelay_ThenProducesATail()
    {
        const int sampleRate = 44100;
        const int shortestCombDelaySamples = 1116; // smallest Freeverb comb tuning, exact at 44100Hz

        var impulse = new float[] { 1.0f };
        var source = new ArraySampleProvider(impulse, sampleRate);
        var reverb = new ReverbSampleProvider(source)
        {
            Enabled = true,
            RoomSize = 0.8f,
            Damping = 0.2f,
            WetDryMix = 1.0f, // fully wet, so silence-before-first-tap is exact, not just "small"
        };

        var output = new float[shortestCombDelaySamples + 1];
        reverb.Read(output, 0, output.Length);

        for (int i = 0; i < shortestCombDelaySamples; i++)
        {
            Assert.Equal(0f, output[i]);
        }

        Assert.NotEqual(0f, output[shortestCombDelaySamples]);
    }

    [Fact]
    public void Read_DoesNotThrow_AndPreservesBufferLengthAndFormat()
    {
        const int sampleRate = 44100;
        const int channels = 2;

        var signal = new SignalGenerator(sampleRate, channels)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = 440,
            Gain = 0.3,
        };

        var reverb = new ReverbSampleProvider(signal)
        {
            Enabled = true,
            RoomSize = 0.7f,
            Damping = 0.4f,
            WetDryMix = 0.5f,
        };

        Assert.Equal(channels, reverb.WaveFormat.Channels);
        Assert.Equal(sampleRate, reverb.WaveFormat.SampleRate);

        var buffer = new float[8192];
        int samplesRead = reverb.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, samplesRead);
        foreach (float sample in buffer)
        {
            Assert.False(float.IsNaN(sample), "Reverb output must never be NaN");
            Assert.False(float.IsInfinity(sample), "Reverb output must never be infinite");
        }
    }

    [Fact]
    public void RoomSize_Damping_WetDryMix_Setters_ClampToDocumentedRange()
    {
        var source = new ArraySampleProvider(new float[] { 1f }, 44100);
        var reverb = new ReverbSampleProvider(source);

        reverb.RoomSize = -1f;
        Assert.Equal(0f, reverb.RoomSize);
        reverb.RoomSize = 5f;
        Assert.Equal(1f, reverb.RoomSize);

        reverb.Damping = -1f;
        Assert.Equal(0f, reverb.Damping);
        reverb.Damping = 5f;
        Assert.Equal(1f, reverb.Damping);

        reverb.WetDryMix = -1f;
        Assert.Equal(0f, reverb.WetDryMix);
        reverb.WetDryMix = 5f;
        Assert.Equal(1f, reverb.WetDryMix);
    }
}
