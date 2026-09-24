using System.Diagnostics;
using KaraokeMixer.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// (see below for the "full-mute" scenario, added after the mic/GC hypotheses from the original
// bug report both failed to reproduce anything on real hardware — it tests a THIRD hypothesis the
// report didn't originally list: AudioMixerCore.StartAsync calls SessionMuter.SetMuteForProcessTree
// on the target process's OWN render session immediately after ProcessLoopbackCapture.StartAsync
// succeeds, and the real log's LastMuteMatchedCount=1 confirms that mute call actually found and
// muted a session on the exact same PID being loopback-captured.)

// Throwaway investigation harness for the karamimeh bug: real machine shows
// ProcessLoopbackCapture.TotalBytesCaptured staying at exactly 0 for an entire session while a
// concurrent mic WasapiCapture (CaptureSource) is also running in the same process. This harness
// isolates that one variable WITHOUT needing WebView2 or a human: it plays a synthetic tone through
// its OWN process (via WasapiOut) and process-loopback-captures its OWN process id — the same
// mechanism the real app uses, just with a self-generated "YouTube" stand-in instead of a real
// browser. See the karamimeh bug report for the full hypothesis list this exercises.
//
// Usage: ConcurrencyRepro.exe <scenario> <trials> <secondsPerTrial>
//   scenario: none | mic-before-gc | mic-after-gc | mic-no-gc | full
//     none          - loopback capture only, no concurrent mic CaptureSource at all (control group)
//     mic-before-gc - CaptureSource.Start() then GC.Collect()x2+WaitForPendingFinalizers, THEN
//                     ProcessLoopbackCapture.StartAsync (matches AudioMixerCore.StartAsync's real
//                     ordering exactly)
//     mic-after-gc  - CaptureSource.Start() then ProcessLoopbackCapture.StartAsync immediately, GC
//                     only AFTER loopback capture has started successfully (tests whether GC timing
//                     relative to loopback activation matters, independent of the mic variable)
//     mic-no-gc     - CaptureSource.Start() then ProcessLoopbackCapture.StartAsync, NO GC.Collect
//                     anywhere (isolates "does the mic alone matter" from "does the GC pause matter")
//     full          - mic-before-gc PLUS a third concurrent WasapiOut ("master output" stand-in),
//                     matching AudioMixerCore's full 3-way concurrency (mic WasapiCapture + loopback
//                     IAudioClient + master WasapiOut, all live at once), not just 2-way.

if (args.Length < 1)
{
    Console.WriteLine("Usage: ConcurrencyRepro.exe <none|mic-before-gc|mic-after-gc|mic-no-gc|full|full-mute|cross-process> [trials=3] [secondsPerTrial=5]");
    return 1;
}

// --- "tone-child" mode: a plain child process that just plays a tone via WasapiOut on the default
// render device, prints its own PID so a parent can target it, then sleeps. Used by the
// "cross-process" scenario below to test loopback-capturing a REAL DIFFERENT process (closer to the
// real WebView2-is-a-separate-process bug conditions than the self-capture scenarios above, which
// all target the harness's own PID).
if (args[0] == "tone-child")
{
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
    using var enumerator2 = new MMDeviceEnumerator();
    using var renderDevice2 = enumerator2.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var toneSignal2 = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.2 };
    using var toneOut2 = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
    toneOut2.Init(toneSignal2);
    toneOut2.Play();
    Console.WriteLine($"PID={Environment.ProcessId}");
    Console.WriteLine("READY");
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    toneOut2.Stop();
    return 0;
}

if (args[0] == "cross-process")
{
    return await RunCrossProcessScenarioAsync(
        trials: args.Length > 1 ? int.Parse(args[1]) : 3,
        secondsPerTrial: args.Length > 2 ? int.Parse(args[2]) : 5,
        withMic: args.Length > 3 && args[3] == "mic",
        withMute: args.Length > 4 && args[4] == "mute");
}

string scenario = args[0];
int trials = args.Length > 1 ? int.Parse(args[1]) : 3;
int secondsPerTrial = args.Length > 2 ? int.Parse(args[2]) : 5;

Console.WriteLine($"=== ConcurrencyRepro scenario={scenario} trials={trials} secondsPerTrial={secondsPerTrial} ownPid={Environment.ProcessId} ===");

var results = new List<(bool started, long total, long silent, long waitSignaled, long waitTimeout, long zeroPoll, long pktErr, int lastPktErrHr, string? initFlags, string? failStage, string? failDetail)>();

for (int trial = 1; trial <= trials; trial++)
{
    Console.WriteLine($"--- Trial {trial}/{trials} (scenario={scenario}) ---");
    var result = await RunOneTrialAsync(scenario, secondsPerTrial);
    results.Add(result);

    Console.WriteLine(
        $"[TRIAL-RESULT] trial={trial} started={result.started} total={result.total} silent={result.silent} " +
        $"waitSignaled={result.waitSignaled} waitTimeout={result.waitTimeout} zeroPoll={result.zeroPoll} " +
        $"pktErr={result.pktErr} lastPktErrHr=0x{result.lastPktErrHr:X8} initFlags={result.initFlags} " +
        $"failStage={result.failStage} failDetail={result.failDetail}");

    // Gap between trials, same spirit as the spike's timing-gap repro — not testing "does a short
    // gap change anything", just giving the OS a moment between full teardown/setup cycles.
    await Task.Delay(1000);
}

int successCount = results.Count(r => r.started);
long zeroByteRuns = results.Count(r => r.started && r.total == 0);
Console.WriteLine($"=== SUMMARY scenario={scenario}: {successCount}/{trials} StartAsync succeeded; " +
                   $"{zeroByteRuns}/{successCount} of those had TotalBytesCaptured==0 for the whole trial ===");

return 0;

static async Task<(bool started, long total, long silent, long waitSignaled, long waitTimeout, long zeroPoll, long pktErr, int lastPktErrHr, string? initFlags, string? failStage, string? failDetail)> RunOneTrialAsync(string scenario, int secondsPerTrial)
{
    CaptureSource? mic = null;
    WasapiOut? toneOut = null;
    WasapiOut? masterOut = null;
    ProcessLoopbackCapture? loopback = null;

    try
    {
        // --- Step 1: start our own "YouTube stand-in" — a real render session for our own PID,
        // exactly like a WebView2 process actually rendering YouTube audio. Started first and given
        // time to actually produce an audio session, mirroring "video already playing before the
        // user clicks Start" in the real app.
        using var enumerator = new MMDeviceEnumerator();
        using var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.WriteLine($"[HARNESS] Render device: {renderDevice.FriendlyName}");

        var toneSignal = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.2 };
        toneOut = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        toneOut.Init(toneSignal);
        toneOut.Play();
        await Task.Delay(400);
        Console.WriteLine("[HARNESS] Self-tone WasapiOut Play()'d — own process now has a real render audio session.");

        // --- Step 2: (scenario-dependent) start concurrent mic capture, exactly like
        // AudioMixerCore.StartAsync does before touching ProcessLoopbackCapture at all.
        if (scenario is "mic-before-gc" or "mic-after-gc" or "mic-no-gc" or "full" or "full-mute")
        {
            try
            {
                using var micEnumerator = new MMDeviceEnumerator();
                MMDevice? defaultMic = null;
                try
                {
                    defaultMic = micEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HARNESS] No default capture (mic) device available: {ex.Message}");
                }

                Console.WriteLine($"[HARNESS] Default mic device: {defaultMic?.FriendlyName ?? "(none found — CaptureSource will use its own internal default)"}");
                mic = new CaptureSource(defaultMic);
                mic.Start();
                Console.WriteLine("[HARNESS] Mic CaptureSource.Start()'d concurrently.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HARNESS] Mic CaptureSource failed to start (continuing WITHOUT mic for this trial): {ex}");
                mic = null;
            }
        }

        // --- Step 3: GC timing variant "before" - matches AudioMixerCore.StartAsync's real ordering
        // (GC happens right before constructing ProcessLoopbackCapture).
        if (scenario is "mic-before-gc" or "full" or "full-mute")
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Console.WriteLine("[HARNESS] Forced GC BEFORE ProcessLoopbackCapture.StartAsync (matches AudioMixerCore ordering).");
        }

        // --- Step 4: start process-loopback capture targeting OUR OWN process id (self-capture) —
        // same mechanism AudioMixerCore uses against the WebView2 browser process id.
        loopback = new ProcessLoopbackCapture();
        var sw = Stopwatch.StartNew();
        var startResult = await loopback.StartAsync((uint)Environment.ProcessId);
        Console.WriteLine($"[HARNESS] ProcessLoopbackCapture.StartAsync took {sw.ElapsedMilliseconds}ms -> success={startResult.Success} stage={startResult.Stage} hr={startResult.HResultHex} detail={startResult.Detail}");

        if (!startResult.Success)
        {
            return (false, 0, 0, 0, 0, 0, 0, 0, null, startResult.Stage, $"{startResult.HResultHex}: {startResult.Detail}");
        }

        // --- Step 4b (scenario "full" only): a THIRD concurrent WASAPI client — a master-output
        // WasapiOut, exactly mirroring AudioMixerCore.StartOutput(). Real AudioMixerCore.StartAsync
        // has 3 live WASAPI clients at once (mic capture, loopback capture, master output); the
        // other scenarios above only modeled 2 (mic capture + loopback capture) plus the loopback
        // TARGET's own render session (the tone stand-in, step 1) which is a 3rd but is the thing
        // being captured, not an independent consumer like the real master output is.
        if (scenario is "full")
        {
            var masterSignal = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 220, Gain = 0.05 };
            masterOut = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            masterOut.Init(masterSignal);
            masterOut.Play();
            Console.WriteLine("[HARNESS] Master-output WasapiOut Play()'d concurrently (3-way: mic capture + loopback capture + master output).");
        }

        // --- Step 4c (scenario "full-mute" only): reproduce AudioMixerCore.StartAsync's exact next
        // action after a successful ProcessLoopbackCapture.StartAsync — muting the target process's
        // OWN render session via SessionMuter.SetMuteForProcessTree. Tests whether muting the very
        // session being loopback-captured is what silences the capture loop's packet stream.
        HashSet<uint>? mutedOwnPids = null;
        if (scenario is "full-mute")
        {
            mutedOwnPids = new HashSet<uint> { (uint)Environment.ProcessId };
            var muteResult = SessionMuter.SetMuteForProcessTree(mutedOwnPids, mute: true);
            Console.WriteLine($"[HARNESS] SessionMuter.SetMuteForProcessTree(mute:true) success={muteResult.Success} matched={muteResult.MatchedPids.Count} error={muteResult.Error}");

            using var checkEnumerator = new MMDeviceEnumerator();
            using var checkDevice = checkEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = checkDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].GetProcessID == (uint)Environment.ProcessId)
                {
                    Console.WriteLine($"[HARNESS] Post-mute session state: Mute={sessions[i].SimpleAudioVolume.Mute} Volume={sessions[i].SimpleAudioVolume.Volume}");
                }
            }
        }

        // --- Step 5: GC timing variant "after" - GC only once loopback capture is already running.
        if (scenario is "mic-after-gc")
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Console.WriteLine("[HARNESS] Forced GC AFTER ProcessLoopbackCapture.StartAsync succeeded.");
        }

        // --- Step 6: poll for secondsPerTrial seconds, logging every second (same cadence spirit as
        // MainWindow.xaml.cs's [Peak] log line in the real app).
        for (int i = 1; i <= secondsPerTrial; i++)
        {
            await Task.Delay(1000);
            Console.WriteLine(
                $"[HARNESS POLL] t={i}s total={loopback.TotalBytesCaptured} silent={loopback.SilentBytesCaptured} " +
                $"waitSignaled={loopback.WaitSignaledCount} waitTimeout={loopback.WaitTimeoutCount} " +
                $"zeroPoll={loopback.ZeroPacketPollCount} pktErr={loopback.PacketSizeErrorCount} " +
                $"lastPktErrHr=0x{loopback.LastPacketSizeErrorHResult:X8} micRunning={mic is not null}");
        }

        if (mutedOwnPids is not null)
        {
            var unmuteResult = SessionMuter.SetMuteForProcessTree(mutedOwnPids, mute: false);
            Console.WriteLine($"[HARNESS] SessionMuter.SetMuteForProcessTree(mute:false) (restore) success={unmuteResult.Success} matched={unmuteResult.MatchedPids.Count}");

            using var checkEnumerator2 = new MMDeviceEnumerator();
            using var checkDevice2 = checkEnumerator2.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions2 = checkDevice2.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions2.Count; i++)
            {
                if (sessions2[i].GetProcessID == (uint)Environment.ProcessId)
                {
                    Console.WriteLine($"[HARNESS] Post-restore session state: Mute={sessions2[i].SimpleAudioVolume.Mute} Volume={sessions2[i].SimpleAudioVolume.Volume}");
                }
            }
        }

        return (true, loopback.TotalBytesCaptured, loopback.SilentBytesCaptured, loopback.WaitSignaledCount,
            loopback.WaitTimeoutCount, loopback.ZeroPacketPollCount, loopback.PacketSizeErrorCount,
            loopback.LastPacketSizeErrorHResult, loopback.InitializeFlagsUsed, null, null);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[HARNESS] UNEXPECTED EXCEPTION: {ex}");
        return (false, 0, 0, 0, 0, 0, 0, 0, null, "harness exception", ex.Message);
    }
    finally
    {
        try { loopback?.Stop(); loopback?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] loopback teardown ex: {ex.Message}"); }
        try { toneOut?.Stop(); toneOut?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] toneOut teardown ex: {ex.Message}"); }
        try { masterOut?.Stop(); masterOut?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] masterOut teardown ex: {ex.Message}"); }
        try { mic?.Stop(); mic?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] mic teardown ex: {ex.Message}"); }

        // Same mandatory fix as AudioMixerCore.Stop()/CleanupPartialStart() — applied every trial so
        // trial N+1 isn't polluted by trial N's leaked COM RCWs.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

static async Task<int> RunCrossProcessScenarioAsync(int trials, int secondsPerTrial, bool withMic, bool withMute)
{
    Console.WriteLine($"=== ConcurrencyRepro cross-process trials={trials} secondsPerTrial={secondsPerTrial} withMic={withMic} withMute={withMute} ownPid={Environment.ProcessId} ===");
    string exePath = Environment.ProcessPath ?? "dotnet";
    string selfDll = System.Reflection.Assembly.GetExecutingAssembly().Location;

    int successCount = 0;
    int zeroByteCount = 0;

    for (int trial = 1; trial <= trials; trial++)
    {
        Console.WriteLine($"--- Cross-process trial {trial}/{trials} ---");
        Process? child = null;
        CaptureSource? mic = null;
        ProcessLoopbackCapture? loopback = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            // When run via `dotnet ConcurrencyRepro.dll ...`, ProcessPath is the dotnet host, so the
            // dll path must be passed as the first argument; when run as a self-contained/apphost exe
            // directly, ProcessPath IS the exe already and no dll arg is needed.
            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(selfDll);
            }

            psi.ArgumentList.Add("tone-child");
            psi.ArgumentList.Add((secondsPerTrial + 15).ToString());

            child = Process.Start(psi);
            if (child is null)
            {
                Console.WriteLine("[HARNESS] Failed to start tone-child process.");
                continue;
            }

            string? pidLine = await child.StandardOutput.ReadLineAsync();
            string? readyLine = await child.StandardOutput.ReadLineAsync();
            Console.WriteLine($"[HARNESS] Child stdout: '{pidLine}' / '{readyLine}'");

            if (pidLine is null || !pidLine.StartsWith("PID=") || !uint.TryParse(pidLine.AsSpan(4), out uint childPid))
            {
                Console.WriteLine("[HARNESS] Could not parse child PID — aborting this trial.");
                continue;
            }

            Console.WriteLine($"[HARNESS] Child tone process PID={childPid} is READY and rendering audio.");
            await Task.Delay(300);

            if (withMic)
            {
                using var micEnumerator = new MMDeviceEnumerator();
                MMDevice? defaultMic = null;
                try { defaultMic = micEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
                catch (Exception ex) { Console.WriteLine($"[HARNESS] No default mic: {ex.Message}"); }

                mic = new CaptureSource(defaultMic);
                mic.Start();
                Console.WriteLine("[HARNESS] Mic CaptureSource.Start()'d concurrently (targeting a DIFFERENT process's loopback).");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            loopback = new ProcessLoopbackCapture();
            var startResult = await loopback.StartAsync(childPid);
            Console.WriteLine($"[HARNESS] ProcessLoopbackCapture.StartAsync(childPid={childPid}) -> success={startResult.Success} stage={startResult.Stage} hr={startResult.HResultHex} detail={startResult.Detail}");

            if (!startResult.Success)
            {
                continue;
            }

            if (withMute)
            {
                var muteResult = SessionMuter.SetMuteForProcessTree(new HashSet<uint> { childPid }, mute: true);
                Console.WriteLine($"[HARNESS] SessionMuter mute of child PID: success={muteResult.Success} matched={muteResult.MatchedPids.Count}");
            }

            for (int i = 1; i <= secondsPerTrial; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine(
                    $"[HARNESS POLL] t={i}s total={loopback.TotalBytesCaptured} silent={loopback.SilentBytesCaptured} " +
                    $"waitSignaled={loopback.WaitSignaledCount} waitTimeout={loopback.WaitTimeoutCount} " +
                    $"zeroPoll={loopback.ZeroPacketPollCount} pktErr={loopback.PacketSizeErrorCount} " +
                    $"lastPktErrHr=0x{loopback.LastPacketSizeErrorHResult:X8}");
            }

            successCount++;
            if (loopback.TotalBytesCaptured == 0)
            {
                zeroByteCount++;
            }

            Console.WriteLine($"[TRIAL-RESULT] trial={trial} total={loopback.TotalBytesCaptured} silent={loopback.SilentBytesCaptured} waitSignaled={loopback.WaitSignaledCount} waitTimeout={loopback.WaitTimeoutCount}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HARNESS] UNEXPECTED EXCEPTION: {ex}");
        }
        finally
        {
            try { loopback?.Stop(); loopback?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] loopback teardown ex: {ex.Message}"); }
            try { mic?.Stop(); mic?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[HARNESS] mic teardown ex: {ex.Message}"); }
            try
            {
                if (child is not null && !child.HasExited)
                {
                    child.Kill();
                }

                child?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HARNESS] child teardown ex: {ex.Message}");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        await Task.Delay(1000);
    }

    Console.WriteLine($"=== SUMMARY cross-process withMic={withMic} withMute={withMute}: {successCount}/{trials} succeeded; {zeroByteCount}/{successCount} had TotalBytesCaptured==0 ===");
    return 0;
}
