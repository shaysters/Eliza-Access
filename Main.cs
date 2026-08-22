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

            // Localization: load the English table now (embedded, no
            // game state needed) and detect the current language.
            // Language detection may return English until the game's
            // GameLogic singleton exists; the per-tick RefreshLanguage
            // in the poll loop below picks up the real language once
            // it does, and re-loads on any mid-session change.
            Loc.Init(_log);

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

            // Mod settings overlay (F5). Polled every tick from any
            // game state — see ModSettingsMenu for the open/close +
            // input-suppression flow.
            ModSettingsMenu modSettings = new ModSettingsMenu(speech, _log);

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

            // F10: dump every exploration-entry icon across every
            // ShowExploration script step to PNGs under
            // {logdir}/TextureDumps/ExplorationIcons/. One-shot; same
            // render-thread piggyback the email dump uses.
            KeyEdge kbF10 = new KeyEdge(Keyboard.VK_F10);

            // F11: dump every location background and character sprite
            // (base + per-location variants + phone portraits) from the
            // static script registries, for the audio-description pass.
            // One-shot; same render-thread piggyback as the other dumps.
            KeyEdge kbF11 = new KeyEdge(Keyboard.VK_F11);

            // Q: on-demand review of the three Transparency-Mode
            // conclusion statements (with current Agreed/Disagreed
            // state). Polled globally rather than from
            // HomeGridSubView because the player has to leave the
            // inline phone home grid to read each email/text, and a
            // SubView-scoped Q would be silently dead during those
            // detours. TransparencyState gates the announcement —
            // outside Transparency Mode it speaks the "not active"
            // line so a stray Q keypress doesn't read as a broken
            // key.
            KeyEdge kbQ = new KeyEdge(Keyboard.VK_Q);

            _log("[Main] Entering poll loop (~200 Hz).");

            while (true)
            {
                // Read all 256 VK states ONCE into the shared snapshot.
                // Every KeyEdge.Pressed and the InputInterrupter sweep
                // read from this cache, so the GetAsyncKeyState 0x0001
                // bit is consumed exactly once per tick instead of
                // racing between multiple callers (which silently lost
                // taps — the dominant cause of "I pressed Q and nothing
                // happened" / lost arrow taps in fast nav).
                KeyboardSnapshot.Refresh();

                // Re-check the game language every tick so mod strings
                // flip in the same tick the player changes it. The
                // detection is a couple of reflection reads (~µs); the
                // table reload only happens on an actual change.
                try   { Loc.RefreshLanguage(); }
                catch (Exception ex)
                    { _log($"[Main] Loc refresh exception: {ex.GetType().Name}: {ex.Message}"); }

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

                try   { modSettings.Poll(); }
                catch (Exception ex)
                    { _log($"[Main] modSettings poll exception: {ex.GetType().Name}: {ex.Message}"); }

                try   { screens.Poll(); }
                catch (Exception ex)
                    { _log($"[Main] screens poll exception: {ex.GetType().Name}: {ex.Message}"); }

                // Q / F10 / F11 are polled AFTER interrupter.Poll() so
                // the interrupter's Stop() (fired by the hotkey's own
                // rising edge) doesn't wipe the announcement we're
                // about to queue. Same rule the SubView path relied on
                // implicitly via Drive() running after interrupter.
                // (F10/F11 originally ran before the interrupter and
                // their announcements were silently wiped every time.)
                if (kbQ.Pressed())
                {
                    speech.Speak(TransparencyState.BuildReviewAnnouncement(),
                                 interrupt: true);
                }

                // if (kbF9.Pressed())
                // {
                //     TextureDumper.DumpRequested = true;
                //     speech.Speak("Dumping email images.");
                // }

                if (kbF10.Pressed())
                {
                    TextureDumper.DumpExplorationRequested = true;
                    speech.Speak("Dumping exploration icons.");
                }

                if (kbF11.Pressed())
                {
                    TextureDumper.DumpLocationsCharactersRequested = true;
                    speech.Speak("Dumping location and character images.");
                }

                // The loc/char dump finishes asynchronously on the
                // render thread; it leaves its completion message here
                // for the speech thread to pick up.
                string? dumpDone = TextureDumper.DoneAnnouncement;
                if (dumpDone != null)
                {
                    TextureDumper.DoneAnnouncement = null;
                    speech.Speak(dumpDone);
                }

                Thread.Sleep(5);
            }
        }

    }
}
