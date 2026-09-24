using KaraokeMixer.Core.Audio;
using KaraokeMixer.Core.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core;

/// <summary>Result of a <see cref="AudioMixerCore.StartAsync"/> call — a caller (KaraokeMixer.App)
/// always gets an actionable stage/detail instead of a bare exception.</summary>
public sealed record EngineStartResult(bool Success, string Stage, string? Detail)
{
    public static EngineStartResult Ok() => new(true, "StartAsync", null);

    public static EngineStartResult Fail(string stage, string? detail = null) => new(false, stage, detail);
}

/// <summary>Minimal description of an audio endpoint (capture OR render device) for UI device
/// pickers — avoids KaraokeMixer.App needing to reference NAudio.CoreAudioApi types directly just
/// to populate a ComboBox. Reused for both the mic (capture) and output (render) pickers since the
/// shape is identical.</summary>
public sealed record MicDeviceInfo(string Id, string Name);

/// <summary>
/// Wires the karamimeh audio pipeline together and owns its lifetime:
///
/// <code>
/// Mic (WASAPI capture, chosen or default device)
///   → Mono → EQ 3-band → Echo → back to Stereo → Mic Volume → normalize → SoftClip → PeakMeter → WasapiOut
///
/// YouTube (WebView2): NOT captured. Plays natively, unmodified, on the system's default output
/// device. "Music Volume" instead drives that process tree's own Windows Audio session Volume
/// directly (via SessionMuter). Windows' Shared-mode audio engine mixes the two streams together
/// on its own — see the big [ARCHITECTURE CHANGE] comment below for why.
/// </code>
///
/// Deliberately out of scope for this pass: acoustic-feedback suppression (notch-filter
/// auto-detection) and Reverb/Freeverb. Both are plain omissions here, not stubs, so there is
/// nothing half-wired to trip over.
/// </summary>
/// <remarks>
/// [ARCHITECTURE CHANGE, 2026-09-24] This class used to also run a process-loopback capture of the
/// WebView2/YouTube process (<c>ProcessLoopbackCapture</c>, still present in
/// <c>Audio/ProcessLoopbackCapture.cs</c> and fully working as a standalone mechanism — see
/// <c>spike/ProcessLoopbackSpike</c>), muting/zeroing that process's own session so the captured
/// copy could be re-mixed in software alongside the mic and sent to a single chosen output device.
///
/// That approach was abandoned after a real user's machine showed: no matter whether the YouTube
/// session was silenced via <c>Mute=true</c> OR <c>Volume=0</c>, the process-loopback capture
/// received EXACTLY ZERO bytes for the entire session (not "silent packets" — no packets at all),
/// while un-muting/raising volume immediately restored the native (uncaptured) YouTube sound. The
/// most coherent explanation (not confirmed against Chromium source — [Unverified]): Chromium
/// itself, on observing its own session go silent by either mechanism, stops actually rendering
/// audio at all as a CPU-saving optimization — starving anyone's loopback tap, not just muting the
/// user's ears. No reliable way was found to keep Chromium rendering while also silencing its
/// native output.
///
/// Given the original DSP design never applied EQ/Echo to the music branch anyway (only mic got
/// those), capturing YouTube's audio in software bought nothing except a "Music Volume" slider and
/// a single unified output device — both of which are achieved more simply, more robustly, and with
/// zero extra latency by NOT capturing at all: let YouTube play natively (Windows mixes multiple
/// apps' Shared-mode streams on the same device automatically — that is what Shared mode IS), send
/// only the mic chain through this engine's own WasapiOut on that same device, and drive "Music
/// Volume" by adjusting the YouTube process's own Windows session Volume directly (see
/// <see cref="MusicVolume"/> / <see cref="SessionMuter"/>). This also eliminates the entire
/// GC/COM-RCW fragility that process-loopback activation required (see the git history of this file
/// and <c>ProcessLoopbackCapture.cs</c>'s doc comments for that saga) — there is no longer any
/// forced <c>GC.Collect()</c> anywhere in this class, because there is no longer any
/// <c>ActivateAudioInterfaceAsync</c>-based COM object whose release timing matters.
///
/// Trade-off: the mic's own output device (chosen or default) MUST be the same physical device
/// WebView2/YouTube is playing on for the user to hear both mixed together — WebView2 follows
/// Windows' system default render device, so if the caller explicitly picks a non-default output
/// device for this engine, YouTube may keep playing on a different device than the mic. The caller
/// (KaraokeMixer.App) surfaces a warning for that case rather than this class silently overriding
/// the caller's explicit device choice.
/// </remarks>
public sealed class AudioMixerCore : IDisposable
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private CaptureSource? _micCapture;
    private WasapiOut? _output;

    private ThreeBandEqSampleProvider? _micEq;
    private EchoSampleProvider? _micEcho;
    private VolumeSampleProvider? _micVolumeProvider;
    private PeakMeterSampleProvider? _micPeakMeter;
    private PeakMeterSampleProvider? _masterPeakMeter;

    private uint _targetBrowserProcessId;
    private float _musicVolume = 0.85f;

    public bool IsRunning { get; private set; }

    /// <summary>How many Windows Audio sessions matched the WebView2 process tree on the last
    /// <see cref="StartAsync"/>/<see cref="MusicVolume"/> call (0 does not necessarily mean failure
    /// — Windows only creates a session for a process after it has rendered audio at least once, so
    /// a cold start where the video hasn't played yet will legitimately show 0 here until the first
    /// video actually starts playing).</summary>
    public int LastMusicSessionMatchedCount { get; private set; }

    // --- Live-adjustable parameters -------------------------------------------------------

    public float MicVolume
    {
        get => _micVolumeProvider?.Volume ?? 1f;
        set
        {
            if (_micVolumeProvider is not null)
            {
                _micVolumeProvider.Volume = Math.Clamp(value, 0f, 2f);
            }
        }
    }

    /// <summary>Drives the YouTube/WebView2 process tree's own Windows Audio session Volume
    /// directly (0.0–1.0 — the real range Windows sessions support, unlike <see cref="MicVolume"/>
    /// which is a software gain that can exceed 1.0). See the class remarks for why there is no
    /// software music branch to control instead. Safe to set before <see cref="StartAsync"/> has
    /// been called (or after <see cref="Stop"/>) — it just updates the stored value, applied the
    /// next time a target process is known.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = Math.Clamp(value, 0f, 1f);

            if (_targetBrowserProcessId != 0)
            {
                var pids = ProcessTreeHelper.GetProcessTreePids(_targetBrowserProcessId);
                var result = SessionMuter.SetVolumeForProcessTree(pids, _musicVolume);
                LastMusicSessionMatchedCount = result.MatchedPids.Count;
            }
        }
    }

    public bool EchoEnabled
    {
        get => _micEcho?.Enabled ?? false;
        set
        {
            if (_micEcho is not null)
            {
                _micEcho.Enabled = value;
            }
        }
    }

    public int EchoDelayMilliseconds
    {
        get => _micEcho?.DelayMilliseconds ?? 0;
        set
        {
            if (_micEcho is not null)
            {
                _micEcho.DelayMilliseconds = value;
            }
        }
    }

    public float EchoFeedback
    {
        get => _micEcho?.Feedback ?? 0f;
        set
        {
            if (_micEcho is not null)
            {
                _micEcho.Feedback = value;
            }
        }
    }

    public float EchoWetDryMix
    {
        get => _micEcho?.WetDryMix ?? 0f;
        set
        {
            if (_micEcho is not null)
            {
                _micEcho.WetDryMix = value;
            }
        }
    }

    public float EqLowGainDb
    {
        get => _micEq?.LowGainDb ?? 0f;
        set
        {
            if (_micEq is not null)
            {
                _micEq.LowGainDb = value;
            }
        }
    }

    public float EqMidGainDb
    {
        get => _micEq?.MidGainDb ?? 0f;
        set
        {
            if (_micEq is not null)
            {
                _micEq.MidGainDb = value;
            }
        }
    }

    public float EqHighGainDb
    {
        get => _micEq?.HighGainDb ?? 0f;
        set
        {
            if (_micEq is not null)
            {
                _micEq.HighGainDb = value;
            }
        }
    }

    /// <summary>Raw mic input peak level (0..~1), tapped before any DSP so it reflects what's
    /// actually arriving from the microphone regardless of volume/EQ/echo settings.</summary>
    public float MicPeakLevel => _micPeakMeter?.CurrentPeak ?? 0f;

    /// <summary>Post-SoftClip master output peak level (0..1) — what's actually being sent to
    /// WasapiOut. Note this reflects the MIC branch only now (see class remarks) — it will read 0
    /// even while YouTube is audibly playing, since that audio no longer passes through this
    /// engine's graph at all.</summary>
    public float MasterPeakLevel => _masterPeakMeter?.CurrentPeak ?? 0f;

    /// <summary>Enumerates available microphone (capture) devices for a UI device picker.
    /// Intentionally does not hardcode or assume any specific device — the caller decides which,
    /// if any, to treat as "selected by default" (typically the system default recording device).</summary>
    public static IReadOnlyList<MicDeviceInfo> EnumerateMicDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var result = new List<MicDeviceInfo>(devices.Count);

        foreach (var device in devices)
        {
            using (device)
            {
                result.Add(new MicDeviceInfo(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    /// <summary>Id of the system default recording device (Multimedia role), or null if none is
    /// available (e.g. no microphone plugged in).</summary>
    public static string? GetDefaultMicDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Enumerates available output (render) devices — speakers, headphones, a Bluetooth
    /// dongle, etc. — for a UI device picker. See the class remarks: picking a non-default device
    /// here only affects where THIS engine's mic output plays, not where WebView2/YouTube plays
    /// (that always follows the Windows system default).</summary>
    public static IReadOnlyList<MicDeviceInfo> EnumerateOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<MicDeviceInfo>(devices.Count);

        foreach (var device in devices)
        {
            using (device)
            {
                result.Add(new MicDeviceInfo(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    /// <summary>Id of the system default playback device (Multimedia role), or null if none is
    /// available.</summary>
    public static string? GetDefaultOutputDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the mic pipeline (opens the chosen or default microphone, builds the EQ/Echo/Volume
    /// DSP chain, starts playback on the chosen or default render device) and applies the current
    /// <see cref="MusicVolume"/> to the YouTube/WebView2 process tree's own Windows session. Does
    /// NOT capture or touch YouTube's audio otherwise — see class remarks.
    /// </summary>
    /// <param name="micDeviceId">MMDevice ID from <see cref="EnumerateMicDevices"/>, or null for
    /// the system default recording device.</param>
    /// <param name="browserProcessId">CoreWebView2.BrowserProcessId of the embedded WebView2 — used
    /// only to find its Windows session for <see cref="MusicVolume"/>, never for capture.</param>
    /// <param name="outputDeviceId">MMDevice ID from <see cref="EnumerateOutputDevices"/>, or null
    /// for the system default playback device. Should normally be left null/default so the mic
    /// output lands on the same device WebView2 is using — see class remarks.</param>
    public async Task<EngineStartResult> StartAsync(string? micDeviceId, uint browserProcessId, string? outputDeviceId = null)
    {
        if (IsRunning)
        {
            return EngineStartResult.Fail("StartAsync", "Engine is already running — call Stop() first.");
        }

        _deviceEnumerator = new MMDeviceEnumerator();

        MMDevice renderDevice;
        try
        {
            renderDevice = string.IsNullOrEmpty(outputDeviceId)
                ? _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _deviceEnumerator.GetDevice(outputDeviceId);
        }
        catch (Exception ex)
        {
            // Requested output device no longer present (unplugged/disconnected since the picker
            // was populated) — fall back to the system default rather than failing the whole start.
            try
            {
                renderDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex2)
            {
                return Fail("GetDefaultAudioEndpoint(Render)", ex2.Message);
            }

            _ = ex; // original device-specific failure isn't fatal, just falls back silently
        }

        // BUG FIX (still applies, unrelated to the capture removal): AudioClient.MixFormat is
        // frequently WaveFormatExtensible in practice (confirmed with a real "Speakers (Creative
        // BT-W6)" device) — passing that straight into MediaFoundationResampler throws "Unsupported
        // source encoding". See ToResamplerSafeFormat's doc comment for the full explanation.
        WaveFormat targetFormat = AudioFormatHelper.ToResamplerSafeFormat(renderDevice.AudioClient.MixFormat);

        // --- Mic capture ----------------------------------------------------------------
        MMDevice? micDevice = null;
        if (!string.IsNullOrEmpty(micDeviceId))
        {
            try
            {
                micDevice = _deviceEnumerator.GetDevice(micDeviceId);
            }
            catch (Exception)
            {
                // Requested device no longer present (unplugged since the picker was populated,
                // e.g. a broken wired headset or a Bluetooth dongle dropping out) — fall back to
                // the system default rather than failing the whole engine start.
                micDevice = null;
            }
        }

        try
        {
            _micCapture = new CaptureSource(micDevice);
            // BUG FIX: CaptureSource opens the device but never actually records until Start() is
            // called — missing entirely in an earlier pass, meaning the engine could "succeed" while
            // silently never producing any mic audio at all.
            _micCapture.Start();
        }
        catch (Exception ex)
        {
            CleanupPartialStart();
            return Fail("CaptureSource (mic WASAPI capture open)", ex.Message);
        }

        // Apply the current Music Volume to the YouTube process tree's own session (does nothing
        // fatal if no session exists yet — see MusicVolume's doc comment and SessionMuter).
        _targetBrowserProcessId = browserProcessId;
        var pids = ProcessTreeHelper.GetProcessTreePids(browserProcessId);
        var volumeResult = SessionMuter.SetVolumeForProcessTree(pids, _musicVolume);
        LastMusicSessionMatchedCount = volumeResult.MatchedPids.Count;

        try
        {
            // --- Mic DSP chain: Mono → EQ → Echo → back to Stereo → Volume → normalize --------
            ISampleProvider micChain = _micCapture;
            _micPeakMeter = new PeakMeterSampleProvider(micChain);
            micChain = _micPeakMeter;

            if (micChain.WaveFormat.Channels > 1)
            {
                micChain = new DownmixToMonoSampleProvider(micChain);
            }

            _micEq = new ThreeBandEqSampleProvider(micChain);
            micChain = _micEq;

            _micEcho = new EchoSampleProvider(micChain) { Enabled = false };
            micChain = _micEcho;

            if (targetFormat.Channels > 1)
            {
                micChain = new MonoToStereoSampleProvider(micChain);
            }

            _micVolumeProvider = new VolumeSampleProvider(micChain) { Volume = 1.0f };
            ISampleProvider normalizedMic = AudioFormatHelper.NormalizeToFormat(_micVolumeProvider, targetFormat);

            // --- SoftClip → master PeakMeter → output --------------------------------
            var softClip = new SoftClipSampleProvider(normalizedMic);
            _masterPeakMeter = new PeakMeterSampleProvider(softClip);

            return StartOutput();
        }
        catch (Exception ex)
        {
            CleanupPartialStart();
            return Fail("StartAsync (DSP graph construction)", ex.Message);
        }
    }

    private EngineStartResult StartOutput()
    {
        try
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _output.Init(_masterPeakMeter!.ToWaveProvider());
            _output.Play();
        }
        catch (Exception ex)
        {
            CleanupPartialStart();
            return Fail("WasapiOut.Init/Play", ex.Message);
        }

        IsRunning = true;
        return EngineStartResult.Ok();
    }

    private static EngineStartResult Fail(string stage, string? detail) => EngineStartResult.Fail(stage, detail);

    private void CleanupPartialStart()
    {
        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // Best-effort teardown of a partially-started engine — nothing useful to do with a
            // failure here beyond not letting it mask the original start failure being reported.
        }

        _output?.Dispose();
        _output = null;

        _micCapture?.Stop();
        _micCapture?.Dispose();
        _micCapture = null;

        RestoreMusicVolumeIfNeeded();

        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;

        _micEq = null;
        _micEcho = null;
        _micVolumeProvider = null;
        _micPeakMeter = null;
        _masterPeakMeter = null;
    }

    /// <summary>
    /// Stops mic capture/playback and restores the YouTube process tree's original session volume.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // Non-fatal — proceed with teardown regardless.
        }

        _output?.Dispose();
        _output = null;

        try
        {
            _micCapture?.Stop();
        }
        catch (Exception)
        {
            // Non-fatal.
        }

        _micCapture?.Dispose();
        _micCapture = null;

        RestoreMusicVolumeIfNeeded();

        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;

        _micEq = null;
        _micEcho = null;
        _micVolumeProvider = null;
        _micPeakMeter = null;
        _masterPeakMeter = null;

        IsRunning = false;
    }

    private void RestoreMusicVolumeIfNeeded()
    {
        if (_targetBrowserProcessId == 0)
        {
            return;
        }

        try
        {
            var pids = ProcessTreeHelper.GetProcessTreePids(_targetBrowserProcessId);
            SessionMuter.RestoreOriginalVolume(pids);
        }
        catch (Exception)
        {
            // Best-effort — if this fails the user can still adjust YouTube's volume manually;
            // not worth failing Stop() over.
        }

        _targetBrowserProcessId = 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
