using System;
using System.Runtime.InteropServices;
using ElizaAccess.Common;

namespace ElizaAccess
{
    /// <summary>
    /// Speaks text via the Prism native library, a single screen-reader
    /// abstraction that wraps NVDA, JAWS, SAPI, OneCore, ZDSR,
    /// ZoomText, System Access, PCTalker, Sense Reader, and more
    /// behind one C API.
    ///
    /// Backend selection is delegated to Prism's own priority logic
    /// (<c>prism_registry_acquire_best</c>). The legacy hand-rolled
    /// chain (NVDA → JAWS → SAPI) is gone — Prism evaluates which
    /// backend is available at init time. If the active backend
    /// disappears mid-session we rely on Prism's own internal
    /// handling; we don't try to second-guess it from C#.
    ///
    /// Same external contract as before:
    ///   - <c>interrupt=true</c>  → stop current speech, then say text
    ///   - <c>interrupt=false</c> → queue text after current speech
    ///   - <see cref="LastSpoken"/> tracks the most recent utterance
    ///     for R-key replay
    ///   - Speech is suppressed when the game window isn't foreground
    ///
    /// The native dependency is <c>prism.dll</c>, vendored from the
    /// Prismatoid NuGet package's <c>runtimes/win-x64/native/</c>
    /// folder. We can't use the managed Prismatoid wrapper directly
    /// because it targets <c>net10.0</c> only and the game runs on
    /// .NET Framework 4.7.2.
    /// </summary>
    public sealed class SpeechEngine : IDisposable
    {
        // PrismContext* + PrismBackend* from the C side. Backends
        // returned by `prism_registry_acquire_best` are owned by the
        // context (cached), so we don't free them individually — only
        // shut down the context on Dispose.
        private IntPtr _ctx;
        private IntPtr _backend;
        private string _backendName = "<none>";

        // Logger plumbed in by Main so we land in the same log file as
        // every other [Speech] / [Main] / [...] line instead of the
        // (invisible) game-process stdout.
        private readonly Action<string> _log;

        public SpeechEngine() : this(_ => { }) { }

        public SpeechEngine(Action<string> log)
        {
            _log = log ?? (_ => { });
            try
            {
                PrismNative.PrismConfig cfg = PrismNative.prism_config_init();
                _log($"[Speech] prism_config_init: version={cfg.Version}");

                _ctx = PrismNative.prism_init(ref cfg);
                if (_ctx == IntPtr.Zero)
                {
                    _log("[Speech] prism_init returned NULL context — no speech available.");
                    return;
                }
                _log($"[Speech] prism_init ok (ctx={_ctx.ToInt64():X})");

                _backend = PrismNative.prism_registry_acquire_best(_ctx);
                if (_backend == IntPtr.Zero)
                {
                    _log("[Speech] prism_registry_acquire_best returned NULL — no usable backend. Start NVDA / JAWS / etc.");
                    return;
                }

                // Backends returned by acquire_* are cached, shared
                // instances — Prism initialises them once on first
                // acquire. Calling initialize again returns
                // AlreadyInitialized, which is benign for us. Only
                // other errors are fatal.
                PrismNative.PrismError initErr = PrismNative.prism_backend_initialize(_backend);
                if (initErr != PrismNative.PrismError.Ok && initErr != PrismNative.PrismError.AlreadyInitialized)
                {
                    string errStr = PrismNative.Utf8Decode(PrismNative.prism_error_string(initErr));
                    _log($"[Speech] prism_backend_initialize failed: {initErr} ({errStr}) — no speech available.");
                    _backend = IntPtr.Zero;
                    return;
                }

                _backendName = PrismNative.Utf8Decode(PrismNative.prism_backend_name(_backend));
                _log($"[Speech] Prism ready. Backend: {_backendName}");
            }
            catch (DllNotFoundException)
            {
                _log("[Speech] prism.dll not found in game directory. No speech available.");
                _ctx = IntPtr.Zero;
                _backend = IntPtr.Zero;
            }
            catch (BadImageFormatException ex)
            {
                _log($"[Speech] prism.dll architecture mismatch: {ex.Message}");
                _ctx = IntPtr.Zero;
                _backend = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                _log($"[Speech] Prism init threw: {ex.GetType().Name}: {ex.Message}");
                _ctx = IntPtr.Zero;
                _backend = IntPtr.Zero;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The last non-empty string passed to Speak() (or
        /// recorded via <see cref="RecordSilent"/>). Used for R-key
        /// repeat.</summary>
        public string? LastSpoken { get; private set; }

        /// <summary>Update <see cref="LastSpoken"/> WITHOUT routing the
        /// text through any TTS backend. Use when the game already
        /// communicates the content another way (e.g. a voiced
        /// dialogue line where the screen reader stays silent) but we
        /// still want R-key replay to surface the text on demand.</summary>
        public void RecordSilent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            LastSpoken = text;
        }

        /// <summary>Speak text. interrupt=true stops current speech first.</summary>
        public void Speak(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Suppress speech when the game window is not the foreground
            // app (user has alt-tabbed away). LastSpoken still updates
            // so R-key replay works once focus returns.
            if (!IsGameWindowFocused())
            {
                LastSpoken = text;
                return;
            }
            LastSpoken = text;

            if (_backend == IntPtr.Zero) return;

            IntPtr p = PrismNative.Utf8Alloc(text);
            try
            {
                PrismNative.PrismError err = PrismNative.prism_backend_speak(_backend, p, interrupt);
                if (err != PrismNative.PrismError.Ok)
                {
                    string errStr = PrismNative.Utf8Decode(PrismNative.prism_error_string(err));
                    _log($"[Speech] Speak(\"{Trunc(text)}\") returned {err} ({errStr})");
                }
            }
            catch (Exception ex)
            {
                _log($"[Speech] Speak threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>Stop all current speech immediately.</summary>
        public void Stop()
        {
            if (_backend == IntPtr.Zero) return;
            try { PrismNative.prism_backend_stop(_backend); }
            catch { }
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero)
            {
                try { PrismNative.prism_shutdown(_ctx); }
                catch { }
            }
            _ctx = IntPtr.Zero;
            _backend = IntPtr.Zero;
        }

        // ── Foreground-window gate ────────────────────────────────────────────

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        private static readonly int _myPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        private static bool IsGameWindowFocused()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return (int)pid == _myPid;
            }
            catch { return true; } // fail open — better to speak than to go silent
        }

        private static string Trunc(string s, int max = 60)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
