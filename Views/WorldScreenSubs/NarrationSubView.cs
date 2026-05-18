using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Watches WorldScreen's supplementary <c>List&lt;string&gt;</c> log and
    /// surfaces the non-technical entries: character enter/exit events,
    /// wait phrases, and the in-session emotion meter ("Sentiment analyzer:
    /// hollow [neg]" → "hollow, negative"). Boot text and engine perf
    /// metrics are suppressed.
    ///
    /// Source: <c>WorldScreen.#=q3oxmFZMlPPZ5w7PNNfq4Xw==</c> — a List&lt;string&gt;
    /// the engine appends to over the course of a session. Items are
    /// strings; classification is by prefix (no engine-level category flag
    /// exists). Polling reads list[lastCount..count) each tick so we never
    /// re-speak entries.
    ///
    /// Gating: <c>IsActive</c> is the only filter. Outside a session the
    /// sub does not poll and the narration list is ignored — so the
    /// "bootloader replay on load" problem (the entire list being walked
    /// at startup) cannot happen even when the saved list is populated.
    /// Inside a session every appended line is fair game; on save-load
    /// mid-session the visible AR boot text is read out to give the
    /// player context for the session they just dropped into.
    ///
    /// Re-entry semantics: <c>_lastNarrationCount</c> is preserved across
    /// WorldScreen unfocus → re-focus and across session enter/leave, so
    /// menu trips and between-session walking don't re-speak past content.
    ///
    /// Emotion-meter toggle: F8 (keyboard) or R3 (right-stick click on
    /// controller) toggles <see cref="AnalyzerEnabled"/>. Some players find
    /// the per-keyword "hollow, negative" / "motivated, positive" stream
    /// during therapy distracting. When off, the analyzer-status branch
    /// short-circuits before speaking. R3 is the least-used button in the
    /// game so it works well as an accessibility shortcut.
    /// </summary>
    internal sealed class NarrationSubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        private const string NarrationListFieldName = "#=q3oxmFZMlPPZ5w7PNNfq4Xw==";

        // ── WorldScreen-level reflection (OnInit) ──────────────────────
        private FieldInfo? _narrationListField;

        // ── Per-activation state ───────────────────────────────────────
        // PlayerFacing covers everything visible on the AR console panel —
        // boot ROM, firmware, Skandha-connect, "Set phase:", and the
        // engine-instrumentation timings ("Forced reflow during execution",
        // "State change handler time", "Load handler time"). The
        // instrumentation lines are drawn on screen too (verified from the
        // HotspotLabel DRAW log) and fire once per discrete event, not per
        // frame, so speaking them matches the sighted-player experience.
        // AnalyzerStatus is kept as its own category because the F8/R3
        // emotion-meter toggle and back-to-back duplicate suppression apply
        // only to those sentiment/multimodal lines.
        private enum NarCategory { PlayerFacing, AnalyzerStatus }
        // Grows as we speak entries. Starts at 0 so the first activation
        // reads from index 0 — that's the visible AR boot text on a
        // fresh BeginTherapy and also on save-load mid-session.
        private int     _lastNarrationCount;
        private string? _lastAnalyzerSpoken;
        private bool    _narSourceLogged;
        private bool    _analyzerSourceLogged;

        // ── F8 / R3 toggle ─────────────────────────────────────────────
        private readonly KeyEdge _kbF8 = new KeyEdge(Keyboard.VK_F8);
        private bool _xinputUseFallback;
        private bool _r3ButtonReleased = true;

        /// <summary>Module-level toggle for the per-keyword emotion-meter
        /// readout. Defaults true; F8 / R3 flip it.</summary>
        public static bool AnalyzerEnabled = true;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _narrationListField = tWS.GetField(NarrationListFieldName, inst);
            if (_narrationListField == null)
                Log("[NarrationSub] narrationListField NOT FOUND — sub disabled");
        }

        // The supplementary narration list (AR headset boot text,
        // sentiment analyzer keywords, "Set phase:" events, etc.) is the
        // visible AR-console content drawn during a therapy session. It
        // is meaningless outside a session — and worse, the list is
        // persistent across the whole game, so when the player loads a
        // save the entire history is present and would replay if we
        // didn't gate. Activation tracks SessionState.IsActive (set by
        // SessionSubView for both formal and shadow sessions), AND
        // excludes Transparency Mode and in-VN texting overlays: those
        // are session-like but their UI fully covers the AR display, so
        // reading its content would be reading something the sighted
        // player can't see either.
        public override bool IsActive =>
            SessionState.IsActive
            && !TransparencyState.IsActive
            && !TextingState.IsActive;

        public override void OnEnter()
        {
            // No re-prime. The baseline set in OnInit already excluded
            // save-restored history; everything past that point is
            // legitimately new and should read when the session is
            // active. Re-priming here would silently swallow the
            // bootloader text that BeginTherapy appends in the same
            // frame it sets the session character (so the count has
            // already advanced by the time we see the transition).
            Log($"[NarrationSub] session entered (lastCount={_lastNarrationCount})");
        }

        public override void OnLeave()
        {
            Log($"[NarrationSub] session ended (lastCount={_lastNarrationCount})");
        }

        public override void OnPoll()
        {
            // While the sub is gated off — no active session, or
            // Transparency / in-VN texting overlay covering the AR
            // display — silently advance the read pointer to the end
            // of the list. The engine still writes narration entries
            // during this window (e.g., bootloader text added when
            // BeginTherapy fires for a therapy session that's running
            // concurrently with transparency); without this catch-up,
            // those entries pile up and the next IsActive=true tick
            // replays the whole batch as soon as the gate reopens.
            if (!IsActive && _narrationListField != null)
            {
                _lastNarrationCount = ReadCurrentListCount();
            }
        }

        private int ReadCurrentListCount()
        {
            if (_narrationListField == null) return 0;
            try
            {
                if (_narrationListField.GetValue(Parent) is IList list) return list.Count;
            }
            catch { }
            return 0;
        }

        public override void OnUpdate()
        {
            PollNarration();
            PollAnalyzerToggle();
        }

        private void PollNarration()
        {
            if (_narrationListField == null) return;

            try
            {
                IList? list = _narrationListField.GetValue(Parent) as IList;
                if (list == null) return;

                int count = list.Count;
                if (count < _lastNarrationCount)
                {
                    _lastNarrationCount = 0;
                    Log("[NarrationSub] list reset detected — restarting from 0");
                }
                if (count <= _lastNarrationCount) return;

                var lines = new List<string>();
                var cats  = new List<NarCategory>();
                int pfCount = 0;

                for (int i = _lastNarrationCount; i < count; i++)
                {
                    string? raw = list[i] as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    if (raw!.TrimStart().StartsWith("#=q")) continue;
                    string line = raw.Trim();
                    NarCategory cat = ClassifyLine(line);
                    lines.Add(line);
                    cats.Add(cat);
                    if (cat == NarCategory.PlayerFacing) pfCount++;
                }

                if (pfCount > 1)
                    Log($"[NarrationSub] batch detected: count={pfCount}");

                for (int j = 0; j < lines.Count; j++)
                {
                    string      line = lines[j];
                    NarCategory cat  = cats[j];

                    if (cat == NarCategory.AnalyzerStatus)
                    {
                        if (!_analyzerSourceLogged)
                        {
                            _analyzerSourceLogged = true;
                            Log("[NarrationSub] category observed: analyzer (in-session emotion meter)");
                        }
                        if (!AnalyzerEnabled)
                        {
                            Log($"[NarrationSub] analyzer suppressed (toggled off): \"{Trunc(line, 40)}\"");
                            continue;
                        }
                        // Speak the line exactly as the AR display shows it
                        // ("Sentiment analyzer: hollow [neg]") rather than
                        // reformatting to a shorter spoken form. Matching
                        // the visible text is the design goal.
                        if (line == _lastAnalyzerSpoken)
                        {
                            Log($"[NarrationSub] analyzer duplicate suppressed: \"{Trunc(line, 40)}\"");
                            continue;
                        }
                        _lastAnalyzerSpoken = line;
                        Speech.Speak(line);
                        Log($"[NarrationSub] analyzer spoken: \"{Trunc(line, 60)}\"");
                        continue;
                    }

                    if (!_narSourceLogged)
                    {
                        _narSourceLogged = true;
                        Log("[NarrationSub] category observed: supplementary (enter/exit, wait phrases)");
                    }
                    Speech.Speak(line);
                    Log($"[NarrationSub] supplementary spoken: \"{Trunc(line, 60)}\"");
                }

                _lastNarrationCount = count;
            }
            catch (Exception ex)
            {
                Log($"[NarrationSub] PollNarration error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void PollAnalyzerToggle()
        {
            bool f8Rising = _kbF8.Pressed();
            bool r3Now    = IsR3Pressed();
            bool r3Rising = r3Now && _r3ButtonReleased;
            _r3ButtonReleased = !r3Now;

            if (f8Rising || r3Rising)
            {
                AnalyzerEnabled = !AnalyzerEnabled;
                string state = AnalyzerEnabled ? "on" : "off";
                Speech.Speak($"Emotion meter {state}.");
                Log($"[NarrationSub] emotion-meter toggle — now {state} (source={(f8Rising ? "F8" : "R3")})");
            }
        }

        // ── Classification helpers ─────────────────────────────────────

        private static NarCategory ClassifyLine(string line)
        {
            if (line.StartsWith("Sentiment analyzer:")) return NarCategory.AnalyzerStatus;
            if (line.StartsWith("Multimodal sense:"))   return NarCategory.AnalyzerStatus;
            return NarCategory.PlayerFacing;
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";

        // ── XInput R3 polling ──────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte   bLeftTrigger;
            public byte   bRightTrigger;
            public short  sThumbLX;
            public short  sThumbLY;
            public short  sThumbRX;
            public short  sThumbRY;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState14(int dwUserIndex, ref XINPUT_STATE pState);
        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState13(int dwUserIndex, ref XINPUT_STATE pState);
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;

        private bool IsR3Pressed()
        {
            try
            {
                var st = default(XINPUT_STATE);
                int rc;
                if (_xinputUseFallback)
                {
                    rc = XInputGetState13(0, ref st);
                }
                else
                {
                    try { rc = XInputGetState14(0, ref st); }
                    catch (DllNotFoundException)
                    {
                        _xinputUseFallback = true;
                        rc = XInputGetState13(0, ref st);
                    }
                }
                if (rc != 0) return false;
                return (st.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0;
            }
            catch { return false; }
        }
    }
}
