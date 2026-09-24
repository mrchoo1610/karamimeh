# Technical Specification & Implementation Guide: Windows Karaoke Mixer Engine

## 1. Project Overview & Objectives
This document defines the software architecture, audio pipeline, and code specifications for building a desktop-based **Real-time Karaoke Audio Mixer Application** on Windows using **C# (.NET 8)** and **NAudio**.

The system acts as a software alternative to hardware mixer devices (e.g., Acnos MI36), performing real-time mixing of microphone inputs with system/file music streams, processing low-latency Digital Signal Processing (DSP) effects (Echo, Reverb, EQ), and outputting the combined audio signal.

---

## 2. Core Architecture & Audio Pipeline

```
[ Input Source 1: Microphone ]
       │ (WASAPI Capture / Low Buffer)
       ▼
[ Mic DSP Chain: EQ -> Delay/Echo -> Reverb ] ──┐
                                                 ├──> [ MixingSampleProvider ] ──> [ WASAPI Out / Low-Latency Master ]
[ Input Source 2: Music (File / Loopback) ]  ──┘    (Independent Volume Control)
```

### Key Technical Requirements
1. **Target Framework:** .NET 8.0 (WPF or Console Agent Core).
2. **Audio Library:** `NAudio` (v2.2+).
3. **Target Latency:** `< 10ms - 15ms` total round-trip audio latency.
4. **Sample Rate Standardization:** All internal audio streams MUST be resampled/converted to `44100 Hz` or `48000 Hz`, `32-bit Floating Point`, `Stereo` before reaching the mixer stage.

---

## 3. Component Breakdown

### A. Input Modules
* **Microphone Input:** Captured via `WasapiCapture` (Share/Exclusive Mode) or `WaveInEvent` with a buffer length of 10ms–20ms.
* **Music Input (Dual Mode):**
  * *File Mode:* `AudioFileReader` for local `.mp3` / `.wav` files.
  * *System Audio Mode:* `WasapiLoopbackCapture` to capture background audio directly from browsers (YouTube) or Spotify.

### B. DSP Processing Engine
* **Gain / Volume Control:** Dynamic gain adjustments per channel.
* **Delay / Echo Effect:** Feedback loop with configurable delay time (ms) and attenuation factor.
* **Reverb Effect:** Comb filter / All-pass filter network simulating room reflections.
* **Parametric Equalizer:** 3-band EQ (Low/Bass, Mid, High/Treble) for vocal tuning.

### C. Output Module
* **Low-Latency Master Output:** `WasapiOut` operating in `AudioClientShareMode.Automatic` or `Exclusive` mode with `latency: 10ms`.

---

## 4. Implementation Code Blueprint (C# Core Engine)

The following is the complete, self-contained implementation code for the core audio engine and DSP processor.

```csharp
using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixerEngine
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== WINDOWS KARAOKE MIXER ENGINE INITIALIZING ===");

            var engine = new AudioMixerCore();
            engine.Start();

            Console.WriteLine("\n[ENGINE RUNNING] Press keys to adjust settings:");
            Console.WriteLine("1/2: Mic Volume Up/Down");
            Console.WriteLine("3/4: Music Volume Up/Down");
            Console.WriteLine("E/R: Toggle/Adjust Echo Effect");
            Console.WriteLine("ESC: Exit Application");

            bool running = true;
            while (running)
            {
                var key = Console.ReadKey(true).Key;
                switch (key)
                {
                    case ConsoleKey.D1: engine.AdjustMicVolume(0.1f); break;
                    case ConsoleKey.D2: engine.AdjustMicVolume(-0.1f); break;
                    case ConsoleKey.D3: engine.AdjustMusicVolume(0.1f); break;
                    case ConsoleKey.D4: engine.AdjustMusicVolume(-0.1f); break;
                    case ConsoleKey.E: engine.ToggleEcho(); break;
                    case ConsoleKey.Escape: running = false; break;
                }
            }

            engine.Stop();
        }
    }

    public class AudioMixerCore
    {
        private WasapiCapture _micCapture;
        private BufferedWaveProvider _micBuffer;
        private VolumeSampleProvider _micVolume;
        private EchoSampleProvider _echoEffect;
        
        private AudioFileReader _musicReader;
        private VolumeSampleProvider _musicVolume;

        private MixingSampleProvider _mixer;
        private WasapiOut _outputDevice;

        public void Start()
        {
            // 1. Setup Microphone Input (Low Latency WASAPI)
            _micCapture = new WasapiCapture
            {
                ShareMode = AudioClientShareMode.Shared
            };

            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true
            };

            _micCapture.DataAvailable += (s, e) =>
            {
                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            // 2. Format Conversion & DSP Chain for Mic
            ISampleProvider micSample = _micBuffer.ToSampleProvider();

            // Convert to Stereo if Mono
            if (micSample.WaveFormat.Channels == 1)
            {
                micSample = new MonoToStereoSampleProvider(micSample);
            }

            // Apply Echo Effect DSP
            _echoEffect = new EchoSampleProvider(micSample)
            {
                DelayMilliseconds = 180,
                Feedback = 0.35f,
                WetDryMix = 0.3f,
                IsEnabled = true
            };

            // Mic Volume
            _micVolume = new VolumeSampleProvider(_echoEffect) { Volume = 1.0f };

            // 3. Setup Music Input (File or Loopback)
            string sampleMusicPath = "background_music.mp3";
            ISampleProvider musicProvider;

            if (File.Exists(sampleMusicPath))
            {
                _musicReader = new AudioFileReader(sampleMusicPath);
                musicProvider = _musicReader;
            }
            else
            {
                // Fallback silence if file missing
                musicProvider = new SignalGenerator(44100, 2) { Gain = 0 }.ToWaveProvider(16).ToSampleProvider();
            }

            _musicVolume = new VolumeSampleProvider(musicProvider) { Volume = 0.7f };

            // 4. Mixer Stage
            _mixer = new MixingSampleProvider(new[] { _micVolume, _musicVolume });

            // 5. Setup WASAPI Output
            _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 10);
            _outputDevice.Init(_mixer);

            // Start Audio Threads
            _micCapture.StartRecording();
            _outputDevice.Play();
        }

        public void AdjustMicVolume(float delta) => _micVolume.Volume = Math.Clamp(_micVolume.Volume + delta, 0.0f, 2.0f);
        public void AdjustMusicVolume(float delta) => _musicVolume.Volume = Math.Clamp(_musicVolume.Volume + delta, 0.0f, 2.0f);
        public void ToggleEcho() => _echoEffect.IsEnabled = !_echoEffect.IsEnabled;

        public void Stop()
        {
            _micCapture?.StopRecording();
            _outputDevice?.Stop();
            _micCapture?.Dispose();
            _musicReader?.Dispose();
            _outputDevice?.Dispose();
        }
    }

    /// <summary>
    /// Custom Real-time Echo DSP Processor
    /// </summary>
    public class EchoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _delayBuffer;
        private int _bufferPosition;

        public WaveFormat WaveFormat => _source.WaveFormat;
        public bool IsEnabled { get; set; } = true;
        public int DelayMilliseconds { get; set; } = 200;
        public float Feedback { get; set; } = 0.4f;
        public float WetDryMix { get; set; } = 0.35f;

        public EchoSampleProvider(ISampleProvider source)
        {
            _source = source;
            int bufferSize = _source.WaveFormat.SampleRate * _source.WaveFormat.Channels * 2; // 2 sec max delay
            _delayBuffer = new float[bufferSize];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            if (!IsEnabled) return samplesRead;

            int delaySamples = (int)((DelayMilliseconds / 1000.0) * WaveFormat.SampleRate) * WaveFormat.Channels;
            if (delaySamples <= 0) return samplesRead;

            for (int i = 0; i < samplesRead; i++)
            {
                int delayIndex = (_bufferPosition - delaySamples + _delayBuffer.Length) % _delayBuffer.Length;
                float drySample = buffer[offset + i];
                float wetSample = _delayBuffer[delayIndex];

                // Output Mix
                buffer[offset + i] = (drySample * (1.0f - WetDryMix)) + (wetSample * WetDryMix);

                // Write to Buffer with Feedback
                _delayBuffer[_bufferPosition] = drySample + (wetSample * Feedback);
                _bufferPosition = (_bufferPosition + 1) % _delayBuffer.Length;
            }

            return samplesRead;
        }
    }
}
```

---

## 5. Development Roadmap for Implementation Agent

### Phase 1: Core Engine & Latency Optimization (Sprint 1)
- [x] Integrate NAudio via NuGet (`dotnet add package NAudio`).
- [ ] Implement WASAPI Exclusive/Shared mode toggle for ultra-low latency (`< 10ms`).
- [ ] Implement `WasapiLoopbackCapture` to capture audio dynamically from Chrome/YouTube.

### Phase 2: Complete DSP Suite (Sprint 2)
- [ ] Expand `EchoSampleProvider` with stereo ping-pong delay support.
- [ ] Implement `ReverbSampleProvider` using Freeverb algorithm or Schroeder Reverb matrix.
- [ ] Implement 3-band Equalizer using NAudio `BiQuadFilter` (Low Shelf, Peaking EQ, High Shelf).

### Phase 3: UI Layer (Sprint 3)
- [ ] Build a WPF/WinUI 3 frontend with virtual mixer knobs/faders.
- [ ] Add input selection dropdowns (Device Selection for Mic and Audio Output).
- [ ] Add Master Peak Meter (VU Meter) for clipping detection.