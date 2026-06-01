using System;
using System.Collections;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Reads the HistoryScreen — the scrollable chat-log of every
    /// dialogue line so far. Line-by-line navigation: each Up/Down
    /// arrow tap steps the read cursor to the previous/next
    /// DialogueLine entry and speaks it.
    ///
    /// The earlier "watch the scroll position, announce whatever
    /// DL is at the bottom of the visible window" design was clunky
    /// and missed lines when the engine's continuous scroll fired
    /// between our settle ticks. Pure line-step nav is deterministic:
    /// one tap = one announcement = one position.
    ///
    /// Vertical axis is suppressed via <see cref="InputSuppressor"/>
    /// for the duration of this screen so the engine's own arrow-to-
    /// scroll path can't fight us. We drive the scroller manually
    /// via <c>Scroller.SetPosition</c> so sighted observers see the
    /// visual roughly track the current line.
    ///
    /// HistoryScreen has no clickable hotspots so
    /// <see cref="ReadCurrentFocus"/> returns null and all work
    /// happens in <see cref="OnUpdate"/>.
    /// </summary>
    internal sealed class HistoryView : ScreenView
    {
        public override string ScreenTypeName => "HistoryScreen";

        private const int   EntryDelayMs = 600;
        // Approximate pixel height of one DialogueLine entry — used to
        // keep the visual scroll position roughly aligned with the
        // line the user is currently hearing. Doesn't need to be
        // exact; the blind player doesn't see it and a sighted
        // observer just wants the right region on screen.
        private const float ScrollStepPx = 150f;

        // ── Obfuscated names ───────────────────────────────────────────
        // History entry (the GClassNN tagged-union of BeginChapter /
        // DialogueLine / ChangeLocation / etc.).
        private const string EntryTypeName            = "#=q1muqt4iu1aIc3Rbuma0kRw==";
        private const string EntryIsBeginChapter     = "#=qNgDO12XfsqxNn6fnFFEYNg==";
        private const string EntryIsDialogueLine     = "#=qLHNTZiGg21EZGO6mWYZgkg==";
        private const string EntryIsChangeLocation   = "#=q0W43iMQUSgvtVhz9ebfVNXa4ZOGmLNFkx4GcWpq3o2k=";
        private const string EntryGetDialogueLine    = "#=qxntq2yBWxWAd$Mi7BRhsDQ==";

        private const string DialogueLineSpeakerField = "#=q0opeQa51ufYxr7JOKJ$S5A==";
        private const string DialogueLineTextField    = "#=qtyrJpA5pn9a39IUBdZVt5A==";
        private const string ScriptCharacterNameField = "#=qVcZOuQei28m8v7C2MtsdYA==";

        private const string HistoryListField        = "#=qs8WfAVpAvgpN3uS$ouSByQ==";
        private const string ScrollerField           = "#=qHPFzw1no_xGjBzJqEvK88Q==";
        private const string ScrollerMaxScrollPos    = "#=quAL7IZ33NpnwZJ2VYIJVeytYhk$3KNu_bGSWclHYG0s=";
        private const string ScrollerSetPosition     = "#=qwp_MZlk8oypfEboUc0lURxGhlbj3CHrDYsyDG83oeJs=";
        private const string ScrollCacheField        = "#=qQUvvA6SIp0Z4BhvuMmKuNTHGjoAP$HUJygZrv6FLde8=";

        // ── Reflected handles ──────────────────────────────────────────
        private MethodInfo? _isDialogueLine;
        private MethodInfo? _getDialogueLine;
        private FieldInfo?  _dlSpeakerField;
        private FieldInfo?  _dlTextField;
        private FieldInfo?  _speakerNameField;
        private FieldInfo?  _historyListField;
        private FieldInfo?  _scrollerField;
        private FieldInfo?  _scrollCacheField;
        private MethodInfo? _maxScrollPosMethod;
        private MethodInfo? _setPositionMethod;

        // ── Per-activation state ───────────────────────────────────────
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);

        private bool     _entrySpeechPending;
        private DateTime _entryTime;
        // Absolute index into the history list. Always points at a
        // DialogueLine entry (or -1 when the list has none).
        private int _currentDLIndex = -1;
        // True while we've claimed exclusive vertical-axis control —
        // tracked so OnUnfocus / OnRemoved release exactly once.
        private bool _axisSuppressed;

        protected override void OnAdded(object screen)
        {
            const BindingFlags inst    = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags privInst = BindingFlags.NonPublic | BindingFlags.Instance;

            Type? tEntry = Nagoya.FindType(EntryTypeName);
            if (tEntry != null)
            {
                _isDialogueLine   = tEntry.GetMethod(EntryIsDialogueLine,   inst);
                _getDialogueLine  = tEntry.GetMethod(EntryGetDialogueLine,  inst);
                if (_getDialogueLine != null)
                {
                    Type dlType = _getDialogueLine.ReturnType;
                    _dlSpeakerField = dlType.GetField(DialogueLineSpeakerField, inst);
                    _dlTextField    = dlType.GetField(DialogueLineTextField,    inst);
                    if (_dlSpeakerField != null)
                        _speakerNameField = _dlSpeakerField.FieldType.GetField(ScriptCharacterNameField, inst);
                }
            }

            Type tScreen = screen.GetType();
            _historyListField = tScreen.GetField(HistoryListField, privInst);
            _scrollerField    = tScreen.GetField(ScrollerField,    privInst);
            _scrollCacheField = tScreen.GetField(ScrollCacheField, privInst);
            if (_scrollerField != null)
            {
                Type st = _scrollerField.FieldType;
                _maxScrollPosMethod = st.GetMethod(ScrollerMaxScrollPos, inst);
                _setPositionMethod  = st.GetMethod(ScrollerSetPosition,  inst);
            }

            Log($"[HistoryView] added: dl-method={(_isDialogueLine != null && _getDialogueLine != null)} " +
                $"dl-fields={(_dlSpeakerField != null && _dlTextField != null)} " +
                $"list={(_historyListField != null)} scroll={(_scrollerField != null)} " +
                $"setPos={(_setPositionMethod != null)}");
        }

        protected override void OnFocus()
        {
            if (ScreenInstance == null) return;
            SuppressAxis();
            // Start at the most recent dialogue line so the player is
            // dropped where the conversation left off — same place a
            // sighted player's eyes land when the screen opens.
            _currentDLIndex = FindLastDL();
            ScrollToCurrent();
            // Defer the entry speech a bit so any prior "History"
            // label (the pause-menu focus announcement) finishes first.
            _entrySpeechPending = true;
            _entryTime          = DateTime.Now;
            _kbUp.Reset(); _kbDown.Reset();
        }

        protected override void OnUnfocus()
        {
            ReleaseAxis();
        }

        protected override void OnUpdate()
        {
            if (ScreenInstance == null) return;

            // (1) Deferred entry speech: hint + current line.
            if (_entrySpeechPending
                && (DateTime.Now - _entryTime).TotalMilliseconds >= EntryDelayMs)
            {
                _entrySpeechPending = false;
                string hint     = Loc.Get("history.entry_hint");
                string lineText = BuildLineSpeech(_currentDLIndex);
                string text     = string.IsNullOrEmpty(lineText) ? hint : hint + " " + lineText;
                Speech.Speak(text);
                Log($"[HistoryView] entry: dl={_currentDLIndex} \"{Trunc(lineText, 80)}\"");
            }

            // (2) Up/Down step. Rising edges only — no hold-to-scroll.
            bool up   = _kbUp.Pressed();
            bool down = _kbDown.Pressed();
            if (!up && !down) return;

            int next = up
                ? FindAdjacentDL(_currentDLIndex, -1)
                : FindAdjacentDL(_currentDLIndex, +1);
            if (next < 0)
            {
                // No further line in that direction — speak the edge
                // hint so the user knows they've hit a boundary
                // rather than thinking the key didn't register.
                Speech.Speak(Loc.Get(up ? "history.at_top" : "history.at_bottom"));
                Log($"[HistoryView] {(up ? "up" : "down")} → no further DL");
                return;
            }
            _currentDLIndex = next;
            ScrollToCurrent();
            string speech = BuildLineSpeech(_currentDLIndex);
            if (!string.IsNullOrEmpty(speech))
            {
                Speech.Speak(speech);
                Log($"[HistoryView] {(up ? "up" : "down")} → dl={_currentDLIndex}: \"{Trunc(speech, 80)}\"");
            }
        }

        protected override (object? key, string? label) ReadCurrentFocus() => (null, null);

        // ── Vertical axis suppression lifecycle ──────────────────────

        private void SuppressAxis()
        {
            if (_axisSuppressed) return;
            InputSuppressor.SuppressAxis(InputSuppressor.AxisVertical);
            _axisSuppressed = true;
        }

        private void ReleaseAxis()
        {
            if (!_axisSuppressed) return;
            InputSuppressor.ReleaseAxis(InputSuppressor.AxisVertical);
            _axisSuppressed = false;
        }

        // ── List discovery ────────────────────────────────────────────

        private int FindLastDL()
        {
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return -1;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object? entry = list[i];
                if (entry != null && InvokeBool(_isDialogueLine, entry)) return i;
            }
            return -1;
        }

        private int FindAdjacentDL(int currentIndex, int direction)
        {
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return -1;
            if (currentIndex < 0)
            {
                // No anchor yet — first up/down lands on the last or
                // first DL respectively.
                return direction < 0 ? FindLastDL() : FindFirstDL(list);
            }
            for (int i = currentIndex + direction; i >= 0 && i < list.Count; i += direction)
            {
                object? entry = list[i];
                if (entry != null && InvokeBool(_isDialogueLine, entry)) return i;
            }
            return -1;
        }

        private int FindFirstDL(IList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                object? entry = list[i];
                if (entry != null && InvokeBool(_isDialogueLine, entry)) return i;
            }
            return -1;
        }

        // ── Speech building ───────────────────────────────────────────

        private string BuildLineSpeech(int dlIndex)
        {
            if (dlIndex < 0 || _getDialogueLine == null) return string.Empty;
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return string.Empty;
            if (dlIndex >= list.Count) return string.Empty;
            object? entry = list[dlIndex];
            if (entry == null) return string.Empty;
            try
            {
                object? dl = _getDialogueLine.Invoke(entry, null);
                if (dl == null) return string.Empty;
                string text = "";
                if (_dlTextField != null) text = _dlTextField.GetValue(dl)?.ToString() ?? "";
                if (string.IsNullOrEmpty(text)) return string.Empty;
                string speaker = "";
                if (_dlSpeakerField != null && _speakerNameField != null)
                {
                    object? sp = _dlSpeakerField.GetValue(dl);
                    if (sp != null) speaker = _speakerNameField.GetValue(sp)?.ToString() ?? "";
                }
                return string.IsNullOrEmpty(speaker)
                    ? text
                    : Loc.Fmt("dialogue.line", speaker, text);
            }
            catch { return string.Empty; }
        }

        // ── Visual scroll alignment ──────────────────────────────────

        // Move the engine's scroller toward the current DL. The
        // mapping from "DL index N" to "scroll position P" is non-
        // trivial (entries have variable heights), so we approximate:
        // step the scroller by a fixed delta in the navigation
        // direction. Good enough for sighted observers — the blind
        // player doesn't see the scroll anyway.
        private void ScrollToCurrent()
        {
            if (_scrollerField == null || _setPositionMethod == null) return;
            float current = ReadScrollPos();
            if (current == float.MinValue) return;
            float maxScroll = ReadMaxScrollPos();
            if (maxScroll <= 0f) return;

            // Direction inferred from how _currentDLIndex moved
            // relative to scroll: bigger index = further toward
            // bottom = higher scroll value. Without a previous
            // index to compare against on entry, just clamp.
            // (Up/Down callers already moved _currentDLIndex; we
            // can read it but we don't know the delta sign here.)
            // Simpler: do nothing extra and let SetPosition clamp.
            // The Up/Down handler calls this AFTER changing index,
            // so we apply a step in the direction of motion.
            // Track last index to know direction; if not yet set,
            // jump to a position proportional to (index / count).
            float target = ProportionalScrollFor(_currentDLIndex, maxScroll);
            if (target < 0f) return;
            try
            {
                object? scroller = _scrollerField.GetValue(ScreenInstance);
                if (scroller == null) return;
                _setPositionMethod.Invoke(scroller, new object[] { target });
            }
            catch (Exception ex)
            {
                Log($"[HistoryView] scroll error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Map "I'm at DL index N out of M total entries" to a scroll
        // position. Linear proportional mapping — close enough that
        // the visual region around the current line is on-screen.
        private float ProportionalScrollFor(int dlIndex, float maxScroll)
        {
            if (dlIndex < 0) return 0f;
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return 0f;
            if (list.Count <= 1) return 0f;
            float t = (float)dlIndex / (list.Count - 1);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return t * maxScroll;
        }

        private float ReadScrollPos()
        {
            if (_scrollCacheField == null || ScreenInstance == null) return float.MinValue;
            try
            {
                object? v = _scrollCacheField.GetValue(ScreenInstance);
                return v == null ? float.MinValue : Convert.ToSingle(v);
            }
            catch { return float.MinValue; }
        }

        private float ReadMaxScrollPos()
        {
            if (_scrollerField == null || _maxScrollPosMethod == null || ScreenInstance == null) return 0f;
            try
            {
                object? scroller = _scrollerField.GetValue(ScreenInstance);
                if (scroller == null) return 0f;
                object? v = _maxScrollPosMethod.Invoke(scroller, null);
                return v == null ? 0f : Convert.ToSingle(v);
            }
            catch { return 0f; }
        }

        private static bool InvokeBool(MethodInfo? m, object target)
        {
            if (m == null) return false;
            try { return (bool)m.Invoke(target, null)!; } catch { return false; }
        }

        private static string Trunc(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
