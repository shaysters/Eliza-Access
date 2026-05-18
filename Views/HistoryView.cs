using System;
using System.Collections;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Reads the HistoryScreen — the scrollable chat-log of every
    /// dialogue line so far. Three behaviours:
    ///
    /// 1. <b>Entry announcement</b>. After a 600 ms delay (so the
    ///    pause-menu's "History" announcement finishes first), speak
    ///    the navigation hint plus the bottom-most visible dialogue
    ///    line so the user knows where they're starting from.
    ///
    /// 2. <b>Scroll tracking</b>. When the scroll position changes,
    ///    recompute the bottom-most visible DialogueLine. When the
    ///    user releases input (axis returns to 0) or 400 ms elapses,
    ///    speak that line. Avoids spamming during a hold-scroll.
    ///
    /// 3. <b>Keyboard navigation</b>. Up/Down on the keyboard either
    ///    drives the scroller (when the list overflows the viewport)
    ///    or walks an anchor index from one DialogueLine to the next
    ///    (when the whole list already fits and the engine wouldn't
    ///    otherwise respond). Either way the new line is spoken.
    ///
    /// HistoryScreen has no clickable hotspots — focus is "what
    /// dialogue line is at the bottom of the visible window" — so
    /// <see cref="ReadCurrentFocus"/> returns null and all work
    /// happens in <see cref="OnUpdate"/>.
    /// </summary>
    internal sealed class HistoryView : ScreenView
    {
        public override string ScreenTypeName => "HistoryScreen";

        private const string Hint            = "History. Use D-pad up and down to navigate. Press and hold to scroll.";
        private const int    EntryDelayMs    = 600;
        private const int    SlowSettleMs    = 400;
        private const float  KbScrollStep    = 150f;

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

        private const string HistoryListField   = "#=qs8WfAVpAvgpN3uS$ouSByQ==";
        private const string ScrollCacheField   = "#=qQUvvA6SIp0Z4BhvuMmKuNTHGjoAP$HUJygZrv6FLde8=";
        private const string ScrollerField      = "#=qHPFzw1no_xGjBzJqEvK88Q==";
        private const string LineHeightsField   = "#=qJad$nd$44WGzYp9Q2j2OTARmqRuCcmT7YmWo5zYyLBA=";
        private const string ScrollerGetOrigin     = "#=qL3NKXZLdwuOtU9jcCm9jC3Zx025YeiaJc_WqHEzMNpk=";
        private const string ScrollerMaxScrollPos  = "#=quAL7IZ33NpnwZJ2VYIJVeytYhk$3KNu_bGSWclHYG0s=";
        private const string ScrollerSetPosition   = "#=qwp_MZlk8oypfEboUc0lURxGhlbj3CHrDYsyDG83oeJs=";

        private const string VirtualInputGetAxis = "#=qemIzt2N4xA_skWiKqpcicQ==";

        // ── Reflected handles ──────────────────────────────────────────
        private MethodInfo? _isBeginChapter;
        private MethodInfo? _isDialogueLine;
        private MethodInfo? _isChangeLocation;
        private MethodInfo? _getDialogueLine;
        private FieldInfo?  _dlSpeakerField;
        private FieldInfo?  _dlTextField;
        private FieldInfo?  _speakerNameField;
        private FieldInfo?  _historyListField;
        private FieldInfo?  _scrollCacheField;
        private FieldInfo?  _scrollerField;
        private FieldInfo?  _lineHeightsField;
        private MethodInfo? _getOriginMethod;
        private MethodInfo? _maxScrollPosMethod;
        private MethodInfo? _setPositionMethod;
        private MethodInfo? _getAxisMethod;
        private FieldInfo?  _vec2YField; // resolved lazily off GetOrigin()'s return

        // ── Per-activation state ───────────────────────────────────────
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);

        private bool     _entrySpeechPending;
        private DateTime _entryTime;

        private float    _lastScrollPos        = float.MinValue;
        private int      _lastBottomVisibleDL  = -2;   // -2 unset, -1 none, ≥0 absolute index
        private int      _pendingBottomDL      = -2;
        private DateTime _pendingBottomTime;
        private int      _spokenBottomDL       = -2;

        private int      _kbAnchorDL           = -1;

        protected override void OnAdded(object screen)
        {
            const BindingFlags inst    = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags privInst = BindingFlags.NonPublic | BindingFlags.Instance;

            Type? tEntry = Nagoya.FindType(EntryTypeName);
            if (tEntry != null)
            {
                _isBeginChapter   = tEntry.GetMethod(EntryIsBeginChapter,   inst);
                _isDialogueLine   = tEntry.GetMethod(EntryIsDialogueLine,   inst);
                _isChangeLocation = tEntry.GetMethod(EntryIsChangeLocation, inst);
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
            _scrollCacheField = tScreen.GetField(ScrollCacheField, privInst);
            _scrollerField    = tScreen.GetField(ScrollerField,    privInst);
            _lineHeightsField = tScreen.GetField(LineHeightsField, privInst);
            if (_scrollerField != null)
            {
                Type st = _scrollerField.FieldType;
                _getOriginMethod    = st.GetMethod(ScrollerGetOrigin,    inst);
                _maxScrollPosMethod = st.GetMethod(ScrollerMaxScrollPos, inst);
                _setPositionMethod  = st.GetMethod(ScrollerSetPosition,  inst);
            }

            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI != null)
                _getAxisMethod = tVI.GetMethod(VirtualInputGetAxis, BindingFlags.Public | BindingFlags.Static);

            Log($"[HistoryView] added: entry-methods={(_isDialogueLine != null && _getDialogueLine != null)} " +
                $"dl-fields={(_dlSpeakerField != null && _dlTextField != null)} " +
                $"list={(_historyListField != null)} scroll={(_scrollerField != null)} " +
                $"heights={(_lineHeightsField != null)} axis={(_getAxisMethod != null)}");
        }

        protected override void OnFocus()
        {
            if (ScreenInstance == null) return;
            // Defer the entry speech so any prior "History" label (the
            // pause-menu focus announcement) finishes first.
            _entrySpeechPending = true;
            _entryTime          = DateTime.Now;

            _lastScrollPos = ReadScrollPos();
            ComputeVisibleDLRange(out _, out int last);
            _lastBottomVisibleDL = last;
            _pendingBottomDL     = -2;
            _spokenBottomDL      = -2;
            _kbAnchorDL          = last;
            _kbUp.Reset(); _kbDown.Reset();
        }

        protected override void OnUpdate()
        {
            if (ScreenInstance == null) return;

            // (1) Deferred entry speech.
            if (_entrySpeechPending
                && (DateTime.Now - _entryTime).TotalMilliseconds >= EntryDelayMs)
            {
                _entrySpeechPending = false;
                ComputeVisibleDLRange(out _, out int last);
                _pendingBottomDL = last;
                _spokenBottomDL  = last;
                string anchorText = BuildAnchorSpeech(last);
                string text = string.IsNullOrEmpty(anchorText) ? Hint : Hint + " " + anchorText;
                Speech.Speak(text);
                Log($"[HistoryView] entry: \"{Trunc(text, 100)}\"");
            }

            // (2) Scroll position change → recompute visible-bottom DL,
            //     start settle timer.
            float pos = ReadScrollPos();
            if (pos != float.MinValue && pos != _lastScrollPos)
            {
                _lastScrollPos = pos;
                int oldBottom = _lastBottomVisibleDL;
                ComputeVisibleDLRange(out _, out int last);
                _lastBottomVisibleDL = last;
                if (last != oldBottom)
                {
                    _pendingBottomDL   = last;
                    _pendingBottomTime = DateTime.Now;
                }
            }

            // (3) Settle: speak when axis releases or timeout fires.
            if (!_entrySpeechPending
                && _pendingBottomDL != _spokenBottomDL
                && _pendingBottomDL >= 0)
            {
                float axis         = ReadAxis1();
                bool  released     = !float.IsNaN(axis) && axis == 0f;
                bool  timedOut     = (DateTime.Now - _pendingBottomTime).TotalMilliseconds >= SlowSettleMs;
                if (released || timedOut)
                {
                    _spokenBottomDL = _pendingBottomDL;
                    string speech = BuildAnchorSpeech(_pendingBottomDL);
                    if (!string.IsNullOrEmpty(speech))
                    {
                        Speech.Speak(speech);
                        Log($"[HistoryView] scroll → dl={_pendingBottomDL} ({(released ? "released" : "timeout")}): \"{Trunc(speech, 100)}\"");
                    }
                }
            }

            // (4) Keyboard nav (rising edges only).
            PollKbNav();
        }

        protected override (object? key, string? label) ReadCurrentFocus() => (null, null);

        // ── Keyboard navigation ───────────────────────────────────────

        private void PollKbNav()
        {
            bool up   = _kbUp.Pressed();
            bool down = _kbDown.Pressed();
            if (!up && !down) return;

            float maxScroll = ReadMaxScrollPos();
            // When the list overflows, drive the scroller. We add to
            // whatever the engine's axis-driven scroll already did this
            // tick — both feed into the same SetPosition, last write
            // wins, so a tap visibly moves things even when the engine
            // alone wouldn't.
            if (maxScroll > 0f && _scrollerField != null && _setPositionMethod != null)
            {
                try
                {
                    object? scroller = _scrollerField.GetValue(ScreenInstance);
                    if (scroller != null)
                    {
                        float current = ReadScrollPos();
                        if (current != float.MinValue)
                        {
                            float newPos = current + (up ? -KbScrollStep : KbScrollStep);
                            if (newPos < 0f) newPos = 0f;
                            if (newPos > maxScroll) newPos = maxScroll;
                            _setPositionMethod.Invoke(scroller, new object[] { newPos });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[HistoryView] kb-scroll error: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Anchor step (always — when content fits the viewport this
            // is the only way to step DLs; when it overflows this keeps
            // the anchor in sync with what the user is hearing).
            try
            {
                if (_kbAnchorDL < 0)
                {
                    ComputeVisibleDLRange(out _, out int l);
                    _kbAnchorDL = l;
                }
                int next = FindAdjacentDL(_kbAnchorDL, up ? -1 : +1);
                if (next >= 0) _kbAnchorDL = next;

                // Only speak here when the engine isn't doing the
                // scroll-driven speech — the settle path will handle
                // that case and announce the new visible bottom.
                if (maxScroll == 0f)
                {
                    string speech = BuildAnchorSpeech(_kbAnchorDL);
                    if (!string.IsNullOrEmpty(speech))
                    {
                        Speech.Speak(speech);
                        _spokenBottomDL  = _kbAnchorDL;
                        _pendingBottomDL = _kbAnchorDL;
                        Log($"[HistoryView] kb-anchor → dl={_kbAnchorDL} ({(up ? "up" : "down")}): \"{Trunc(speech, 100)}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[HistoryView] kb-anchor error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private int FindAdjacentDL(int currentIndex, int direction)
        {
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return -1;
            if (currentIndex < 0) return -1;
            for (int i = currentIndex + direction; i >= 0 && i < list.Count; i += direction)
            {
                object? entry = list[i];
                if (entry != null && InvokeBool(_isDialogueLine, entry)) return i;
            }
            return -1;
        }

        // ── Visible-window math (mirrors HistoryScreen render at ~108-165) ──

        private void ComputeVisibleDLRange(out int first, out int last)
        {
            first = -1; last = -1;
            if (!(_historyListField?.GetValue(ScreenInstance) is IList list)) return;
            float originY = ReadScrollerOriginY();
            if (originY == float.MinValue) return;
            IList? heights = _lineHeightsField?.GetValue(ScreenInstance) as IList;

            float y    = originY - 39f;
            bool  flag = true;
            for (int i = 0; i < list.Count; i++)
            {
                object? entry = list[i];
                if (entry == null) continue;
                float topY = y;
                bool  visible = topY > -400f && topY < 2000f;

                bool isBC = InvokeBool(_isBeginChapter,   entry);
                bool isDL = InvokeBool(_isDialogueLine,   entry);
                bool isCL = InvokeBool(_isChangeLocation, entry);

                if (isBC)      { y -= 133f; flag = true; }
                else if (isCL) { if (!flag) { y -= 51f; flag = true; } }
                else if (isDL)
                {
                    if (visible)
                    {
                        if (first < 0) first = i;
                        last = i;
                    }
                    float h = 0f;
                    if (heights != null && i < heights.Count) h = Convert.ToSingle(heights[i]);
                    y -= h > 0f ? (h - 4f) : 50f;
                    flag = false;
                }
                if (y < -500f) break;
            }
        }

        // ── Speech building ───────────────────────────────────────────

        private string BuildAnchorSpeech(int dlIndex)
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
                return string.IsNullOrEmpty(speaker) ? text : $"{speaker}: {text}";
            }
            catch { return string.Empty; }
        }

        // ── Scroller / axis reads ─────────────────────────────────────

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

        private float ReadScrollerOriginY()
        {
            if (_scrollerField == null || _getOriginMethod == null || ScreenInstance == null) return float.MinValue;
            try
            {
                object? scroller = _scrollerField.GetValue(ScreenInstance);
                if (scroller == null) return float.MinValue;
                object? origin = _getOriginMethod.Invoke(scroller, null);
                if (origin == null) return float.MinValue;
                if (_vec2YField == null)
                {
                    Type v2 = origin.GetType();
                    _vec2YField = v2.GetField("Y", BindingFlags.Public | BindingFlags.Instance)
                               ?? v2.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                    if (_vec2YField == null)
                    {
                        // Live runtime may have it as private + obfuscated;
                        // pick the second float field on the type.
                        int seen = 0;
                        foreach (FieldInfo f in v2.GetFields(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (f.FieldType != typeof(float)) continue;
                            if (seen == 1) { _vec2YField = f; break; }
                            seen++;
                        }
                    }
                }
                if (_vec2YField == null) return float.MinValue;
                object? yv = _vec2YField.GetValue(origin);
                return yv == null ? float.MinValue : Convert.ToSingle(yv);
            }
            catch { return float.MinValue; }
        }

        private float ReadAxis1()
        {
            if (_getAxisMethod == null) return float.NaN;
            try
            {
                ParameterInfo[] p = _getAxisMethod.GetParameters();
                if (p.Length != 1) return float.NaN;
                object axisArg = Enum.ToObject(p[0].ParameterType, 1);
                object? v = _getAxisMethod.Invoke(null, new[] { axisArg });
                return v == null ? float.NaN : Convert.ToSingle(v);
            }
            catch { return float.NaN; }
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
