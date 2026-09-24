using KaraokeMixer.Core.Dsp;
using KaraokeMixer.Core.Tests.TestSupport;

namespace KaraokeMixer.Core.Tests;

public class EchoSampleProviderTests
{
    [Fact]
    public void Read_ImpulseInput_ProducesEchoAtExpectedDelayOffset()
    {
        const int sampleRate = 44100;
        const int delayMilliseconds = 100;
        const float feedback = 0.5f;
        int expectedDelaySamples = (int)(delayMilliseconds / 1000.0 * sampleRate); // 4410

        // Impulse: a single 1.0 sample at t=0, everything else silence.
        var impulse = new float[1] { 1.0f };
        var source = new ArraySampleProvider(impulse, sampleRate);

        var echo = new EchoSampleProvider(source)
        {
            Enabled = true,
            DelayMilliseconds = delayMilliseconds,
            Feedback = feedback,
            WetDryMix = 1.0f, // fully wet, so the echo tap value is easy to reason about exactly
        };

        int totalSamples = expectedDelaySamples * 3 + 100;
        var output = new float[totalSamples];
        echo.Read(output, 0, totalSamples);

        // First echo repeat: the impulse we wrote at t=0 should reappear ~4410 samples later.
        float firstRepeatPeak = MaxAbsInRange(output, expectedDelaySamples - 2, expectedDelaySamples + 2);
        Assert.True(firstRepeatPeak > 0.9f, $"Expected an echo repeat near sample {expectedDelaySamples}, peak found was {firstRepeatPeak}");

        // Second echo repeat, one delay period later, should be attenuated by ~feedback (0.5)
        // relative to the first repeat — feedback < 1 means later repeats are smaller.
        int secondRepeatIndex = expectedDelaySamples * 2;
        float secondRepeatPeak = MaxAbsInRange(output, secondRepeatIndex - 2, secondRepeatIndex + 2);

        Assert.True(secondRepeatPeak > 0f, "Expected a second, decayed echo repeat to be present");
        Assert.True(secondRepeatPeak < firstRepeatPeak, "Later echo repeats must decay (feedback < 1)");
        Assert.InRange(secondRepeatPeak, feedback - 0.05f, feedback + 0.05f);
    }

    [Fact]
    public void DelayMilliseconds_Setter_ClampsToSafeRange_AndNeverThrows()
    {
        var source = new ArraySampleProvider(new float[] { 1f }, 44100);
        var echo = new EchoSampleProvider(source);

        echo.DelayMilliseconds = -500;
        Assert.InRange(echo.DelayMilliseconds, 1, EchoSampleProvider.MaxDelayMilliseconds);

        // This is exactly the scenario that broke the earlier architecture-spec draft: setting a
        // delay far beyond what a fixed-size ring buffer was constructed for. Here it must clamp,
        // not throw.
        echo.DelayMilliseconds = 999_999;
        Assert.Equal(EchoSampleProvider.MaxDelayMilliseconds, echo.DelayMilliseconds);

        var buffer = new float[8000];
        var exception = Record.Exception(() => echo.Read(buffer, 0, buffer.Length));
        Assert.Null(exception);
    }

    [Fact]
    public void Feedback_And_WetDryMix_Setters_ClampToDocumentedRanges()
    {
        var source = new ArraySampleProvider(new float[] { 1f }, 44100);
        var echo = new EchoSampleProvider(source);

        echo.Feedback = -1f;
        Assert.Equal(0f, echo.Feedback);
        echo.Feedback = 5f;
        Assert.Equal(0.95f, echo.Feedback);

        echo.WetDryMix = -1f;
        Assert.Equal(0f, echo.WetDryMix);
        echo.WetDryMix = 5f;
        Assert.Equal(1f, echo.WetDryMix);
    }

    private static float MaxAbsInRange(float[] data, int start, int endExclusive)
    {
        start = Math.Max(0, start);
        endExclusive = Math.Min(data.Length, endExclusive);
        float max = 0f;
        for (int i = start; i < endExclusive; i++)
        {
            float abs = Math.Abs(data[i]);
            if (abs > max)
            {
                max = abs;
            }
        }

        return max;
    }
}
