using NAudio.Wave;

namespace KaraokeMixer.Core.Dsp;

/// <summary>
/// Averages an arbitrary number of input channels down to mono. Used instead of NAudio's own
/// <c>StereoToMonoSampleProvider</c> for the mic chain's "convert to mono first" step, because that
/// NAudio helper specifically requires exactly 2 input channels — this app deliberately does not
/// hardcode any particular mic device (the user's wired headset broke; they could end up on a
/// laptop mic, a Bluetooth device via a Creative BT-W6 dongle, or something else with an unusual
/// channel count), so the mono-downmix step needs to tolerate any channel count NAudio hands us,
/// not just exactly 2.
/// </summary>
public sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceScratch = [];

    public WaveFormat WaveFormat { get; }

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = Math.Max(1, source.WaveFormat.Channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        int sourceSamplesNeeded = count * _sourceChannels;
        if (_sourceScratch.Length < sourceSamplesNeeded)
        {
            _sourceScratch = new float[sourceSamplesNeeded];
        }

        int sourceSamplesRead = _source.Read(_sourceScratch, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _sourceChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            float sum = 0f;
            int baseIndex = frame * _sourceChannels;
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                sum += _sourceScratch[baseIndex + ch];
            }

            buffer[offset + frame] = sum / _sourceChannels;
        }

        return framesRead;
    }
}
