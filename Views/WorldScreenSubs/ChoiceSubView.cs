using System;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Announces in-story dialogue choices. Speaks the prompt (if any) and
    /// an enumerating instruction on entry, then each focused option's
    /// text as the player navigates between them.
    ///
    /// Activation: <c>IsActive ⇔ scriptState.hasDialogChoice</c>. The
    /// framework owns OnEnter/OnLeave transitions, so this class doesn't
    /// track "did I just enter the choice mode?" manually.
    ///
    /// When the player confirms (the WorldScreen <c>selectedField</c>
    /// Maybe&lt;int&gt; transitions to a valid index), writes
    /// <see cref="ChoiceState.LastConfirmedChoiceText"/> so the autoplay
    /// observer can suppress speaking the echoed dialogue line that
    /// follows.
    ///
    /// Migrated from V2ChoiceObserver. Dropped:
    /// - PollKbNav (Harmony's KeyboardToController handles input now;
    ///   the binding loop in VirtualInput.smethod_9 writes focus/select
    ///   directly when the emulated A button fires).
    /// - Texting context replay (V2AutoObserver speaks dialogue lines
    ///   as they arrive; we no longer re-read them at choice entry).
    /// </summary>
    internal sealed class ChoiceSubView : SubView
    {
        // ── Known obfuscated names (stable across Eazfuscator 3.3 builds) ──

        // WorldScreen → scriptState (instance method, no args)
        private const string ScriptStateMethodName     = "#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=";
        // WorldScreen instance Maybe<int> fields
        private const string FocusFieldName            = "#=qecXjHwt7O3QmYuKRQR8K76k8P6OpQs1D_aUc0pPRHSI=";
        private const string SelectedFieldName         = "#=qSRxAQlsYhVzt0wtGhTkkYFN5lTrj1YnmZH8S4ZLUsLc=";
        // ScriptState instance methods
        private const string HasDialogChoiceMethodName = "#=qHdn5kuJb7Vh9G1Lb1wKV$XKOqRnoF0nNbyDAmmMXEFI=";
        private const string GetChoiceStructMethodName = "#=qzBLNJTLl$d4xOaJLN1MS_21IligeMm3a52uWxaui3RI=";
        // ChoiceStruct instance fields
        private const string ChoiceTextsFieldName      = "#=qJ8$dt4_UaFTF4xdDwdBNIA==";
        private const string ChoicePromptFieldName     = "#=qE9wcDpgbiU24hpoqqnornA==";
        // Maybe<string>.GetValue (disambiguates from ToString — both return string)
        private const string MaybeStrGetValueMethodName = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        // ── WorldScreen-level reflection (resolved in OnInit) ──────────
        private MethodInfo? _scriptStateMethod;
        private FieldInfo?  _focusField;
        private FieldInfo?  _selectedField;
        private MethodInfo? _maybeIntHasValue;
        private MethodInfo? _maybeIntGetValue;

        // ── ScriptState-level reflection (deferred to first live instance) ──
        private bool _ssReflectDone;
        private MethodInfo? _hasDialogChoiceMethod;
        private MethodInfo? _getChoiceStructMethod;

        // ── ChoiceStruct-level reflection (deferred to first live instance) ──
        private bool _structReflectDone;
        private FieldInfo?  _choiceTextsField;
        private FieldInfo?  _choicePromptField;
        private MethodInfo? _maybeStrHasValue;
        private MethodInfo? _maybeStrGetValue;

        // ── Proxy-response reflection (separate from the ScriptState
        // choice-struct path, because that struct's texts array is in a
        // different order than the engine's DialogueChoice payload). The
        // engine unconditionally renders the proxy as
        // currentStep.method_46().gclass23_0[0] (WorldScreen.cs:2170), so
        // we read that same gclass23_0[0] live and string-match it against
        // each focused choice to pick the right one to flag. ──
        private MethodInfo? _currentStepMethod;        // WorldScreen → GClass10 (signature-based)
        private bool        _choiceCastReflectDone;
        private MethodInfo? _choiceCastMethod;         // GClass10 → GStruct32 (the DialogueChoice cast)
        private FieldInfo?  _castStructTextsField;     // GStruct32.gclass23_0

        // ── Per-activation state ───────────────────────────────────────
        // Captured at OnEnter so focus-change announcements don't re-fetch
        // the texts array every poll (it doesn't change while active).
        private Array? _texts;
        private int    _lastFocusedIdx  = -1;
        private int    _lastSelectedIdx = -1;
        // Cleaned proxy text captured at OnEnter; null if reflection
        // failed. Compared against each focused choice's label so the
        // matching option gets the "(Proxy Response)" suffix.
        private string? _proxyText;

        // Up/Down at either end of the list doesn't move the game's
        // focus (it clamps), but the press still stops speech through
        // InputInterrupter, so mashing past the end left the player in
        // silence. After a press we give the game a moment to move the
        // focus; if it didn't, re-read the current option.
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);
        private int _bumpCheckAt;      // TickCount to check at; 0 = none
        private int _bumpFocusIdx;
        private const int BumpCheckMs = 150;

        // Deferred-announce state. The choice struct can land just
        // before another sub speaks something that should precede the
        // prompt — typically a texting overlay's incoming message
        // (typing animation hasn't completed) or a session-narration
        // line that the engine queued in the same frame the choice
        // activated.
        //
        // Texting case: 32ms time deadline. When no message is
        // arriving we want the prompt immediately, not a noticeable
        // pause; the typing animation is so long (seconds) that 32ms
        // of "did the tick advance" is decisive.
        //
        // Session case: catch-up to a NARRATION TARGET. The engine
        // walks through script steps synchronously inside one frame
        // (WorldScreen.method_3), so when the choice activates, any
        // "Set phase: …" line was already queued in queue_0 the
        // moment we polled — checking for growth after OnEnter
        // catches nothing because the growth happened just before.
        // Instead we snapshot (CurrentListCount + PendingQueueCount)
        // — the total narration entries the engine "knew about" —
        // and wait for NarrationSub.LastReadCount to reach that
        // target. By then every item pending at choice-activation
        // has been processed (spoken or filtered). Hard cap is the
        // safety net for the case where NarrationSub gets gated off
        // mid-defer and never reads the target.
        private string? _pendingPrompt;
        private string? _pendingInstruction;
        private int     _pendingTextingBaseline;
        private int     _pendingNarrationTarget;       // -1 = unused
        private int     _pendingGraceUntil;
        private int     _pendingTextingDeadline;
        private int     _pendingHardCap;
        private const int PendingGraceMs           = 300;
        private const int PendingTextingDeadlineMs =  32;
        // Generous enough that even a moderately-deep queue of
        // pending narration (a few items each throttled at 250ms)
        // drains before the cap fires. Above 3s the player would
        // notice the lag, so this is the upper bound.
        private const int PendingSessionHardMs     = 3000;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _scriptStateMethod = tWS.GetMethod(ScriptStateMethodName, inst);
            _focusField        = tWS.GetField(FocusFieldName, inst);
            _selectedField     = tWS.GetField(SelectedFieldName, inst);

            if (_focusField != null)
            {
                Type maybeIntType = _focusField.FieldType;
                foreach (MethodInfo m in maybeIntType.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 0) continue;
                    if (m.ReturnType == typeof(bool))      _maybeIntHasValue = m;
                    else if (m.ReturnType == typeof(int))  _maybeIntGetValue = m;
                }
            }

            // currentStep accessor: instance no-arg method on WorldScreen
            // whose return type has 30+ bool no-arg discriminators
            // (GClass10's 36-case tagged union shape).
            foreach (MethodInfo m in tWS.GetMethods(inst))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (rt.IsValueType || rt == typeof(string)) continue;
                if (CountBoolNoArgMethods(rt) >= 30)
                {
                    _currentStepMethod = m;
                    break;
                }
            }

            Log($"[ChoiceSubView] init: scriptState={(_scriptStateMethod != null)} " +
                $"focus={(_focusField != null)} selected={(_selectedField != null)} " +
                $"currentStep={(_currentStepMethod != null)} " +
                $"maybeInt.Has={(_maybeIntHasValue != null)} maybeInt.Get={(_maybeIntGetValue != null)}");
        }

        private static int CountBoolNoArgMethods(Type t)
        {
            int n = 0;
            foreach (MethodInfo m in t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool)) n++;
            }
            return n;
        }

        public override bool IsActive
        {
            get
            {
                if (_scriptStateMethod == null) return false;
                object? ss = GetScriptState();
                if (ss == null) return false;
                if (!_ssReflectDone) ReflectScriptState(ss);
                if (_hasDialogChoiceMethod == null) return false;
                try { return (bool)_hasDialogChoiceMethod.Invoke(ss, null)!; }
                catch { return false; }
            }
        }

        public override void OnEnter()
        {
            _lastFocusedIdx  = -1;
            _lastSelectedIdx = -1;
            _texts = null;
            _proxyText = null;
            _bumpCheckAt = 0;
            _kbUp.Reset();
            _kbDown.Reset();

            object? ss = GetScriptState();
            if (ss == null || _getChoiceStructMethod == null) return;

            object? structVal;
            try { structVal = _getChoiceStructMethod.Invoke(ss, null); }
            catch { return; }
            if (structVal == null) return;

            if (!_structReflectDone) ReflectChoiceStruct(structVal);
            if (_choiceTextsField == null) return;

            try { _texts = _choiceTextsField.GetValue(structVal) as Array; }
            catch { _texts = null; }
            if (_texts == null || _texts.Length == 0) return;

            _proxyText = ResolveProxyText();
            Log($"[ChoiceSubView] proxy text: \"{_proxyText ?? "<unresolved>"}\"");

            string? prompt = ReadPrompt(structVal);
            string instruction = BuildInstructionText(_texts.Length);

            // Defer when something else might still need to speak
            // first:
            //   - Texting overlay: typing animation may not have
            //     completed for the incoming message.
            //   - Session narration: engine can append a "Set phase: …"
            //     line a tick or two after the choice struct lands.
            // We snapshot both event ticks and wait for either to
            // advance past its baseline; whichever fires first
            // triggers the grace delay and announce. Safety deadline
            // is tight so non-deferred cases (no texting, no session,
            // or simply nothing else queued) get the prompt
            // immediately.
            bool inTexting = TextingState.IsActive;
            bool inSession = SessionState.IsActive;
            if (inTexting || inSession)
            {
                int now = Environment.TickCount;
                _pendingPrompt          = prompt;
                _pendingInstruction     = instruction;
                _pendingTextingBaseline = TextingState.LastMessageTick;
                // Catch-up target: how many narration entries
                // NarrationSub needs to have READ before we fire.
                // = current visible list + items still throttled in
                // the engine's queue. -1 if no session (texting-only
                // case doesn't use this signal).
                _pendingNarrationTarget = inSession
                    ? NarrationSubView.CurrentListCount + NarrationSubView.PendingQueueCount
                    : -1;
                _pendingGraceUntil      = 0;
                _pendingTextingDeadline = inTexting ? now + PendingTextingDeadlineMs : int.MaxValue;
                _pendingHardCap         = now + PendingSessionHardMs;
                Log($"[ChoiceSubView] enter: deferred (texting={inTexting} session={inSession} narrationTarget={_pendingNarrationTarget} list={NarrationSubView.CurrentListCount} queue={NarrationSubView.PendingQueueCount} read={NarrationSubView.LastReadCount}) prompt=\"{prompt ?? "<none>"}\" instruction=\"{instruction}\" count={_texts.Length}");
                return;
            }

            if (prompt != null)
            {
                Speech.Speak(prompt);
                Speech.Speak(instruction);
                Log($"[ChoiceSubView] enter: prompt=\"{prompt}\" instruction=\"{instruction}\" count={_texts.Length}");
            }
            else
            {
                Speech.Speak(instruction);
                Log($"[ChoiceSubView] enter: no prompt, instruction=\"{instruction}\" count={_texts.Length}");
            }
        }

        private void DrainPendingAnnouncement()
        {
            if (_pendingInstruction == null) return;
            int now = Environment.TickCount;

            // Texting-only case: short time deadline. Tick advance on
            // TextingState.LastMessageTick means a message just spoke
            // → start grace.
            if (_pendingNarrationTarget < 0)
            {
                bool textingAdvanced = TextingState.LastMessageTick != _pendingTextingBaseline;
                if (_pendingGraceUntil == 0 && textingAdvanced)
                    _pendingGraceUntil = now + PendingGraceMs;

                if (_pendingGraceUntil != 0 && now >= _pendingGraceUntil)
                {
                    Fire(now, "texting-grace");
                    return;
                }
                if (now >= _pendingTextingDeadline)
                {
                    Fire(now, "texting-deadline");
                    return;
                }
                return;
            }

            // Session case: wait for NarrationSub to catch up to the
            // target (every narration entry that was already pending
            // at choice OnEnter has been processed). Once caught up,
            // add the small grace so the speech engine actually
            // starts playing the last spoken line before our prompt
            // queues behind it.
            bool caughtUp = NarrationSubView.LastReadCount >= _pendingNarrationTarget;
            if (_pendingGraceUntil == 0 && caughtUp)
            {
                _pendingGraceUntil = now + PendingGraceMs;
            }

            if (_pendingGraceUntil != 0 && now >= _pendingGraceUntil)
            {
                Fire(now, $"session-catchup (target={_pendingNarrationTarget} read={NarrationSubView.LastReadCount})");
                return;
            }

            // Hard cap: fire regardless. Covers the case where
            // NarrationSub gets gated off mid-defer and never reaches
            // the target. Better to announce late than hang.
            if (now >= _pendingHardCap)
            {
                Fire(now, $"hard-cap (target={_pendingNarrationTarget} read={NarrationSubView.LastReadCount})");
            }
        }

        private void Fire(int now, string reason)
        {
            string? p = _pendingPrompt;
            string  i = _pendingInstruction!;
            _pendingPrompt      = null;
            _pendingInstruction = null;
            _pendingGraceUntil  = 0;

            if (p != null)
            {
                Speech.Speak(p);
                Speech.Speak(i);
            }
            else
            {
                Speech.Speak(i);
            }
            Log($"[ChoiceSubView] deferred announce drained ({reason})");
        }

        // Walks the engine's own proxy-response path: currentStep is the
        // active GClass10 step, method_46 casts it to GStruct32 (the
        // DialogueChoice payload), and gclass23_0[0] is the GClass23 the
        // panel renders at WorldScreen.cs:2170. ScriptState's choice
        // struct (read via _texts above) holds the same options but in a
        // different order, so we have to grab the proxy from the engine's
        // path and string-match it back against _texts to flag it.
        private string? ResolveProxyText()
        {
            if (_currentStepMethod == null) return null;

            object? step;
            try { step = _currentStepMethod.Invoke(Parent, null); }
            catch { return null; }
            if (step == null) return null;

            if (!_choiceCastReflectDone) ReflectChoiceCast(step);
            if (_choiceCastMethod == null || _castStructTextsField == null) return null;

            try
            {
                object? payload = _choiceCastMethod.Invoke(step, null);
                if (payload == null) return null;
                if (!(_castStructTextsField.GetValue(payload) is Array arr) || arr.Length == 0) return null;
                object? first = arr.GetValue(0);
                return CleanLabel(first?.ToString());
            }
            catch (Exception ex)
            {
                Log($"[ChoiceSubView] proxy-read error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Identify GClass10.method_46 by struct shape. Constraint: the
        // return type is a value-type struct that contains an array field
        // whose element type matches the element type of _texts (i.e.,
        // GClass23). GStruct32 (DialogueChoice payload) is the only
        // tagged-union cast whose struct has a GClass23 array — the
        // other GStructs hold single GClass23s, no GClass23s at all, or
        // entirely different shapes. Invoking the wrong cast throws
        // GException0, so we cross-check by try-invoke as a tiebreaker.
        private void ReflectChoiceCast(object step)
        {
            _choiceCastReflectDone = true;
            if (_texts == null || _texts.Length == 0) return;
            Type elementType = _texts.GetValue(0)?.GetType() ?? typeof(object);

            foreach (MethodInfo m in step.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (!rt.IsValueType) continue;

                FieldInfo? arrField = null;
                foreach (FieldInfo f in rt.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.IsArray && f.FieldType.GetElementType() == elementType)
                    {
                        arrField = f;
                        break;
                    }
                }
                if (arrField == null) continue;

                // Try-invoke: the right method returns; the wrong casts
                // throw GException0. We're called from OnEnter where the
                // active step IS a DialogueChoice, so method_46 succeeds.
                try
                {
                    object? result = m.Invoke(step, null);
                    if (result == null) continue;
                    _choiceCastMethod      = m;
                    _castStructTextsField  = arrField;
                    Log($"[ChoiceSubView] choice-cast: method={m.Name} textsField={arrField.Name}");
                    return;
                }
                catch { /* wrong cast, keep searching */ }
            }

            Log("[ChoiceSubView] choice-cast: no matching method found");
        }

        public override void OnUpdate()
        {
            DrainPendingAnnouncement();

            // Keep the edges' held state current every tick, even while
            // the announcement is still deferred.
            bool navPressed = _kbUp.Pressed() | _kbDown.Pressed();

            if (_texts == null || _texts.Length == 0) return;
            if (_focusField == null || _selectedField == null) return;
            if (_maybeIntHasValue == null || _maybeIntGetValue == null) return;

            // The option is read after the prompt and instruction, so
            // hold it back while those are still deferred.
            bool announcing = _pendingInstruction == null;

            int focusIdx = ReadMaybeInt(_focusField);

            int now = Environment.TickCount;
            if (navPressed && focusIdx >= 0)
            {
                _bumpCheckAt  = now + BumpCheckMs;
                _bumpFocusIdx = focusIdx;
            }

            if (announcing && focusIdx >= 0 && focusIdx < _texts.Length && focusIdx != _lastFocusedIdx)
            {
                _bumpCheckAt = 0;
                _lastFocusedIdx = focusIdx;
                AnnounceFocus(focusIdx);
            }
            else if (_bumpCheckAt != 0 && now - _bumpCheckAt >= 0)
            {
                _bumpCheckAt = 0;
                if (announcing && focusIdx == _bumpFocusIdx && focusIdx == _lastFocusedIdx)
                {
                    Log($"[ChoiceSubView] end of list — re-reading [{focusIdx}]");
                    AnnounceFocus(focusIdx);
                }
            }

            int selIdx = ReadMaybeInt(_selectedField);
            if (selIdx >= 0 && selIdx < _texts.Length && selIdx != _lastSelectedIdx)
            {
                _lastSelectedIdx = selIdx;
                string chosen = CleanLabel(_texts.GetValue(selIdx)?.ToString());
                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    ChoiceState.LastConfirmedChoiceText = chosen;
                    ChoiceState.LastConfirmedChoiceTick = Environment.TickCount;
                    Log($"[ChoiceSubView] confirmed: [{selIdx}] \"{chosen}\"");
                }
            }
        }

        private void AnnounceFocus(int focusIdx)
        {
            if (_texts == null) return;
            string label = CleanLabel(_texts.GetValue(focusIdx)?.ToString());
            if (!string.IsNullOrWhiteSpace(label))
            {
                // The Eliza panel's "Proxy Response" tracks
                // currentStep.method_46().gclass23_0[0] from the engine
                // side; we resolved that text in OnEnter as _proxyText.
                // The struct ChoiceSubView reads via ScriptState has the
                // same options in a different order, so we string-match
                // rather than rely on a fixed index.
                //
                // The proxy concept ONLY exists during a therapy
                // session — the Eliza panel that labels one option as
                // her recommended response is the in-session UI.
                // Outside sessions (regular dialogue choices) every
                // option is just an option, no "off-script" framing.
                // SessionState.IsActive is the cross-observer signal
                // SessionSubView keeps in sync.
                bool isProxy = SessionState.IsActive
                               && _proxyText != null
                               && string.Equals(label, _proxyText, StringComparison.Ordinal);

                // Format via ButtonElement so the announcement
                // matches every other button focus across the
                // mod: "{label}, Button". When this option is
                // the one the Eliza panel marked as her
                // recommended response, prefix with "Proxy
                // Response" — the marker must land BEFORE the
                // response text, not after, because the player
                // makes story-relevant decisions based on
                // whether the option is Eliza's recommendation
                // and we can't risk the prefix being missed
                // mid-utterance.
                string body = new ButtonElement(label).ResolveFocusText();
                string announce = isProxy ? Loc.Fmt("choice.proxy_response", body) : body;
                Speech.Speak(announce);
            }
            bool matchedProxyText = _proxyText != null && label == _proxyText;
            Log($"[ChoiceSubView] focus → [{focusIdx}] \"{label}\"" +
                (matchedProxyText
                    ? (SessionState.IsActive ? " (proxy)" : " (proxy-text matched but no session — marker suppressed)")
                    : ""));
        }

        public override void OnLeave()
        {
            _texts = null;
            _lastFocusedIdx  = -1;
            _lastSelectedIdx = -1;
            _pendingPrompt      = null;
            _pendingInstruction = null;
            _pendingGraceUntil  = 0;
            _bumpCheckAt        = 0;
        }

        // ── Reflection helpers ─────────────────────────────────────────

        private object? GetScriptState()
        {
            try { return _scriptStateMethod?.Invoke(Parent, null); }
            catch { return null; }
        }

        private void ReflectScriptState(object ss)
        {
            _ssReflectDone = true;
            Type ssType = ss.GetType();
            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            _hasDialogChoiceMethod = ssType.GetMethod(HasDialogChoiceMethodName, bf);
            _getChoiceStructMethod = ssType.GetMethod(GetChoiceStructMethodName, bf);
            Log($"[ChoiceSubView] ss-reflect ({ssType.Name}): hasDialogChoice={(_hasDialogChoiceMethod != null)} " +
                $"getChoiceStruct={(_getChoiceStructMethod != null)}");
        }

        private void ReflectChoiceStruct(object structVal)
        {
            _structReflectDone = true;
            Type st = structVal.GetType();
            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            _choiceTextsField  = st.GetField(ChoiceTextsFieldName, bf);
            _choicePromptField = st.GetField(ChoicePromptFieldName, bf);

            if (_choicePromptField != null)
            {
                Type maybeStrType = _choicePromptField.FieldType;
                foreach (MethodInfo m in maybeStrType.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 0) continue;
                    if (m.ReturnType == typeof(bool))          _maybeStrHasValue = m;
                    else if (m.Name == MaybeStrGetValueMethodName) _maybeStrGetValue = m;
                }
                // Fallback if the known GetValue name shifted: any zero-arg
                // string-returning method that's not ToString.
                if (_maybeStrGetValue == null)
                {
                    foreach (MethodInfo m in maybeStrType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.ReturnType != typeof(string)) continue;
                        if (m.Name == "ToString") continue;
                        _maybeStrGetValue = m;
                        break;
                    }
                }
            }

            Log($"[ChoiceSubView] struct-reflect ({st.Name}): texts={(_choiceTextsField != null)} " +
                $"prompt={(_choicePromptField != null)} maybeStr.Has={(_maybeStrHasValue != null)} " +
                $"maybeStr.Get={(_maybeStrGetValue != null)}");
        }

        private int ReadMaybeInt(FieldInfo field)
        {
            try
            {
                object? maybe = field.GetValue(Parent);
                if (maybe == null) return -1;
                if (!(bool)_maybeIntHasValue!.Invoke(maybe, null)!) return -1;
                return (int)_maybeIntGetValue!.Invoke(maybe, null)!;
            }
            catch { return -1; }
        }

        private string? ReadPrompt(object structVal)
        {
            if (_choicePromptField == null || _maybeStrHasValue == null || _maybeStrGetValue == null)
                return null;
            try
            {
                object? maybe = _choicePromptField.GetValue(structVal);
                if (maybe == null) return null;
                if (!(bool)_maybeStrHasValue.Invoke(maybe, null)!) return null;
                object? val = _maybeStrGetValue.Invoke(maybe, null);
                if (val == null) return null;
                string pt = CleanLabel(val.ToString());
                if (string.IsNullOrWhiteSpace(pt)) return null;
                // Pure-numeric prompts ("10"/"20"/...) are script-internal
                // counters with no audio meaning — suppress.
                if (IsPureNumeric(pt)) return null;
                return pt;
            }
            catch { return null; }
        }

        // ── Pure helpers ───────────────────────────────────────────────

        private static string BuildInstructionText(int count)
        {
            if (count == 0) return Loc.Get("choice.no_choices");
            if (count == 1) return Loc.Get("choice.one_choice");
            return Loc.Fmt("choice.make_choice", count);
        }

        private static string CleanLabel(string? raw)
        {
            if (raw == null) return "";
            string s = raw.Trim();
            // Hash-prefixed strings are obfuscator artifacts that occasionally
            // leak through the localization path; never speak them.
            if (s.StartsWith("#=q")) return "";
            return s;
        }

        private static bool IsPureNumeric(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (!char.IsDigit(c) && c != '.' && c != ',' && c != ' ' && c != '%')
                    return false;
            }
            return true;
        }
    }
}
