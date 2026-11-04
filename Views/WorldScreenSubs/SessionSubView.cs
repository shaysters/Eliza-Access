using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Therapy-session lifecycle: tracks session start/end (with the
    /// patient's name) and announces the final summary from PhoneState's
    /// session-history list.
    ///
    /// Two signals are tracked:
    /// - <b>Formal sessions</b>: PhoneState.therapySession Maybe&lt;ScriptTransparency&gt;
    ///   is set for the duration (Transparency Mode). Start/end via
    ///   HasValue transition.
    /// - <b>Sessions list growth</b>: every completed session appends an
    ///   entry to PhoneState.sessions[] (verified WorldScreen.cs:3492-3509).
    ///   This is the most reliable end signal — reads back the same
    ///   character + rating that the post-session results screen displays
    ///   on screen.
    ///
    /// On session start we snapshot all int-valued no-arg getters on
    /// SaveData; on end we diff and log the non-zero deltas so the user
    /// (test play) can map obfuscated keys to human labels (money / level /
    /// etc). Diff is logged, not spoken.
    ///
    /// Migrated from V2SessionObserver. Dropped:
    /// - <c>OnSessionInactive</c> (screen-lost guard): SubView dispatch
    ///   already gates on WorldScreen being on top.
    /// - <c>_shadowSessionEndAnnounceTick</c>: was never read.
    /// </summary>
    internal sealed class SessionSubView : SubView
    {
        // ── Obfuscated names (stable across builds) ────────────────────
        private const string PhoneStateFieldName        = "#=q2pLuamZTCy6SsIMCZ5Ln6g==";
        private const string TherapySessionFieldName    = "#=qCr0dAEEjVt1iKTYcEjk1mg==";
        private const string SessionsListFieldName      = "#=qMboYkvsXmvmTq5xBhHGXoNSpjsPmUGHPsQZetCBwz5g=";
        private const string TherapyEntryCharField      = "#=qkeYpVY94ep5BzKAGLfITCw==";
        private const string TherapyEntryProgressField  = "#=qCe1i$pnpZ3qYZGGgSwnOcA==";
        private const string TherapyEntryNestedTypeName = "TherapySession";
        private const string TransparencyCharField      = "#=q4TjAdhPscprfeq1vnw8_DQ==";
        private const string CharacterNameField         = "#=qRCVVfHEfwjI6gO0__DKOzA==";
        private const string LocalizedTextToStringName  = "#=qsJzy3eMaJZxVcSjfk7qTJg==";
        private const string GameLogicInstanceField     = "#=qvRwww6t42W6jI4Ih8ken0A==";
        private const string GameLogicSaveDataField     = "#=q$Ym0T3iY98wD_$htXdfigA==";

        private const string MaybeHasValueName = "#=qNJtN2awYHS9$m49fwgFIAQ==";
        private const string MaybeGetValueName = "#=qzKSBQuHxXcvGUu3NWei4NQ==";

        // PhoneState.maybe_4 — Maybe<ScriptTransparency>. Set at
        // WorldScreen.cs:3702 when a ShowTransparency script event
        // fires (the "agree/disagree with Eliza's conclusions" survey
        // UI), cleared at :3721 when the mode ends. Used to detect
        // Transparency Mode so we can gate NarrationSub off — the
        // survey UI covers the AR display, so the bootloader /
        // sentiment-analyzer history isn't visible and shouldn't read.
        private const string TransparencyMaybeField = "#=qCr0dAEEjVt1iKTYcEjk1mg==";

        // PhoneState.maybe_0 — Maybe<IPhoneApp>. Set when the player
        // taps an app on the phone home grid; cleared when the app
        // closes. Mirrored to PhoneAppOpenState.IsInlineAppOpen each
        // tick so KeyboardToController can avoid double-firing Escape
        // (the inline phone consumes Key.ESCAPE for app-back; if we
        // also drove button 7 → FAKE_KEY_4, WorldScreen.method_12
        // would open the escape menu on the same press).
        private const string ActiveAppMaybeField = "#=qPchaqqvMkTQI5R3xY60obA==";

        // ── WorldScreen / PhoneState reflection ────────────────────────
        private FieldInfo?  _wsPhoneStateField;
        private FieldInfo?  _phoneStateTherapyField;
        private FieldInfo?  _phoneStateSessionsField;
        private FieldInfo?  _therapyEntryCharField;
        private FieldInfo?  _therapyEntryProgressField;
        private FieldInfo?  _scriptTransparencyCharField;
        private MethodInfo? _maybeTherapyHasValue;
        private MethodInfo? _maybeTherapyGetValue;
        private MethodInfo? _therapyProgressHasValue;
        private MethodInfo? _therapyProgressGetValue;
        private FieldInfo?  _charNameField;
        private MethodInfo? _charNameToString;

        // Transparency-Mode signal — PhoneState.maybe_4.HasValue.
        // Reflected in OnInit, polled in OnPoll, written to
        // TransparencyState for NarrationSub / TextingOverlaySub to
        // gate against.
        private FieldInfo?  _phoneStateTransparencyField;
        private MethodInfo? _maybeTransparencyHasValue;

        // Inline-phone-app-open signal — PhoneState.maybe_0.HasValue
        // AND TransparencyState.IsActive. Written to
        // PhoneAppOpenState.IsInlineAppOpen each tick in OnPoll.
        private FieldInfo?  _phoneStateActiveAppField;
        private MethodInfo? _maybeActiveAppHasValue;

        // ── Session-active signal (maybe_20 on WorldScreen) ──────────
        // The engine's authoritative "therapy session in progress" flag.
        // Set at BeginTherapy (worldScreen.maybe_20 = scriptCharacter_0
        // — WorldScreen.cs:29), cleared at EndTherapy cleanup (line 3511).
        // The AR-console narration-write block is gated on this same
        // field (line 3216 — `if (maybe_20.method_0() && !bool_13)`),
        // so mirroring it gives us the same boundary the engine uses for
        // narration generation.
        //
        // WorldScreen has two Maybe<ScriptCharacter> fields (maybe_14
        // for ShowTransparency / texting, maybe_20 for therapy). We
        // track both: ANY has value → session-like state. maybe_14
        // doesn't generate narration writes, so over-activating during
        // texting is harmless (NarrationSub stays idle).
        private readonly List<FieldInfo> _maybeScriptCharFields = new List<FieldInfo>();
        private MethodInfo? _maybeScriptCharHasValue;
        // Resolved to the maybe_20 instance (last in declaration order) once cataloged.
        private FieldInfo? _therapyCharacterField;

        // SaveData reflection (for the end-of-session int-counter diff log)
        private FieldInfo?  _gameLogicInstanceField;
        private FieldInfo?  _gameLogicSaveDataField;
        private MethodInfo[] _saveDataIntGetters = Array.Empty<MethodInfo>();

        // ── Per-activation state ───────────────────────────────────────
        private bool        _sessionActive;
        private string      _sessionCharName = "";
        private Dictionary<string, int> _saveDataSnapshot = new Dictionary<string, int>();

        // -1 = not yet primed; first poll captures whatever the savegame
        // already contains so historical entries don't replay.
        private int _lastSessionsCount = -1;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags instAll =
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _wsPhoneStateField = tWS.GetField(PhoneStateFieldName, instAll);
            Log($"[SessionSub] phoneStateField={Tag(_wsPhoneStateField)}");

            if (_wsPhoneStateField != null)
            {
                Type tPhoneState = _wsPhoneStateField.FieldType;

                _phoneStateSessionsField = tPhoneState.GetField(SessionsListFieldName, instAll);
                Type? tTherapyEntry = tPhoneState.GetNestedType(TherapyEntryNestedTypeName,
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (tTherapyEntry != null)
                {
                    _therapyEntryCharField     = tTherapyEntry.GetField(TherapyEntryCharField,     instAll);
                    _therapyEntryProgressField = tTherapyEntry.GetField(TherapyEntryProgressField, instAll);
                    if (_therapyEntryProgressField != null)
                        ResolveMaybeMethods(_therapyEntryProgressField.FieldType,
                            out _therapyProgressHasValue, out _therapyProgressGetValue);
                }

                _phoneStateTransparencyField = tPhoneState.GetField(TransparencyMaybeField, instAll);
                if (_phoneStateTransparencyField != null)
                {
                    ResolveMaybeMethods(_phoneStateTransparencyField.FieldType,
                        out _maybeTransparencyHasValue, out _);
                }

                _phoneStateActiveAppField = tPhoneState.GetField(ActiveAppMaybeField, instAll);
                if (_phoneStateActiveAppField != null)
                {
                    ResolveMaybeMethods(_phoneStateActiveAppField.FieldType,
                        out _maybeActiveAppHasValue, out _);
                }

                _phoneStateTherapyField = tPhoneState.GetField(TherapySessionFieldName, instAll);
                if (_phoneStateTherapyField != null)
                {
                    ResolveMaybeMethods(_phoneStateTherapyField.FieldType,
                        out _maybeTherapyHasValue, out _maybeTherapyGetValue);

                    if (_maybeTherapyGetValue != null)
                    {
                        Type tT = _maybeTherapyGetValue.ReturnType;  // ScriptTransparency
                        _scriptTransparencyCharField = tT.GetField(TransparencyCharField, instAll);
                        if (_scriptTransparencyCharField != null)
                        {
                            Type tChar = _scriptTransparencyCharField.FieldType;
                            _charNameField = tChar.GetField(CharacterNameField, instAll);
                            if (_charNameField != null)
                            {
                                _charNameToString = _charNameField.FieldType.GetMethod(
                                    LocalizedTextToStringName,
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                            }
                        }
                    }
                }
            }

            // Maybe<ScriptCharacter> fields on WorldScreen. The engine has
            // TWO: maybe_14 (ShowTransparency / texting partner — line
            // 763 in source) and maybe_20 (therapy session patient — line
            // 781). We want maybe_20 specifically: it's set at
            // BeginTherapy (line 29) and cleared at EndTherapy cleanup
            // (line 3511), and the AR-console narration write block at
            // line 3216 is gated on exactly this field.
            //
            // .NET reflection's GetFields() returns fields in
            // declaration order (metadata-table order, which Roslyn
            // emits in source order). The Eazfuscator obfuscator
            // renames but doesn't typically reorder, so maybe_20 is the
            // SECOND Maybe<ScriptCharacter> field. We pick by that
            // heuristic — and log the field names so if it's wrong we
            // can verify and swap.
            Type? tScriptCharacter = Nagoya.FindType("ScriptCharacter");
            if (tScriptCharacter != null)
            {
                foreach (FieldInfo f in tWS.GetFields(instAll))
                {
                    Type ft = f.FieldType;
                    if (!ft.IsGenericType) continue;
                    Type[] args = ft.GetGenericArguments();
                    if (args.Length != 1 || args[0] != tScriptCharacter) continue;
                    _maybeScriptCharFields.Add(f);
                    if (_maybeScriptCharHasValue == null)
                    {
                        ResolveMaybeMethods(ft, out _maybeScriptCharHasValue, out _);
                    }
                }
            }
            // If exactly two fields, the second is maybe_20 (therapy).
            // Otherwise fall back to the last one (treating any extras
            // as unknown but assuming declaration-order maybe_20-last).
            if (_maybeScriptCharFields.Count >= 2)
                _therapyCharacterField = _maybeScriptCharFields[_maybeScriptCharFields.Count - 1];
            else if (_maybeScriptCharFields.Count == 1)
                _therapyCharacterField = _maybeScriptCharFields[0];

            Log($"[SessionSub] maybeScriptCharFields={_maybeScriptCharFields.Count} " +
                $"hasValue={(_maybeScriptCharHasValue != null)}");
            for (int i = 0; i < _maybeScriptCharFields.Count; i++)
            {
                Log($"[SessionSub]   maybeScriptChar[{i}]: {_maybeScriptCharFields[i].Name}" +
                    (_maybeScriptCharFields[i] == _therapyCharacterField ? " (picked as maybe_20)" : ""));
            }

            // GameLogic.Instance.saveData → int-getter method list.
            Type? tGameLogic = Nagoya.FindType("GameLogic");
            if (tGameLogic != null)
            {
                _gameLogicInstanceField = tGameLogic.GetField(GameLogicInstanceField,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (_gameLogicInstanceField != null)
                {
                    Type tInstance = _gameLogicInstanceField.FieldType;
                    _gameLogicSaveDataField = tInstance.GetField(GameLogicSaveDataField, instAll);
                    if (_gameLogicSaveDataField != null)
                    {
                        Type tSaveData = _gameLogicSaveDataField.FieldType;
                        var getters = new List<MethodInfo>();
                        foreach (MethodInfo m in tSaveData.GetMethods(
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (m.GetParameters().Length == 0 && m.ReturnType == typeof(int))
                                getters.Add(m);
                        }
                        _saveDataIntGetters = getters.ToArray();
                    }
                }
            }

            Log($"[SessionSub] therapyField={Tag(_phoneStateTherapyField)} " +
                $"sessionsListField={Tag(_phoneStateSessionsField)} " +
                $"transparencyChar={Tag(_scriptTransparencyCharField)} " +
                $"transparencyMaybe={Tag(_phoneStateTransparencyField)} " +
                $"maybeTherapy.Has={_maybeTherapyHasValue != null} Get={_maybeTherapyGetValue != null} " +
                $"maybeTransp.Has={_maybeTransparencyHasValue != null} " +
                $"saveDataIntGetters={_saveDataIntGetters.Length}");
        }

        public override void OnPoll()
        {
            // Publish Transparency-Mode and inline-phone-app-open state
            // every tick. Runs regardless of IsActive so the values
            // stay current both during and after the survey, and so
            // NarrationSub / TextingOverlaySub / KeyboardToController
            // / ChatAppSubView (which read these in the same tick) see
            // fresh data.
            bool transparency = ReadTransparencyActive();
            TransparencyState.IsActive   = transparency;
            TransparencyState.SubjectName = transparency ? ReadTransparencySubjectName() : "";
            PhoneAppOpenState.IsInlineAppOpen =
                transparency && ReadActiveAppHasValue();
        }

        private string ReadTransparencySubjectName()
        {
            if (_wsPhoneStateField == null
                || _phoneStateTherapyField == null
                || _maybeTherapyGetValue == null
                || _scriptTransparencyCharField == null) return "";
            try
            {
                object? phoneState = _wsPhoneStateField.GetValue(Parent);
                if (phoneState == null) return "";
                object? maybe = _phoneStateTherapyField.GetValue(phoneState);
                if (maybe == null) return "";
                object? transparency = _maybeTherapyGetValue.Invoke(maybe, null);
                if (transparency == null) return "";
                object? charObj = _scriptTransparencyCharField.GetValue(transparency);
                if (charObj == null) return "";
                return ResolveCharName(charObj);
            }
            catch { return ""; }
        }

        private bool ReadTransparencyActive()
        {
            if (_wsPhoneStateField == null
                || _phoneStateTransparencyField == null
                || _maybeTransparencyHasValue == null) return false;
            try
            {
                object? phoneState = _wsPhoneStateField.GetValue(Parent);
                if (phoneState == null) return false;
                object? maybe = _phoneStateTransparencyField.GetValue(phoneState);
                if (maybe == null) return false;
                return (bool)_maybeTransparencyHasValue.Invoke(maybe, null)!;
            }
            catch { return false; }
        }

        private bool ReadActiveAppHasValue()
        {
            if (_wsPhoneStateField == null
                || _phoneStateActiveAppField == null
                || _maybeActiveAppHasValue == null) return false;
            try
            {
                object? phoneState = _wsPhoneStateField.GetValue(Parent);
                if (phoneState == null) return false;
                object? maybe = _phoneStateActiveAppField.GetValue(phoneState);
                if (maybe == null) return false;
                return (bool)_maybeActiveAppHasValue.Invoke(maybe, null)!;
            }
            catch { return false; }
        }

        public override void OnUpdate()
        {
            if (_wsPhoneStateField == null) return;
            object? phoneState;
            try { phoneState = _wsPhoneStateField.GetValue(Parent); }
            catch (Exception ex)
            {
                LogOnce(ref _loggedPhoneStateError, $"[SessionSub] phoneState read threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (phoneState == null)
            {
                LogOnce(ref _loggedPhoneStateNull, "[SessionSub] phoneState is null (PhoneState not yet populated)");
                return;
            }

            // Sessions-list growth is the most reliable end signal for
            // every session type — fires on every completed session
            // regardless of how the therapy field transitioned.
            PollSessionsListGrowth(phoneState);

            if (_phoneStateTherapyField == null || _maybeTherapyHasValue == null) return;

            object? therapyMaybe;
            try { therapyMaybe = _phoneStateTherapyField.GetValue(phoneState); }
            catch { return; }
            if (therapyMaybe == null)
            {
                LogOnce(ref _loggedTherapyMaybeNull, "[SessionSub] therapyMaybe is null (Maybe<ScriptTransparency> field returned null)");
                return;
            }

            bool hasSession;
            try { hasSession = (bool)_maybeTherapyHasValue.Invoke(therapyMaybe, null)!; }
            catch (Exception ex)
            {
                LogOnce(ref _loggedHasSessionError, $"[SessionSub] hasSession invoke threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            LogOnce(ref _loggedFirstHasSession, $"[SessionSub] data readable: first hasSession poll = {hasSession}");

            if (hasSession && !_sessionActive)      OnSessionStart(therapyMaybe);
            else if (!hasSession && _sessionActive) OnSessionEnd();

            // Surface a stable in-session signal for cross-observer use
            // (NarrationSubView's gate, ChoiceSubView's proxy-response
            // marker). The engine sets maybe_20 (Maybe<ScriptCharacter>)
            // on WorldScreen at BeginTherapy and clears it at EndTherapy
            // cleanup; the narration-write block at WorldScreen.cs:3216
            // is gated on this exact field. By tracking only THAT field
            // we avoid false-positives from maybe_14, which holds the
            // chat-partner ScriptCharacter during text conversations —
            // a different in-game UI that shouldn't trigger session-
            // mode behaviors.
            SessionState.IsActive = IsTherapyCharacterSet() || _sessionActive;
        }

        private bool IsTherapyCharacterSet()
        {
            if (_therapyCharacterField == null || _maybeScriptCharHasValue == null)
                return false;
            try
            {
                object? boxed = _therapyCharacterField.GetValue(Parent);
                if (boxed == null) return false;
                return (bool)_maybeScriptCharHasValue.Invoke(boxed, null)!;
            }
            catch { return false; }
        }

        // Single-shot diagnostic flags — flip once each so the first
        // failure of each kind is logged without flooding subsequent polls.
        private bool _loggedPhoneStateError;
        private bool _loggedPhoneStateNull;
        private bool _loggedTherapyMaybeNull;
        private bool _loggedHasSessionError;
        private bool _loggedFirstHasSession;

        private void LogOnce(ref bool flag, string message)
        {
            if (flag) return;
            flag = true;
            Log(message);
        }

        // ── Session lifecycle ──────────────────────────────────────────

        private void OnSessionStart(object therapyMaybe)
        {
            _sessionActive    = true;
            _sessionCharName  = "";
            _saveDataSnapshot = SnapshotSaveData();

            try
            {
                if (_maybeTherapyGetValue != null && _scriptTransparencyCharField != null)
                {
                    object? transparency = _maybeTherapyGetValue.Invoke(therapyMaybe, null);
                    if (transparency != null)
                    {
                        object? charObj = _scriptTransparencyCharField.GetValue(transparency);
                        if (charObj != null) _sessionCharName = ResolveCharName(charObj);
                    }
                }
            }
            catch { }

            // No spoken "session started" announcement — the AR
            // bootloader text NarrationSub reads on the same transition
            // covers the same ground (and reliably; this path fires for
            // Transparency Mode too, where calling it a "therapy
            // session" was outright wrong).
            Log($"[SessionSub] start: character=\"{_sessionCharName}\" snapshotKeys={_saveDataSnapshot.Count}");
        }

        private void OnSessionEnd()
        {
            // Diff SaveData; logged only, used to map obfuscated counter
            // names to human labels through test play.
            var post = SnapshotSaveData();
            var deltas = new List<string>();
            foreach (var kv in post)
            {
                if (_saveDataSnapshot.TryGetValue(kv.Key, out int old) && old != kv.Value)
                    deltas.Add($"{kv.Key.Substring(0, Math.Min(8, kv.Key.Length))}={kv.Value - old:+0;-0}");
            }

            Log($"[SessionSub] end: deltas=[{string.Join(", ", deltas)}]");

            _sessionActive = false;
            _saveDataSnapshot.Clear();
        }

        // ── Sessions-list growth (post-session results announcement) ───

        private void PollSessionsListGrowth(object phoneState)
        {
            if (_phoneStateSessionsField == null) return;
            int count;
            try
            {
                if (!(_phoneStateSessionsField.GetValue(phoneState) is ICollection list)) return;
                count = list.Count;
            }
            catch { return; }

            if (_lastSessionsCount < 0)
            {
                _lastSessionsCount = count;
                Log($"[SessionSub] sessions list primed at count={count}");
                return;
            }
            if (count <= _lastSessionsCount) return;

            int added = count - _lastSessionsCount;
            _lastSessionsCount = count;

            try
            {
                if (!(_phoneStateSessionsField.GetValue(phoneState) is IList ilist)) return;
                object? entry = ilist[ilist.Count - 1];
                if (entry == null) return;

                string charName = "";
                if (_therapyEntryCharField != null)
                {
                    object? c = _therapyEntryCharField.GetValue(entry);
                    if (c != null) charName = ResolveCharName(c);
                }

                string rating = "in progress";
                if (_therapyEntryProgressField != null
                    && _therapyProgressHasValue != null
                    && _therapyProgressGetValue != null)
                {
                    object? prog = _therapyEntryProgressField.GetValue(entry);
                    if (prog != null && (bool)_therapyProgressHasValue.Invoke(prog, null)!)
                    {
                        int n = (int)_therapyProgressGetValue.Invoke(prog, null)!;
                        rating = $"{n} of 5 stars";
                    }
                }

                string spoken = string.IsNullOrWhiteSpace(charName)
                    ? $"Session results: {rating}."
                    : $"Session results with {charName}: {rating}.";
                // Don't speak — SessionResultsSubView reads the full
                // results panel during the EndTherapy screen, and that
                // fires for every session end. This list-growth path
                // was the V2 backstop before the panel dump existed;
                // keeping it active double-announces after Continue is
                // pressed. Tracking is preserved so the log still
                // shows when the list grew.
                Log($"[SessionSub] results (suppressed — covered by SessionResultsSub): \"{spoken}\" (added={added}, listCount={count})");
            }
            catch (Exception ex)
            {
                Log($"[SessionSub] sessions list read error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Reads ──────────────────────────────────────────────────────

        private Dictionary<string, int> SnapshotSaveData()
        {
            var snap = new Dictionary<string, int>();
            if (_gameLogicInstanceField == null || _gameLogicSaveDataField == null) return snap;
            try
            {
                object? instance = _gameLogicInstanceField.GetValue(null);
                if (instance == null) return snap;
                object? saveData = _gameLogicSaveDataField.GetValue(instance);
                if (saveData == null) return snap;
                foreach (MethodInfo m in _saveDataIntGetters)
                {
                    try
                    {
                        if (m.Invoke(saveData, null) is int i) snap[m.Name] = i;
                    }
                    catch { }
                }
            }
            catch { }
            return snap;
        }

        private string ResolveCharName(object character)
        {
            try
            {
                if (_charNameField != null && _charNameToString != null)
                {
                    object? nameObj = _charNameField.GetValue(character);
                    if (nameObj != null)
                        return (string)_charNameToString.Invoke(nameObj, null)! ?? "";
                }
            }
            catch { }
            return character.ToString() ?? "";
        }

        // ── Helpers ────────────────────────────────────────────────────

        private static void ResolveMaybeMethods(Type maybeType, out MethodInfo? hasValue, out MethodInfo? getValue)
        {
            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            hasValue = maybeType.GetMethod(MaybeHasValueName, bf, null, Type.EmptyTypes, null);
            getValue = maybeType.GetMethod(MaybeGetValueName, bf, null, Type.EmptyTypes, null);
        }

        private static string Tag(object? x) => x != null ? "[found]" : "NOT FOUND";
    }
}
