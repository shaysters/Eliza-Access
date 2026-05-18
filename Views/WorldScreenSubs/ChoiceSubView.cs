using System;
using System.Reflection;
using ElizaAccess.Common;

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
            if (_texts == null || _texts.Length == 0) return;
            if (_focusField == null || _selectedField == null) return;
            if (_maybeIntHasValue == null || _maybeIntGetValue == null) return;

            int focusIdx = ReadMaybeInt(_focusField);
            if (focusIdx >= 0 && focusIdx < _texts.Length && focusIdx != _lastFocusedIdx)
            {
                _lastFocusedIdx = focusIdx;
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
                    // Prefix order mirrors the visual layout: the "Proxy
                    // Response" header is drawn above the line of text it
                    // labels, so we read header-then-content the way a
                    // sighted player parses the panel.
                    string announce = isProxy ? $"Proxy Response, {label}" : label;
                    Speech.Speak(announce);
                }
                bool matchedProxyText = _proxyText != null && label == _proxyText;
                Log($"[ChoiceSubView] focus → [{focusIdx}] \"{label}\"" +
                    (matchedProxyText
                        ? (SessionState.IsActive ? " (proxy)" : " (proxy-text matched but no session — marker suppressed)")
                        : ""));
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

        public override void OnLeave()
        {
            _texts = null;
            _lastFocusedIdx  = -1;
            _lastSelectedIdx = -1;
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
            if (count == 0) return "No choices.";
            if (count == 1) return "One choice.";
            return $"Make a choice. {count} choices.";
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
