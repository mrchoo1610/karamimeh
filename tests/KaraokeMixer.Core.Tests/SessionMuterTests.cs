using KaraokeMixer.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

/// <summary>
/// Regression coverage for the real bug this fix addresses: a real user's machine showed
/// AudioMixerCore's YouTube process-loopback capture stuck at exactly 0 bytes for an entire session
/// while <c>SessionMuter.SetMuteForProcessTree(pids, mute: true)</c> had set that process's Windows
/// Audio session <c>Mute = true</c> — and manually un-muting the session in Windows' own Volume
/// Mixer made bytes start flowing immediately. The empirically-confirmed fix is to stop using the
/// session's <c>Mute</c> flag entirely and instead drive its <c>Volume</c> to 0 (see the big comment
/// on <see cref="SessionMuter.SetMuteForProcessTree"/> for the full evidence and the — explicitly
/// [Unverified] — leading theory for why Mute=true breaks the capture: the captured process itself,
/// not Windows' audio engine, most likely stops rendering audio when it observes its own session
/// went silent).
///
/// This test cannot reproduce the ORIGINAL zero-bytes symptom (that requires a real WebView2 +
/// YouTube process reacting to its own session-mute notification — a synthetic NAudio WasapiOut tone
/// in this test process has no such reaction, confirmed separately via experiments/ConcurrencyRepro,
/// which found Mute=true never broke capture in the synthetic harness either). What THIS test does
/// verify, mechanically, is the contract change itself: that <c>SetMuteForProcessTree(mute: true)</c>
/// no longer touches <c>Mute</c> (leaves/sets it false) and instead sets <c>Volume = 0</c>, and that
/// <c>SetMuteForProcessTree(mute: false)</c> restores the session's ORIGINAL volume (not a hardcoded
/// 1.0) rather than just flipping Mute back off. Guards against silently regressing back to the
/// Mute=true implementation that real evidence showed breaks process-loopback capture.
///
/// Needs a real default render (playback) device to create a real audio session against — skips
/// (passes trivially, logging why) rather than failing on a machine/CI runner with no audio device,
/// since that is an environment limitation, not a code defect.
/// </summary>
public class SessionMuterTests
{
    [Fact]
    public void SetMuteForProcessTree_MuteTrue_SetsVolumeZeroAndLeavesMuteFlagFalse()
    {
        if (!TryCreateOwnAudioSession(out var toneOut, out var device))
        {
            return; // No audio hardware available in this environment — nothing to verify here.
        }

        using (toneOut)
        using (device)
        {
            try
            {
                var ownPids = new HashSet<uint> { (uint)Environment.ProcessId };

                var muteResult = SessionMuter.SetMuteForProcessTree(ownPids, mute: true);

                Assert.True(muteResult.Success, muteResult.Error);
                Assert.Contains((uint)Environment.ProcessId, muteResult.MatchedPids);

                var session = FindOwnSession(device);
                Assert.NotNull(session);
                Assert.False(session!.SimpleAudioVolume.Mute, "Mute flag must stay false — setting it true is exactly what broke process-loopback capture on real hardware.");
                Assert.Equal(0f, session.SimpleAudioVolume.Volume);
            }
            finally
            {
                // Best-effort restore even if an assertion above threw, so this test never leaves a
                // muted/zero-volume session behind for a later test or a developer's own speakers.
                SessionMuter.SetMuteForProcessTree(new HashSet<uint> { (uint)Environment.ProcessId }, mute: false);
            }
        }
    }

    [Fact]
    public void SetMuteForProcessTree_MuteFalse_RestoresOriginalVolumeNotHardcodedOne()
    {
        if (!TryCreateOwnAudioSession(out var toneOut, out var device))
        {
            return;
        }

        using (toneOut)
        using (device)
        {
            var ownPids = new HashSet<uint> { (uint)Environment.ProcessId };
            var originalSession = FindOwnSession(device);
            Assert.NotNull(originalSession);

            // Set a distinctive, non-default volume BEFORE muting, so restoring to a hardcoded 1.0f
            // (the old, wrong behavior) would be distinguishable from correctly restoring 0.5f.
            originalSession!.SimpleAudioVolume.Volume = 0.5f;

            try
            {
                var muteResult = SessionMuter.SetMuteForProcessTree(ownPids, mute: true);
                Assert.True(muteResult.Success, muteResult.Error);
                Assert.Equal(0f, FindOwnSession(device)!.SimpleAudioVolume.Volume);

                var unmuteResult = SessionMuter.SetMuteForProcessTree(ownPids, mute: false);
                Assert.True(unmuteResult.Success, unmuteResult.Error);

                var restoredSession = FindOwnSession(device);
                Assert.NotNull(restoredSession);
                Assert.False(restoredSession!.SimpleAudioVolume.Mute);
                Assert.Equal(0.5f, restoredSession.SimpleAudioVolume.Volume, precision: 3);
            }
            finally
            {
                SessionMuter.SetMuteForProcessTree(ownPids, mute: false);
            }
        }
    }

    private static bool TryCreateOwnAudioSession(out WasapiOut toneOut, out MMDevice device)
    {
        toneOut = null!;
        device = null!;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception)
        {
            // No default playback device on this machine/runner — not a code defect, just an
            // environment that can't exercise this test. See class doc comment.
            return false;
        }

        try
        {
            var signal = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.01 };
            toneOut = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            toneOut.Init(signal);
            toneOut.Play();

            // Windows only creates an audio SESSION for a process after it has actually rendered at
            // least one buffer — give it a moment before querying AudioSessionManager.Sessions.
            SpinWaitForOwnSession(device);
            return true;
        }
        catch (Exception)
        {
            toneOut?.Dispose();
            device?.Dispose();
            return false;
        }
    }

    private static void SpinWaitForOwnSession(MMDevice device)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (FindOwnSession(device) is not null)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    private static AudioSessionControl? FindOwnSession(MMDevice device)
    {
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].GetProcessID == (uint)Environment.ProcessId)
            {
                return sessions[i];
            }
        }

        return null;
    }
}
