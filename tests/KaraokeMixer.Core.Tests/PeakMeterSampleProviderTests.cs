using System.Threading;
using KaraokeMixer.Core.Dsp;
using KaraokeMixer.Core.Tests.TestSupport;

namespace KaraokeMixer.Core.Tests;

public class PeakMeterSampleProviderTests
{
    [Fact]
    public void Read_PassesSamplesThroughUnchanged()
    {
        const int sampleRate = 44100;
        var input = new float[] { 0.1f, -0.5f, 0.9f, -0.2f };
        var source = new ArraySampleProvider(input, sampleRate);
        var meter = new PeakMeterSampleProvider(source);

        var output = new float[input.Length];
        meter.Read(output, 0, output.Length);

        Assert.Equal(input, output);
    }

    [Fact]
    public void CurrentPeak_ReflectsLoudestSampleSeen_OncePublished()
    {
        const int sampleRate = 44100;
        var input = new float[] { 0.1f, -0.9f, 0.2f };
        var source = new ArraySampleProvider(input, sampleRate);
        var meter = new PeakMeterSampleProvider(source);

        // First Read() sees the loud (-0.9) sample and folds it into the running peak, but the
        // ~100ms publish interval likely hasn't elapsed yet, so CurrentPeak may still read 0 right
        // after this call. Sleeping past the interval, then doing a second Read() (of silence),
        // deterministically forces the publish — avoids a flaky "spin until it happens" loop, since
        // that loop's total wall-clock time is not guaranteed to reach 100ms no matter how many
        // samples it pushes through.
        var scratch = new float[input.Length];
        meter.Read(scratch, 0, scratch.Length);

        Thread.Sleep(150);

        var silence = new float[16];
        meter.Read(silence, 0, silence.Length);

        float observedPeak = meter.CurrentPeak;
        Assert.InRange(observedPeak, 0.85f, 0.95f); // ~0.9, the loudest sample fed in
    }
}
