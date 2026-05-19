using System;
using System.IO;
using System.Threading;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;
using ElizaAccess.Views;

namespace ElizaAccess
{
    /// <summary>
    /// Mod entry point and main loop. Constructs the speech engine,
    /// applies Harmony patches once Nagoya loads, registers every
    /// <see cref="ScreenView"/> with the manager, and polls the input
    /// interrupter + screen manager at ~200 Hz for the rest of the
    /// process lifetime.
    ///
    /// Design constraints:
    /// - No broad screen scanning beyond what registered views need.
    /// - Logs only on meaningful change (assembly found, screen
    ///   transitions, errors). No per-poll spam.
    /// </summary>
    internal sealed class Main
    {
        private readonly string _logPath;
        private readonly Action<string> _log;

        public Main(string logPath)
        {
            _logPath = logPath;
            _log     = msg =>
            {
                try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); }
                catch { }
            };
        }

        public void Run()
        {
            _log("[Main] Run started.");

            SpeechEngine speech = new SpeechEngine(_log);

            // Wait for Nagoya to appear in the AppDomain before initializing observers.
            if (!Nagoya.WaitForLoad(timeoutMs: 30_000))
            {
                _log("[Main] Nagoya assembly not found within timeout — exiting.");
                return;
            }

            _log("[Main] Nagoya found. Bootstrapping Harmony.");
            HarmonyBootstrap.TryApplyAll(_logPath, _log);

            // Every screen is owned by a view registered below. The
            // manager mirrors the game's ScreenStack and dispatches
            // lifecycle hooks (added/focused/updated/unfocused/removed)
            // to the matching view.
            ScreenViewManager screens = new ScreenViewManager(_log, speech);
            screens.Register<TitleMenuView>("TitleScreen");
            screens.Register<EscapeMenuView>("EscapeMenuScreen");
            screens.Register<WorldScreenView>("WorldScreen");
            screens.Register<SettingsView>("SettingsScreen");
            screens.Register<FakeAppScreenView>("FakeAppScreen");
            screens.Register<PhoneScreenView>("PhoneScreen");
            screens.Register<ConfirmDialogView>(ConfirmDialogView.DialogTypeName);
            screens.Register<CreditsView>("CreditsScreen");
            screens.Register<SolitaireView>("SolitaireScreen");
            screens.Register<ChapterSelectView>(ChapterSelectView.ChapterScreenTypeName);
            screens.Register<HistoryView>("HistoryScreen");

            // Single source of speech-queue interruption. Polled
            // before views so any new announcement they queue this
            // iteration arrives in an empty queue.
            InputInterrupter interrupter = new InputInterrupter(speech);

            // R-key global repeat. Polled AFTER the interrupter so the
            // repeat queues into the now-empty queue.
            SpeechRepeater repeater = new SpeechRepeater(speech);

            // Texture dumper: F9 triggers a one-shot dump of the
            // email-image dictionary (GClass127.dictionary_0) to PNG
            // files under {logdir}/TextureDumps/. Used once to
            // produce visual descriptions for the screen-reader image
            // announcements (now in EmailImageDescriptions). Hotkey
            // is commented out so players don't trigger a dump by
            // accident; the infrastructure stays in place for future
            // uses (e.g. if Zachtronics adds new image tokens). To
            // re-enable: uncomment the two `kbF9` lines plus the
            // Pressed() block inside the loop.
            TextureDumper.LogPath = _logPath;
            TextureDumper.OutputDir = Path.Combine(
                Path.GetDirectoryName(_logPath) ?? ".", "TextureDumps");
            // KeyEdge kbF9 = new KeyEdge(Keyboard.VK_F9);

            _log("[Main] Entering poll loop (~200 Hz).");

            while (true)
            {
                // if (kbF9.Pressed())
                // {
                //     TextureDumper.DumpRequested = true;
                //     speech.Speak("Dumping email images.");
                // }
                // Keep IsControllerActive pinned true. The Harmony prefix on
                // VirtualInput.smethod_1 doesn't catch flips because the
                // setter is inlined by the JIT; the field-rewrite here is
                // the actual mode lock.
                ControllerModeLock.Hold(_logPath);

                try   { interrupter.Poll(); }
                catch (Exception ex)
                    { _log($"[Main] interrupter poll exception: {ex.GetType().Name}: {ex.Message}"); }

                try   { repeater.Poll(); }
                catch (Exception ex)
                    { _log($"[Main] repeater poll exception: {ex.GetType().Name}: {ex.Message}"); }

                try   { screens.Poll(); }
                catch (Exception ex)
                    { _log($"[Main] screens poll exception: {ex.GetType().Name}: {ex.Message}"); }

                Thread.Sleep(5);
            }
        }

    }
}
