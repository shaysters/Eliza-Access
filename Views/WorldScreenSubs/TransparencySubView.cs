using System;
using System.Collections;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Announces the Transparency-Mode survey screen — the Eliza UI
    /// that pops up at the end of a session asking the player to
    /// agree or disagree with three conclusions about the patient
    /// they just reviewed.
    ///
    /// Activation: <c>IsActive ⇔ TransparencyState.IsActive</c>, which
    /// <see cref="SessionSubView"/> writes each tick based on
    /// <c>PhoneState.maybe_4.HasValue</c>.
    ///
    /// Layout (from WorldScreen.cs:1213-1319):
    /// - Header: patient name (from <c>ScriptTransparency.scriptCharacter_0</c>).
    /// - Two counters: emails read of total, texts read of total.
    /// - Three conclusion statements (<c>ScriptTransparency.gclass23_0[0..2]</c>),
    ///   each with Agree | Disagree buttons.
    /// - Submit button, gated active once all three are answered.
    ///
    /// Engine state we mirror:
    /// - <c>WorldScreen.maybe_28</c> — <c>Maybe&lt;bool&gt;[3]</c>, one per
    ///   statement: no-value=unanswered, true=Agree, false=Disagree.
    /// - <c>WorldScreen.bool_21</c> — <c>bool[7]</c>, controller cursor
    ///   focus flags. Indices i*2+j (i in [0,3), j in [0,2)) cover the
    ///   six Agree/Disagree buttons; index 6 is Submit.
    /// - <c>PhoneState.hashSet_2</c> — read emails. Count vs
    ///   <c>PhoneState.method_6().Count()</c> (which in transparency
    ///   mode returns only the transparency-context emails).
    /// - <c>PhoneState.hashSet_3</c> — read text-conversation characters.
    ///   Count vs <c>PhoneState.method_11().Count</c>.
    ///
    /// Until both counters are at 100%, the Agree/Disagree buttons render
    /// dimmed and the engine doesn't register click bounds for them
    /// (WorldScreen.cs:1258 — VirtualInput.smethod_6 is wrapped in
    /// <c>if (flag2)</c>). Submit is gated until all three answered.
    /// </summary>
    internal sealed class TransparencySubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        private const string PhoneStateFieldName       = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string TransparencyMaybeField    = "#=q_jj7ZK0kGqgOw$34jWXLPA==";
        private const string PlayerAnswersField        = "#=q$$5T9P5y1c2ItVFX4ngYaVWZ8JLSrGDdr8OpdXz$Wmg=";
        private const string ButtonFocusField          = "#=q1H88sBsBrbY_MiP4N038AUcH9VvuBxsKMfXDgSTtCcc=";
        private const string ReadEmailsSet             = "#=q1Qd3ov4nl6CtJ_RmyGuTcswB1mz5bYEPoyIGvxxAoZ4=";
        private const string ReadCharsSet              = "#=qn7MqpfvnPoCxnvTBaZkU_fq_41ZxSgLMhxJMywFuWVo=";
        private const string TransparencyEmailsMethod  = "#=qlT3cYi$p0e2GwVUfwM$ZpWqDBmtdx01cDnuGa3xJVG0=";
        private const string TransparencyChatsMethod   = "#=qZgd2CiTPtk7XHtkZLehK97OCgnLoGwT3R_ZjmbiiG68=";
        private const string ScriptTransparencyChar    = "#=q5wd5DblUTkgorw0HqYia3w==";
        private const string ScriptTransparencyState   = "#=qFmlWv$8rILVeNaJbwmEh2A==";
        private const string CharNameField             = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string LocalizedTextToString     = "#=qTivbHO_rGrDAcTGXa1E4rg==";

        // Maybe<T> method names — used directly because the signature-
        // based heuristic can't distinguish HasValue from GetValue when
        // T==bool (both return bool). SessionSubView resolves the same
        // way.
        private const string MaybeHasValueName         = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValueName         = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        // ── Reflection (OnInit) ────────────────────────────────────────
        private FieldInfo?  _wsPhoneStateField;
        private FieldInfo?  _wsAnswersField;       // WorldScreen.maybe_28
        private FieldInfo?  _wsFocusField;         // WorldScreen.bool_21
        private FieldInfo?  _phoneStateTransparencyField;
        private MethodInfo? _maybeTransparencyHasValue;
        private MethodInfo? _maybeTransparencyGetValue;
        private FieldInfo?  _phoneStateReadEmailsField;
        private FieldInfo?  _phoneStateReadCharsField;
        private MethodInfo? _phoneStateEmailsMethod;
        private MethodInfo? _phoneStateChatsMethod;
        private FieldInfo?  _stCharField;          // ScriptTransparency.scriptCharacter_0
        private FieldInfo?  _stStatementsField;    // ScriptTransparency.gclass23_0[]
        private MethodInfo? _maybeBoolHasValue;
        private MethodInfo? _maybeBoolGetValue;
        private FieldInfo?  _charNameField;
        private MethodInfo? _charNameToString;

        // ── Per-activation state ───────────────────────────────────────
        private readonly bool?[] _lastAnswers   = new bool?[3];
        private readonly bool[]  _lastFocus     = new bool[7];
        private int  _lastEmailsRead = -1;
        private int  _lastChatsRead  = -1;
        // Which statement (0–2) the player's focus last landed on, or
        // -1 for "none / Submit / fresh." Used to skip re-announcing
        // the statement text when the player moves between Agree and
        // Disagree within the same row — mimics the container path-
        // diff that scope-tracking elements get for free.
        private int  _lastFocusedStatement = -1;
        // Snapshotted at OnEnter — the conclusion text for each of the
        // three statements. Used by FocusLabel so cursor focus on an
        // Agree/Disagree button reads back the statement it's voting on.
        private string[] _cachedStatements = Array.Empty<string>();

        public override bool IsActive => TransparencyState.IsActive;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _wsPhoneStateField = tWS.GetField(PhoneStateFieldName, inst);
            _wsAnswersField    = tWS.GetField(PlayerAnswersField, inst);
            _wsFocusField      = tWS.GetField(ButtonFocusField, inst);

            if (_wsAnswersField != null && _wsAnswersField.FieldType.IsArray)
            {
                Type elem = _wsAnswersField.FieldType.GetElementType()!;
                ResolveMaybeMethods(elem, out _maybeBoolHasValue, out _maybeBoolGetValue);
            }

            if (_wsPhoneStateField != null)
            {
                Type tPS = _wsPhoneStateField.FieldType;
                _phoneStateTransparencyField = tPS.GetField(TransparencyMaybeField, inst);
                _phoneStateReadEmailsField   = tPS.GetField(ReadEmailsSet, inst);
                _phoneStateReadCharsField    = tPS.GetField(ReadCharsSet, inst);
                _phoneStateEmailsMethod      = tPS.GetMethod(TransparencyEmailsMethod, inst);
                _phoneStateChatsMethod       = tPS.GetMethod(TransparencyChatsMethod, inst);

                if (_phoneStateTransparencyField != null)
                {
                    ResolveMaybeMethods(_phoneStateTransparencyField.FieldType,
                        out _maybeTransparencyHasValue, out _maybeTransparencyGetValue);
                    if (_maybeTransparencyGetValue != null)
                    {
                        Type tST = _maybeTransparencyGetValue.ReturnType;
                        _stCharField       = tST.GetField(ScriptTransparencyChar, inst);
                        _stStatementsField = tST.GetField(ScriptTransparencyState, inst);
                        if (_stCharField != null)
                        {
                            _charNameField = _stCharField.FieldType.GetField(CharNameField, inst);
                            if (_charNameField != null)
                            {
                                _charNameToString = _charNameField.FieldType.GetMethod(
                                    LocalizedTextToString,
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                            }
                        }
                    }
                }
            }

            Log($"[TransparencySub] init: " +
                $"answers={(_wsAnswersField != null)} focus={(_wsFocusField != null)} " +
                $"phoneState={(_wsPhoneStateField != null)} maybe4={(_phoneStateTransparencyField != null)} " +
                $"readEmails={(_phoneStateReadEmailsField != null)} readChars={(_phoneStateReadCharsField != null)} " +
                $"emailsMethod={(_phoneStateEmailsMethod != null)} chatsMethod={(_phoneStateChatsMethod != null)} " +
                $"stChar={(_stCharField != null)} stStatements={(_stStatementsField != null)} " +
                $"maybeBool.Has={(_maybeBoolHasValue != null)} Get={(_maybeBoolGetValue != null)} " +
                $"charName={(_charNameField != null)} toString={(_charNameToString != null)}");
        }

        public override void OnEnter()
        {
            // Reset per-activation deltas so the OnEnter announcement is
            // authoritative and subsequent OnUpdate ticks only chase
            // changes.
            for (int i = 0; i < 3; i++) _lastAnswers[i] = null;
            for (int k = 0; k < 7; k++) _lastFocus[k]   = false;
            _lastEmailsRead       = -1;
            _lastChatsRead        = -1;
            _lastFocusedStatement = -1;

            string patient   = ReadPatientName();
            int    emailRead = ReadReadEmails();
            int    emailTot  = ReadTotalEmails();
            int    chatRead  = ReadReadChats();
            int    chatTot   = ReadTotalChats();

            var parts = new System.Collections.Generic.List<string>
            {
                Loc.Fmt("transparency.header", patient)
            };
            if (emailRead >= 0 && emailTot >= 0)
                parts.Add(Loc.Fmt("transparency.emails_read", emailRead, emailTot));
            if (chatRead >= 0 && chatTot >= 0)
                parts.Add(Loc.Fmt("transparency.chats_read", chatRead, chatTot));
            parts.Add(Loc.Get("transparency.three_conclusions"));
            Speech.Speak(string.Join(" ", parts));

            // The conclusion text used to be spoken here as part of
            // OnEnter. Per accessibility feedback that produced a long
            // burst of speech with no way to review until the gated
            // buttons unlocked — moved to an on-demand Q-key handler
            // on HomeGridSubView, which pulls the cached snapshot
            // from TransparencyState.
            _cachedStatements = ReadStatements();
            bool?[]  answers  = ReadAnswers();
            TransparencyState.Statements = _cachedStatements;
            for (int i = 0; i < 3; i++)
            {
                _lastAnswers[i] = answers[i];
                TransparencyState.Answers[i] = answers[i];
            }

            _lastEmailsRead = emailRead;
            _lastChatsRead  = chatRead;

            Log($"[TransparencySub] entered: patient=\"{patient}\" " +
                $"emails={emailRead}/{emailTot} chats={chatRead}/{chatTot}");
        }

        public override void OnLeave()
        {
            // Clear the shared snapshot so HomeGridSubView's Q-key
            // handler can detect "no transparency questions active"
            // by an empty Statements array rather than IsActive alone
            // (IsActive is owned by SessionSubView's polling, which
            // may not have caught up to OnLeave's tick yet).
            TransparencyState.Statements = Array.Empty<string>();
            for (int i = 0; i < TransparencyState.Answers.Length; i++)
                TransparencyState.Answers[i] = null;
            Log("[TransparencySub] left.");
        }

        public override void OnUpdate()
        {
            // 1) Per-tick: did the player answer a statement?
            bool?[] answers = ReadAnswers();
            for (int i = 0; i < 3; i++)
            {
                if (answers[i] != _lastAnswers[i])
                {
                    if (answers[i].HasValue)
                    {
                        string verb = Loc.Get(answers[i]!.Value ? "transparency.verb_agreed" : "transparency.verb_disagreed");
                        Speech.Speak(Loc.Fmt("transparency.statement_answer", i + 1, verb));
                        Log($"[TransparencySub] answer changed: statement {i + 1} → {verb}");
                    }
                    _lastAnswers[i] = answers[i];
                    TransparencyState.Answers[i] = answers[i];
                }
            }

            // 2) Per-tick: did the controller cursor move to a new button?
            bool[] focus = ReadFocus();
            for (int k = 0; k < 7; k++)
            {
                if (focus[k] && !_lastFocus[k])
                {
                    SpeakFocus(k, answers);
                    Log($"[TransparencySub] focus → {k}");
                }
                _lastFocus[k] = focus[k];
            }

            // If focus has moved off all six agree/disagree buttons
            // (player tabbed to the inline home grid visible behind
            // the survey, or focus has briefly cleared between hotspot
            // rebuilds), reset the row tracker so the next focus into
            // any statement re-announces its context. Without this,
            // exiting a statement and returning would speak only the
            // radio because _lastFocusedStatement still pointed at
            // that row.
            bool anyStatementFocused = false;
            for (int k = 0; k < 6; k++) { if (focus[k]) { anyStatementFocused = true; break; } }
            if (!anyStatementFocused) _lastFocusedStatement = -1;

            // 3) Per-tick: did the read-emails / read-chats counters tick up?
            int emailRead = ReadReadEmails();
            int chatRead  = ReadReadChats();
            if (_lastEmailsRead >= 0 && emailRead != _lastEmailsRead)
            {
                int emailTot = ReadTotalEmails();
                if (emailTot >= 0)
                    Speech.Speak(Loc.Fmt("transparency.emails_read", emailRead, emailTot));
            }
            if (_lastChatsRead >= 0 && chatRead != _lastChatsRead)
            {
                int chatTot = ReadTotalChats();
                if (chatTot >= 0)
                    Speech.Speak(Loc.Fmt("transparency.chats_read", chatRead, chatTot));
            }
            _lastEmailsRead = emailRead;
            _lastChatsRead  = chatRead;
        }

        // ── Read helpers ───────────────────────────────────────────────

        private object? TryGetPhoneState()
        {
            if (_wsPhoneStateField == null) return null;
            try { return _wsPhoneStateField.GetValue(Parent); }
            catch { return null; }
        }

        private object? TryGetScriptTransparency()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null
                || _phoneStateTransparencyField == null
                || _maybeTransparencyHasValue == null
                || _maybeTransparencyGetValue == null) return null;
            try
            {
                object? maybe = _phoneStateTransparencyField.GetValue(phoneState);
                if (maybe == null) return null;
                if (!(bool)_maybeTransparencyHasValue.Invoke(maybe, null)!) return null;
                return _maybeTransparencyGetValue.Invoke(maybe, null);
            }
            catch { return null; }
        }

        private string ReadPatientName()
        {
            object? st = TryGetScriptTransparency();
            if (st == null || _stCharField == null) return "(unknown)";
            try
            {
                object? c = _stCharField.GetValue(st);
                if (c == null) return "(unknown)";
                if (_charNameField != null && _charNameToString != null)
                {
                    object? name = _charNameField.GetValue(c);
                    if (name != null)
                        return (string)_charNameToString.Invoke(name, null)! ?? c.ToString()!;
                }
                return c.ToString() ?? "(unknown)";
            }
            catch { return "(unknown)"; }
        }

        private string[] ReadStatements()
        {
            object? st = TryGetScriptTransparency();
            if (st == null || _stStatementsField == null) return Array.Empty<string>();
            try
            {
                if (!(_stStatementsField.GetValue(st) is Array arr)) return Array.Empty<string>();
                var result = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    // Each element is a GClass23 (localized-text wrapper).
                    // Use ToString() — the implicit-string conversion isn't
                    // triggered by `as string`, but ToString() is overridden
                    // on GClass23 to return the current-language string.
                    object? element = arr.GetValue(i);
                    result[i] = element?.ToString() ?? "";
                }
                return result;
            }
            catch { return Array.Empty<string>(); }
        }

        private bool?[] ReadAnswers()
        {
            var result = new bool?[3];
            if (_wsAnswersField == null
                || _maybeBoolHasValue == null
                || _maybeBoolGetValue == null) return result;
            try
            {
                if (!(_wsAnswersField.GetValue(Parent) is Array arr)) return result;
                int n = Math.Min(arr.Length, 3);
                for (int i = 0; i < n; i++)
                {
                    object? m = arr.GetValue(i);
                    if (m == null) continue;
                    if (!(bool)_maybeBoolHasValue.Invoke(m, null)!) continue;
                    result[i] = (bool)_maybeBoolGetValue.Invoke(m, null)!;
                }
            }
            catch { }
            return result;
        }

        private bool[] ReadFocus()
        {
            var result = new bool[7];
            if (_wsFocusField == null) return result;
            try
            {
                if (_wsFocusField.GetValue(Parent) is bool[] arr)
                {
                    int n = Math.Min(arr.Length, 7);
                    for (int k = 0; k < n; k++) result[k] = arr[k];
                }
            }
            catch { }
            return result;
        }

        private int ReadReadEmails()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null || _phoneStateReadEmailsField == null) return -1;
            try   { return CountCollection(_phoneStateReadEmailsField.GetValue(phoneState)); }
            catch { return -1; }
        }

        private int ReadTotalEmails()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null || _phoneStateEmailsMethod == null) return -1;
            try   { return CountCollection(_phoneStateEmailsMethod.Invoke(phoneState, null)); }
            catch { return -1; }
        }

        private int ReadReadChats()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null || _phoneStateReadCharsField == null) return -1;
            try   { return CountCollection(_phoneStateReadCharsField.GetValue(phoneState)); }
            catch { return -1; }
        }

        private int ReadTotalChats()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null || _phoneStateChatsMethod == null) return -1;
            try   { return CountCollection(_phoneStateChatsMethod.Invoke(phoneState, null)); }
            catch { return -1; }
        }

        // ── Labels ─────────────────────────────────────────────────────

        // Speak focus for the given button index. Statement context
        // is announced only when the player moves into a new
        // statement row (path-diff: same effect as wrapping each
        // statement in a RowContainer). The Agree/Disagree pair are
        // RadioButtonElements so they announce the standard
        // "{label}, radio button, selected / not selected" form.
        // Submit is a plain ButtonElement and clears the row tracker
        // so re-entering a statement re-announces it.
        private void SpeakFocus(int k, bool?[] answers)
        {
            if (k == 6)
            {
                _lastFocusedStatement = -1;
                Speech.Speak(new ButtonElement(Loc.Get("transparency.submit")).ResolveFocusText());
                return;
            }

            int statement = k / 2;
            if (statement != _lastFocusedStatement)
            {
                string text = statement < _cachedStatements.Length ? _cachedStatements[statement] : "";
                Speech.Speak(string.IsNullOrEmpty(text)
                    ? Loc.Fmt("transparency.statement_label", statement + 1)
                    : Loc.Fmt("transparency.statement_label_text", statement + 1, text));
                _lastFocusedStatement = statement;
            }

            bool   isAgree    = (k % 2 == 0);
            string side       = Loc.Get(isAgree ? "transparency.agree" : "transparency.disagree");
            bool?  ans        = statement < answers.Length ? answers[statement] : null;
            bool   isSelected = ans.HasValue && ans.Value == isAgree;
            var radio = new RadioButtonElement(side, isSelected: () => isSelected);
            Speech.Speak(radio.ResolveFocusText());
        }

        // ── Maybe<T> method resolution ─────────────────────────────────

        // Use the known obfuscated method names rather than the
        // signature-based heuristic — for Maybe<bool> HasValue and
        // GetValue both return bool, and a return-type filter can't
        // tell them apart.
        private static void ResolveMaybeMethods(Type maybeType,
            out MethodInfo? hasValue, out MethodInfo? getValue)
        {
            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            hasValue = maybeType.GetMethod(MaybeHasValueName, bf, null, Type.EmptyTypes, null);
            getValue = maybeType.GetMethod(MaybeGetValueName, bf, null, Type.EmptyTypes, null);
        }

        // Count a collection field whose runtime type might be a
        // HashSet<T> / List<T> / etc. None of these implement non-
        // generic ICollection, so `is ICollection` returns false. Fall
        // back to a Count property lookup, then to IEnumerable walking.
        private static int CountCollection(object? raw)
        {
            if (raw == null) return -1;
            // Most collection types expose Count via a CLR property.
            var prop = raw.GetType().GetProperty("Count",
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(int))
            {
                try
                {
                    object? v = prop.GetValue(raw);
                    if (v is int n) return n;
                }
                catch { }
            }
            if (raw is IEnumerable e)
            {
                int n = 0;
                foreach (object? _ in e) n++;
                return n;
            }
            return -1;
        }
    }
}
