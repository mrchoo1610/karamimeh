using NAudio.Wave;

namespace KaraokeMixer.Core.Tests.TestSupport;

/// <summary>
/// Minimal ISampleProvider test stub that plays back a fixed float array (interpreted as
/// already-interleaved samples at <paramref name="channels"/> channels), then returns silence
/// forever once exhausted — never runs out, so a test can Read() past the end of its "real" signal
/// to observe delayed effects (e.g. an echo tap arriving after the impulse itself).
/// </summary>
internal sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public ArraySampleProvider(float[] data, int sampleRate, int channels = 1)
    {
        _data = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = _position < _data.Length ? _data[_position] : 0f;
            _position++;
        }

        return count;
    }
}
