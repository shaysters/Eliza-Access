using System;
using System.Collections.Generic;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Mod-owned settings overlay. Polled from <see cref="Main"/>'s
    /// loop on every tick. Press F7 anywhere in the game to open;
    /// arrows navigate, Enter/Space toggle / drill into a submenu,
    /// Escape goes up one level (closes from the root).
    ///
    /// Lives outside the <see cref="ScreenView"/> + <see cref="SubView"/>
    /// world because the menu isn't tied to any game screen — players
    /// should be able to reach it from a session, the title menu, the
    /// phone, anywhere. While open it claims the keyboard via
    /// <see cref="InputSuppressor"/> AND globally suppresses every
    /// other <see cref="KeyEdge"/> via
    /// <see cref="KeyEdge.SetGlobalSuppress"/> so the underlying
    /// game screen + any sub-view that drives its own nav (e.g.
    /// ExplorationSubView's Up/Down) don't also react.
    ///
    /// Items are organised into submenus that mirror the
    /// <see cref="ModConfig"/> section structure. Toggling an item
    /// flips the property and immediately persists via
    /// <see cref="ModConfig.Save"/> so a crash or alt-F4 can't lose
    /// the change.
    /// </summary>
    internal sealed class ModSettingsMenu
    {
        private readonly SpeechEngine   _speech;
        private readonly Action<string> _log;
        private readonly KeyEdge _kbF7     = new KeyEdge(Keyboard.VK_F7);
        private readonly KeyEdge _kbUp     = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown   = new KeyEdge(Keyboard.VK_DOWN);
        private readonly KeyEdge _kbEnter  = new KeyEdge(Keyboard.VK_RETURN);
        private readonly KeyEdge _kbSpace  = new KeyEdge(Keyboard.VK_SPACE);
        private readonly KeyEdge _kbEscape = new KeyEdge(Keyboard.VK_ESCAPE);

        private bool _active;
        private bool _inputSuppressed;

        // Navigation: trail of (parentSubmenu, parentIndex) frames so
        // backing out of a drilled submenu restores focus to the entry
        // we came from. Top of stack is the IMMEDIATE parent; current
        // submenu is _current.
        private readonly Stack<(Submenu menu, int idx)> _trail
            = new Stack<(Submenu, int)>();
        private Submenu _current;
        private int     _navIdx;
        private readonly Submenu _root;

        private sealed class Submenu
        {
            public string         Title;
            public List<MenuItem> Items;
            public Submenu(string title, List<MenuItem> items)
            {
                Title = title;
                Items = items;
            }
        }

        private struct MenuItem
        {
            public string     Label;
            // Either Submenu is non-null (drill-in entry) OR
            // Get + Toggle are non-null (toggleable setting). Never
            // both, never neither.
            public Submenu?   Submenu;
            public Func<bool>? Get;
            public Action?     Toggle;
        }

        public ModSettingsMenu(SpeechEngine speech, Action<string> log)
        {
            _speech = speech;
            _log    = log;
            _root   = BuildRoot();
            _current = _root;

            // Mark our own edges as exempt from the global suppression
            // we flip on while open. Without this, the overlay would
            // also gate its own input and the menu would freeze.
            _kbF7.BypassGlobalSuppress     = true;
            _kbUp.BypassGlobalSuppress     = true;
            _kbDown.BypassGlobalSuppress   = true;
            _kbEnter.BypassGlobalSuppress  = true;
            _kbSpace.BypassGlobalSuppress  = true;
            _kbEscape.BypassGlobalSuppress = true;
        }

        // ── Menu definition ──────────────────────────────────────────

        private static Submenu BuildRoot()
        {
            // One submenu per ModConfig section. Add a section here
            // when ModConfig grows a new top-level group.
            var elizaSessions = new Submenu("Eliza sessions", new List<MenuItem>
            {
                Toggle("Read bootup text",
                    () => ModConfig.ElizaSessions.ReadBootupText,
                    () => ModConfig.ElizaSessions.ReadBootupText
                       = !ModConfig.ElizaSessions.ReadBootupText),
                Toggle("Read sentiment analyzer",
                    () => ModConfig.ElizaSessions.ReadSentimentAnalyzer,
                    () => ModConfig.ElizaSessions.ReadSentimentAnalyzer
                       = !ModConfig.ElizaSessions.ReadSentimentAnalyzer),
                Toggle("Read multimodal sense",
                    () => ModConfig.ElizaSessions.ReadMultimodalSense,
                    () => ModConfig.ElizaSessions.ReadMultimodalSense
                       = !ModConfig.ElizaSessions.ReadMultimodalSense),
                Toggle("Read phase changes",
                    () => ModConfig.ElizaSessions.ReadSetPhase,
                    () => ModConfig.ElizaSessions.ReadSetPhase
                       = !ModConfig.ElizaSessions.ReadSetPhase),
                Toggle("Read miscellaneous narration",
                    () => ModConfig.ElizaSessions.ReadMisc,
                    () => ModConfig.ElizaSessions.ReadMisc
                       = !ModConfig.ElizaSessions.ReadMisc),
            });

            return new Submenu("Mod settings", new List<MenuItem>
            {
                SubmenuEntry("Eliza sessions", elizaSessions),
            });
        }

        private static MenuItem Toggle(string label, Func<bool> get, Action toggle)
            => new MenuItem { Label = label, Get = get, Toggle = toggle };

        private static MenuItem SubmenuEntry(string label, Submenu sub)
            => new MenuItem { Label = label, Submenu = sub };

        // ── Poll ─────────────────────────────────────────────────────

        public void Poll()
        {
            // F7 toggles open/closed from any state. We handle the
            // edge BEFORE the other key checks so pressing F7 while
            // already open closes cleanly, even if Up/Down/Enter
            // happen to land on the same tick.
            if (_kbF7.Pressed())
            {
                if (_active) Close();
                else         Open();
                return;
            }

            if (!_active) return;

            // While the menu is open, drain every other key edge each
            // poll so the engine's still-pressed state doesn't leak
            // out as a queued action the moment we close.
            bool up    = _kbUp.Pressed();
            bool down  = _kbDown.Pressed();
            bool enter = _kbEnter.Pressed();
            bool space = _kbSpace.Pressed();
            bool esc   = _kbEscape.Pressed();

            if (esc)            { GoBack(); return; }
            if (down)           { Move(+1); }
            if (up)             { Move(-1); }
            if (enter || space) { Activate(); }
        }

        // ── Lifecycle ─────────────────────────────────────────────────

        private void Open()
        {
            _active = true;
            _current = _root;
            _trail.Clear();
            _navIdx = 0;
            SuppressGameInput();
            // Block every other observer's KeyEdge from firing while
            // we're open — otherwise Up/Down/Enter pressed in the
            // menu would also drive ExplorationSubView's nav,
            // SolitaireView, HistoryView, etc.
            KeyEdge.SetGlobalSuppress(true);
            _kbUp.Reset();
            _kbDown.Reset();
            _kbEnter.Reset();
            _kbSpace.Reset();
            _kbEscape.Reset();
            _log("[ModSettings] opened");
            SpeakSubmenuEntry();
        }

        private void Close()
        {
            _active = false;
            ReleaseGameInput();
            KeyEdge.SetGlobalSuppress(false);
            ModConfig.Save();
            _log("[ModSettings] closed (saved)");
            _speech.Speak("Mod settings closed.");
        }

        // ── Navigation + activation ───────────────────────────────────

        private void Move(int delta)
        {
            int newIdx = _navIdx + delta;
            if (newIdx < 0)                       newIdx = 0;
            if (newIdx >= _current.Items.Count)   newIdx = _current.Items.Count - 1;
            if (newIdx == _navIdx) return;
            _navIdx = newIdx;
            SpeakFocused();
            _log($"[ModSettings] focus → [{_navIdx}] \"{_current.Items[_navIdx].Label}\"");
        }

        private void Activate()
        {
            var item = _current.Items[_navIdx];
            if (item.Submenu != null)
            {
                // Drill into submenu. Remember the entry we came from
                // so Escape can restore focus.
                _trail.Push((_current, _navIdx));
                _current = item.Submenu;
                _navIdx  = 0;
                _log($"[ModSettings] drill → \"{_current.Title}\"");
                SpeakSubmenuEntry();
                return;
            }
            if (item.Toggle == null) return;
            try { item.Toggle(); }
            catch (Exception ex)
            {
                _log($"[ModSettings] toggle threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            // Persist immediately — a crash before Close() shouldn't
            // lose the user's change.
            ModConfig.Save();
            SpeakFocused();
            _log($"[ModSettings] toggled [{_navIdx}] \"{item.Label}\" → {item.Get!()}");
        }

        private void GoBack()
        {
            if (_trail.Count == 0)
            {
                // At root — Escape closes.
                Close();
                return;
            }
            var (parent, parentIdx) = _trail.Pop();
            _current = parent;
            _navIdx  = parentIdx;
            _log($"[ModSettings] back → \"{_current.Title}\" [{_navIdx}]");
            SpeakSubmenuEntry();
        }

        // ── Speech ────────────────────────────────────────────────────

        // Used when we just entered a new submenu (open, drill-in, or
        // pop-back). Speaks "[submenu title], [focused item]" so the
        // player simultaneously hears where they are and what's
        // selected.
        private void SpeakSubmenuEntry()
        {
            string itemText = BuildItemText(_current.Items[_navIdx]);
            _speech.Speak($"{_current.Title}, {itemText}");
        }

        // Used when nav changes within the current submenu — no
        // title prefix, just the new item.
        private void SpeakFocused()
        {
            _speech.Speak(BuildItemText(_current.Items[_navIdx]));
        }

        private static string BuildItemText(MenuItem item)
        {
            if (item.Submenu != null) return $"{item.Label}, submenu";
            // Toggle: CheckboxElement composes "{label}, checkbox,
            // checked / unchecked" via the shared
            // AnnouncementComposer so the menu speaks in the same
            // form as every other UI surface.
            var element = new CheckboxElement(item.Label, item.Get!);
            return element.ResolveFocusText();
        }

        // ── Game-input suppression ────────────────────────────────────

        private void SuppressGameInput()
        {
            if (_inputSuppressed) return;
            InputSuppressor.SuppressAxis(InputSuppressor.AxisHorizontal);
            InputSuppressor.SuppressAxis(InputSuppressor.AxisVertical);
            InputSuppressor.SuppressButton(InputSuppressor.ButtonConfirmA);
            InputSuppressor.SuppressButton(InputSuppressor.ButtonBackB);
            _inputSuppressed = true;
        }

        private void ReleaseGameInput()
        {
            if (!_inputSuppressed) return;
            InputSuppressor.ReleaseAxis(InputSuppressor.AxisHorizontal);
            InputSuppressor.ReleaseAxis(InputSuppressor.AxisVertical);
            InputSuppressor.ReleaseButton(InputSuppressor.ButtonConfirmA);
            InputSuppressor.ReleaseButton(InputSuppressor.ButtonBackB);
            _inputSuppressed = false;
        }
    }
}
