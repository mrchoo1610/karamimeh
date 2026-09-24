using System.Runtime.CompilerServices;

// Grants the throwaway concurrency-repro harness (experiments/ConcurrencyRepro — see that folder's
// README/Program.cs for context) direct access to internal types (ProcessLoopbackCapture) instead of
// needing reflection. Investigation-only visibility grant — does not change any runtime behavior of
// KaraokeMixer.Core itself. Safe to remove if the harness is deleted and nothing else needs it.
[assembly: InternalsVisibleTo("ConcurrencyRepro")]

// Grants KaraokeMixer.Core.Tests direct access to internal types (SessionMuter, ProcessLoopbackCapture)
// so the SessionMuter regression test (SessionMuterTests.cs) can call SetMuteForProcessTree directly
// instead of going through reflection or the full AudioMixerCore pipeline.
[assembly: InternalsVisibleTo("KaraokeMixer.Core.Tests")]
