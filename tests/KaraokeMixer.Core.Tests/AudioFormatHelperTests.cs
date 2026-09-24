using KaraokeMixer.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

/// <summary>
/// Regression coverage for the exact failure mode the karamimeh mixer must avoid: handing
/// <c>MixingSampleProvider</c> two inputs with different formats. NAudio does not resample or
/// remix channels for you — it throws. <see cref="AudioFormatHelper.NormalizeToFormat"/> exists
/// specifically to prevent that from ever reaching the mixer.
/// </summary>
public class AudioFormatHelperTests
{
    [Fact]
    public void MixingSampleProvider_AddMixerInput_WithMismatchedSampleRate_ThrowsArgumentException()
    {
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var mixer = new MixingSampleProvider(targetFormat);

        var mismatched = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440 };

        // This is the exact mistake the karaoke engine must never make: handing the mixer a
        // source whose format doesn't match, on the assumption NAudio will "figure it out". It
        // does not — this must throw, confirming the helper below is actually necessary.
        Assert.Throws<ArgumentException>(() => mixer.AddMixerInput(mismatched));
    }

    [Fact]
    public void MixingSampleProvider_AddMixerInput_WithMismatchedChannelCount_ThrowsArgumentException()
    {
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var mixer = new MixingSampleProvider(targetFormat);

        var mismatched = new SignalGenerator(48000, 1) { Type = SignalGeneratorType.Sin, Frequency = 440 };

        Assert.Throws<ArgumentException>(() => mixer.AddMixerInput(mismatched));
    }

    [Fact]
    public void NormalizeToFormat_WithMismatchedSampleRate_ActuallyResamples_AndMixerAcceptsResult()
    {
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var mixer = new MixingSampleProvider(targetFormat);

        var source = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.3 };

        ISampleProvider normalized = AudioFormatHelper.NormalizeToFormat(source, targetFormat);

        Assert.Equal(targetFormat.SampleRate, normalized.WaveFormat.SampleRate);
        Assert.Equal(targetFormat.Channels, normalized.WaveFormat.Channels);
        Assert.NotSame(source, normalized); // must have actually wrapped/resampled, not passed through

        var exception = Record.Exception(() => mixer.AddMixerInput(normalized));
        Assert.Null(exception);

        // Confirm the mixer graph actually produces real (non-silent, non-NaN) audio after
        // resampling, not just that AddMixerInput didn't throw.
        var buffer = new float[4096];
        int samplesRead = mixer.Read(buffer, 0, buffer.Length);
        Assert.True(samplesRead > 0);

        bool anyNonZero = false;
        foreach (float sample in buffer)
        {
            Assert.False(float.IsNaN(sample));
            Assert.False(float.IsInfinity(sample));
            if (sample != 0f)
            {
                anyNonZero = true;
            }
        }

        Assert.True(anyNonZero, "Expected the resampled 440Hz tone to actually produce non-silent output");
    }

    [Fact]
    public void NormalizeToFormat_WithAlreadyMatchingFormat_ReturnsSameInstance()
    {
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var source = new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = 440 };

        ISampleProvider result = AudioFormatHelper.NormalizeToFormat(source, targetFormat);

        Assert.Same(source, result);
    }

    // --- Regression coverage for a real bug found via a user's debug log: AudioClient.MixFormat
    // came back as WaveFormatExtensible for a real device ("Speakers (Creative BT-W6)"), and handing
    // that straight to MediaFoundationResampler threw ArgumentException("Unsupported source
    // encoding"). ToResamplerSafeFormat exists specifically to prevent that. ------------------------

    [Fact]
    public void ToResamplerSafeFormat_With32BitExtensible_ReturnsPlainIeeeFloat()
    {
        var extensible = new WaveFormatExtensible(48000, 32, 2);
        Assert.Equal(WaveFormatEncoding.Extensible, extensible.Encoding); // sanity: this really is the problematic shape

        WaveFormat safe = AudioFormatHelper.ToResamplerSafeFormat(extensible);

        Assert.Equal(WaveFormatEncoding.IeeeFloat, safe.Encoding);
        Assert.Equal(48000, safe.SampleRate);
        Assert.Equal(2, safe.Channels);
        Assert.Equal(32, safe.BitsPerSample);
    }

    [Fact]
    public void ToResamplerSafeFormat_With16BitExtensible_ReturnsPlainPcm()
    {
        var extensible = new WaveFormatExtensible(44100, 16, 2);

        WaveFormat safe = AudioFormatHelper.ToResamplerSafeFormat(extensible);

        Assert.Equal(WaveFormatEncoding.Pcm, safe.Encoding);
        Assert.Equal(44100, safe.SampleRate);
        Assert.Equal(2, safe.Channels);
        Assert.Equal(16, safe.BitsPerSample);
    }

    [Fact]
    public void ToResamplerSafeFormat_WithAlreadyPlainFormat_ReturnsSameInstance()
    {
        var plain = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        WaveFormat result = AudioFormatHelper.ToResamplerSafeFormat(plain);

        Assert.Same(plain, result);
    }

    [Fact]
    public void NormalizeToFormat_WithExtensibleTargetPutThroughToResamplerSafeFormat_DoesNotThrow_AndProducesRealAudio()
    {
        // Reproduces the exact real-world shape: a device mix format that is WaveFormatExtensible,
        // resampling FROM a different sample rate (so NormalizeToFormat must actually invoke
        // MediaFoundationResampler, not just pass through unchanged).
        var deviceMixFormat = new WaveFormatExtensible(48000, 32, 2);
        WaveFormat targetFormat = AudioFormatHelper.ToResamplerSafeFormat(deviceMixFormat);

        var source = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.3 };

        ISampleProvider normalized = AudioFormatHelper.NormalizeToFormat(source, targetFormat);

        Assert.Equal(targetFormat.SampleRate, normalized.WaveFormat.SampleRate);
        Assert.Equal(targetFormat.Channels, normalized.WaveFormat.Channels);

        var buffer = new float[4096];
        int samplesRead = normalized.Read(buffer, 0, buffer.Length);
        Assert.True(samplesRead > 0);

        bool anyNonZero = false;
        foreach (float sample in buffer)
        {
            Assert.False(float.IsNaN(sample));
            Assert.False(float.IsInfinity(sample));
            if (sample != 0f)
            {
                anyNonZero = true;
            }
        }

        Assert.True(anyNonZero, "Expected real resampled audio, not silence");
    }
}
