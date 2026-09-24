using KaraokeMixer.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeMixer.Core.Tests;

/// <summary>
/// Regression coverage for <see cref="SessionMuter"/>'s current role: a direct Windows-session
/// Volume control used for the "Music Volume" slider (see the big class doc comment on
/// <see cref="SessionMuter"/> for the full history — an earlier "mute-then-recapture" architecture
/// was abandoned after real evidence showed both Mute=true AND Volume=0 broke a separate
/// process-loopback capture mechanism that is no longer used at all). These tests just verify the
/// mechanical contract: <see cref="SessionMuter.SetVolumeForProcessTree"/> sets Volume (never Mute)
/// and clamps to 0..1, and <see cref="SessionMuter.RestoreOriginalVolume"/> restores the ORIGINAL
/// volume (not a hardcoded 1.0) captured the first time a PID was touched.
///
/// Needs a real default render (playback) device to create a real audio session against — skips
/// (passes trivially) rather than failing on a machine/CI runner with no audio device, since that is
/// an environment limitation, not a code defect.
/// </summary>
public class SessionMuterTests
{
    [Fact]
    public void SetVolumeForProcessTree_SetsVolumeAndLeavesMuteFlagFalse()
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

                var result = SessionMuter.SetVolumeForProcessTree(ownPids, volume: 0f);

                Assert.True(result.Success, result.Error);
                Assert.Contains((uint)Environment.ProcessId, result.MatchedPids);

                var session = FindOwnSession(device);
                Assert.NotNull(session);
                Assert.False(session!.SimpleAudioVolume.Mute, "Mute flag must stay false — this class only ever drives Volume.");
                Assert.Equal(0f, session.SimpleAudioVolume.Volume);
            }
            finally
            {
                // Best-effort restore even if an assertion above threw, so this test never leaves a
                // zero-volume session behind for a later test or a developer's own speakers.
                SessionMuter.RestoreOriginalVolume(new HashSet<uint> { (uint)Environment.ProcessId });
            }
        }
    }

    [Fact]
    public void SetVolumeForProcessTree_ClampsOutOfRangeValues()
    {
        if (!TryCreateOwnAudioSession(out var toneOut, out var device))
        {
            return;
        }

        using (toneOut)
        using (device)
        {
            var ownPids = new HashSet<uint> { (uint)Environment.ProcessId };
            try
            {
                SessionMuter.SetVolumeForProcessTree(ownPids, volume: 5f);
                Assert.Equal(1f, FindOwnSession(device)!.SimpleAudioVolume.Volume, precision: 3);

                SessionMuter.SetVolumeForProcessTree(ownPids, volume: -1f);
                Assert.Equal(0f, FindOwnSession(device)!.SimpleAudioVolume.Volume, precision: 3);
            }
            finally
            {
                SessionMuter.RestoreOriginalVolume(ownPids);
            }
        }
    }

    [Fact]
    public void RestoreOriginalVolume_RestoresOriginalNotHardcodedOne()
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

            // Set a distinctive, non-default volume BEFORE touching it via SessionMuter, so
            // restoring to a hardcoded 1.0f (the old, wrong behavior) would be distinguishable from
            // correctly restoring 0.5f.
            originalSession!.SimpleAudioVolume.Volume = 0.5f;

            try
            {
                var setResult = SessionMuter.SetVolumeForProcessTree(ownPids, volume: 0f);
                Assert.True(setResult.Success, setResult.Error);
                Assert.Equal(0f, FindOwnSession(device)!.SimpleAudioVolume.Volume);

                var restoreResult = SessionMuter.RestoreOriginalVolume(ownPids);
                Assert.True(restoreResult.Success, restoreResult.Error);

                var restoredSession = FindOwnSession(device);
                Assert.NotNull(restoredSession);
                Assert.False(restoredSession!.SimpleAudioVolume.Mute);
                Assert.Equal(0.5f, restoredSession.SimpleAudioVolume.Volume, precision: 3);
            }
            finally
            {
                SessionMuter.RestoreOriginalVolume(ownPids);
            }
        }
    }

    private static bool TryCreateOwnAudioSession(out WasapiOut toneOut, out MMDevice device)
    {
        toneOut = null!;
        device = null!;

        // BUG FIX (found via a real, ~14-minute-stuck GitHub Actions run — not anticipated when
        // this test was first written): GitHub's windows-latest runners sometimes report a
        // "default" render device that enumerates successfully but isn't backed by real audio
        // hardware — WasapiOut.Init()/Play() against it can hang instead of throwing a catchable
        // exception, unlike a machine with genuinely NO device (which fails cleanly at
        // GetDefaultAudioEndpoint, already handled below). Bail out before touching any real audio
        // API at all when running under CI. GitHub Actions always sets both CI=true and
        // GITHUB_ACTIONS=true; check both since other CI systems only set one or the other.
        bool isCi = Environment.GetEnvironmentVariable("CI") is not null
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null;
        if (isCi)
        {
            return false;
        }

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
