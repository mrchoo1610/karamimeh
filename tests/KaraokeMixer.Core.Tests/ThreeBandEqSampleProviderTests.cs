using KaraokeMixer.Core.Dsp;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

public class ThreeBandEqSampleProviderTests
{
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

        var eq = new ThreeBandEqSampleProvider(signal)
        {
            LowGainDb = 6,
            MidGainDb = -3,
            HighGainDb = 9,
        };

        Assert.Equal(channels, eq.WaveFormat.Channels);
        Assert.Equal(sampleRate, eq.WaveFormat.SampleRate);

        var buffer = new float[2048];
        int samplesRead = eq.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, samplesRead);

        foreach (float sample in buffer)
        {
            Assert.False(float.IsNaN(sample), "EQ output must never be NaN");
            Assert.False(float.IsInfinity(sample), "EQ output must never be infinite");
        }
    }

    [Fact]
    public void GainSetters_ClampToDocumentedRange()
    {
        var signal = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = 440 };
        var eq = new ThreeBandEqSampleProvider(signal);

        eq.LowGainDb = 100;
        Assert.Equal(ThreeBandEqSampleProvider.MaxGainDb, eq.LowGainDb);

        eq.MidGainDb = -100;
        Assert.Equal(ThreeBandEqSampleProvider.MinGainDb, eq.MidGainDb);

        eq.HighGainDb = 100;
        Assert.Equal(ThreeBandEqSampleProvider.MaxGainDb, eq.HighGainDb);
    }

    [Fact]
    public void LiveGainChange_MidBand_DoesNotThrow_AndAffectsOutput()
    {
        // Smoke-level check that changing a gain live (as a UI slider would) works without
        // exceptions and without silently becoming a no-op — a full frequency-response assertion
        // is out of scope here (BiQuadFilter's own math is NAudio's, already tested upstream).
        var signal = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = 1000, Gain = 0.3 };
        var eq = new ThreeBandEqSampleProvider(signal) { MidGainDb = 0 };

        var bufferFlat = new float[1024];
        eq.Read(bufferFlat, 0, bufferFlat.Length);

        eq.MidGainDb = 12; // boost right at the peaking band's centre frequency (1kHz)

        var bufferBoosted = new float[1024];
        var exception = Record.Exception(() => eq.Read(bufferBoosted, 0, bufferBoosted.Length));
        Assert.Null(exception);

        double flatRms = Rms(bufferFlat);
        double boostedRms = Rms(bufferBoosted);
        Assert.True(boostedRms > flatRms, "Boosting the mid band at the signal's own frequency should increase output level");
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
