using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Therapy-session lifecycle: announces session start/end (with the
    /// patient's name), the live 3-star rating as it shifts during the
    /// session, and the final summary from PhoneState's session-history
    /// list.
    ///
    /// Star semantics (WorldScreen.cs:3760, 3777 — Maybe&lt;bool&gt;[3]):
    ///   Maybe.None        = not yet evaluated this session
    ///   Maybe.Some(true)  = good outcome on that dimension
    ///   Maybe.Some(false) = bad outcome on that dimension
    /// The three dimensions are character-specific (each ScriptTransparency
    /// picks what the patient cares about), so we use generic "Star 1/2/3"
    /// labels.
    ///
    /// Three intertwined session signals are tracked because the game uses
    /// two distinct session shapes:
    /// - <b>Formal sessions</b>: PhoneState.therapySession Maybe&lt;ScriptTransparency&gt;
    ///   is set for the duration. Start/end via HasValue transition.
    /// - <b>Shadow sessions</b>: the first-chapter Darren-style scenes
    ///   update the stars array WITHOUT ever setting PhoneState.therapySession.
    ///   Detected from the star array directly: any non-None star starts;
    ///   all-None again ends.
    /// - <b>Sessions list growth</b>: every completed session (formal OR
    ///   shadow) appends an entry to PhoneState.sessions[] (verified
    ///   WorldScreen.cs:3492-3509). This is the most reliable end signal —
    ///   reads back the same character + rating that the post-session
    ///   results screen displays on screen.
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
        private const string PhoneStateFieldName        = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string StarsArrayFieldName        = "#=q$$5T9P5y1c2ItVFX4ngYaVWZ8JLSrGDdr8OpdXz$Wmg=";
        private const string TherapySessionFieldName    = "#=q_jj7ZK0kGqgOw$34jWXLPA==";
        private const string SessionsListFieldName      = "#=qbKHlo689MZLcwopVAqFTlfmVi1PcGmOEIM9Sf8iXAXw=";
        private const string TherapyEntryCharField      = "#=qLFu0a0kAeGPfR7pdByCKRg==";
        private const string TherapyEntryProgressField  = "#=qRjgOIUotpixwH_ebqFNRQQ==";
        private const string TherapyEntryNestedTypeName = "TherapySession";
        private const string TransparencyCharField      = "#=q5wd5DblUTkgorw0HqYia3w==";
        private const string CharacterNameField         = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string LocalizedTextToStringName  = "#=qTivbHO_rGrDAcTGXa1E4rg==";
        private const string GameLogicInstanceField     = "#=qdf_jbonLPSi33E8HSVNUxQ==";
        private const string GameLogicSaveDataField     = "#=qS1Z20vlzJ0bmCV7BUDCMGg==";

        private const string MaybeHasValueName = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValueName = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        // PhoneState.maybe_4 — Maybe<ScriptTransparency>. Set at
        // WorldScreen.cs:3702 when a ShowTransparency script event
        // fires (the "agree/disagree with Eliza's conclusions" survey
        // UI), cleared at :3721 when the mode ends. Used to detect
        // Transparency Mode so we can gate NarrationSub off — the
        // survey UI covers the AR display, so the bootloader /
        // sentiment-analyzer history isn't visible and shouldn't read.
        private const string TransparencyMaybeField = "#=q_jj7ZK0kGqgOw$34jWXLPA==";

        // PhoneState.maybe_0 — Maybe<IPhoneApp>. Set when the player
        // taps an app on the phone home grid; cleared when the app
        // closes. Mirrored to PhoneAppOpenState.IsInlineAppOpen each
        // tick so KeyboardToController can avoid double-firing Escape
        // (the inline phone consumes Key.ESCAPE for app-back; if we
        // also drove button 7 → FAKE_KEY_4, WorldScreen.method_12
        // would open the escape menu on the same press).
        private const string ActiveAppMaybeField = "#=qS3nVDip0NyI_pzkywVHdyQ==";

        // ── WorldScreen / PhoneState reflection ────────────────────────
        private FieldInfo?  _wsPhoneStateField;
        private FieldInfo?  _wsStarsArrayField;
        private FieldInfo?  _phoneStateTherapyField;
        private FieldInfo?  _phoneStateSessionsField;
        private FieldInfo?  _therapyEntryCharField;
        private FieldInfo?  _therapyEntryProgressField;
        private FieldInfo?  _scriptTransparencyCharField;
        private MethodInfo? _maybeTherapyHasValue;
        private MethodInfo? _maybeTherapyGetValue;
        private MethodInfo? _maybeBoolHasValue;
        private MethodInfo? _maybeBoolGetValue;
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
        // null = not-yet-rated; true/false = rating
        private bool?[]     _prevStars = new bool?[3];
        private Dictionary<string, int> _saveDataSnapshot = new Dictionary<string, int>();

        // Shadow-session state — first-chapter sessions where the formal
        // therapySession field never flips (Darren etc.). We snapshot the
        // latest non-null stars during the run so we can report them when
        // all three return to null (session-results screen has finished).
        private bool    _shadowSessionActive;
        private bool?[] _shadowFinalStars = new bool?[3];
        // Shadow detection requires stars to have been all-None since
        // the last real session ended. Without this, the same poll
        // that flips _sessionActive=false (in OnSessionEnd) sees stars
        // STILL set from the just-finished session and starts a
        // spurious shadow that never ends (stars don't auto-clear
        // until the next session begins), leaving SessionState.IsActive
        // permanently true — which then leaks into the proxy-response
        // marker, narration gating, etc. Starts armed so the intro/
        // tutorial flow (no formal session field, stars-only) still
        // fires correctly.
        private bool    _shadowArmed = true;

        // -1 = not yet primed; first poll captures whatever the savegame
        // already contains so historical entries don't replay.
        private int _lastSessionsCount = -1;

        // Diagnostic — log once when ANY star first becomes non-None so we
        // can confirm shadow sessions trip the array even when
        // therapySession.HasValue() stays false.
        private bool _seenAnyStar;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags instAll =
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _wsPhoneStateField = tWS.GetField(PhoneStateFieldName, instAll);
            _wsStarsArrayField = tWS.GetField(StarsArrayFieldName, instAll);
            Log($"[SessionSub] phoneStateField={Tag(_wsPhoneStateField)} starsArrayField={Tag(_wsStarsArrayField)}");

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

            // Maybe<bool> reflection — derive type from the stars array element type.
            if (_wsStarsArrayField != null && _wsStarsArrayField.FieldType.IsArray)
            {
                Type elemType = _wsStarsArrayField.FieldType.GetElementType()!;
                ResolveMaybeMethods(elemType, out _maybeBoolHasValue, out _maybeBoolGetValue);
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
                $"maybeBool.Has={_maybeBoolHasValue != null} Get={_maybeBoolGetValue != null} " +
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
            // Star polling runs unconditionally. The pre-migration
            // V2SessionObserver gated star reading on phoneState +
            // therapyMaybe being readable, but in the intro shadow session
            // (Darren, no formal therapySession field) one of those reads
            // was returning null and short-circuiting the whole observer —
            // which is why the session ended silent. Stars come straight
            // off WorldScreen so they don't depend on PhoneState at all.
            PollStarChanges();

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

            // Sessions-list growth is the most reliable end signal for both
            // formal and shadow sessions — fires on every completed session
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
            SessionState.IsActive = IsTherapyCharacterSet()
                                    || _sessionActive
                                    || _shadowSessionActive;
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
            _prevStars        = new bool?[3];
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
            // Read final star tally — still set on the WorldScreen instance
            // until the next session resets it.
            int good = 0, bad = 0, none = 0;
            bool?[] finalStars = ReadStars();
            for (int i = 0; i < 3; i++)
            {
                if (finalStars[i] == true)  good++;
                else if (finalStars[i] == false) bad++;
                else none++;
            }

            // Diff SaveData; logged only, used to map obfuscated counter
            // names to human labels through test play.
            var post = SnapshotSaveData();
            var deltas = new List<string>();
            foreach (var kv in post)
            {
                if (_saveDataSnapshot.TryGetValue(kv.Key, out int old) && old != kv.Value)
                    deltas.Add($"{kv.Key.Substring(0, Math.Min(8, kv.Key.Length))}={kv.Value - old:+0;-0}");
            }

            Speech.Speak(Loc.Fmt("session.ended_stars", good));
            Log($"[SessionSub] end: good={good} bad={bad} unrated={none} deltas=[{string.Join(", ", deltas)}]");

            _sessionActive = false;
            _saveDataSnapshot.Clear();
            // Disarm shadow detection until stars first go all-None.
            // The stars on the engine instance still hold this session's
            // final tally; without disarming, PollStarChanges on the
            // very next poll mistakes them for a new shadow session.
            _shadowArmed = false;
        }

        // ── Star changes (live per-rating updates + shadow detection) ──

        private void PollStarChanges()
        {
            bool?[] stars = ReadStars();

            if (!_seenAnyStar)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (stars[i] != null)
                    {
                        _seenAnyStar = true;
                        Log($"[SessionSub] first star activity: stars=[{Fmt(stars[0])},{Fmt(stars[1])},{Fmt(stars[2])}] sessionActive={_sessionActive}");
                        break;
                    }
                }
            }

            // Shadow-session detection — fires only when no formal session
            // is active. First non-None star starts the shadow session;
            // all-None again ends it (results screen has finished its run).
            if (!_sessionActive)
            {
                bool anyActive = stars[0] != null || stars[1] != null || stars[2] != null;
                if (!anyActive) _shadowArmed = true;
                if (anyActive && !_shadowSessionActive && _shadowArmed)
                {
                    _shadowSessionActive = true;
                    Log("[SessionSub] shadow session started (first star activated, no therapy session field).");
                }
                else if (!anyActive && _shadowSessionActive)
                {
                    _shadowSessionActive = false;
                    int good = 0, bad = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        if (_shadowFinalStars[i] == true)  good++;
                        else if (_shadowFinalStars[i] == false) bad++;
                    }
                    Speech.Speak(Loc.Fmt("session.results_stars", good));
                    Log($"[SessionSub] shadow ended. good={good} bad={bad} stars=[{Fmt(_shadowFinalStars[0])},{Fmt(_shadowFinalStars[1])},{Fmt(_shadowFinalStars[2])}]");
                    _shadowFinalStars = new bool?[3];
                }
                // Always remember the latest non-null stars in case all
                // three flip to null on the same poll as a star resolves.
                if (anyActive)
                    for (int i = 0; i < 3; i++) if (stars[i] != null) _shadowFinalStars[i] = stars[i];
            }

            for (int i = 0; i < 3; i++)
            {
                if (stars[i] == _prevStars[i]) continue;
                _prevStars[i] = stars[i];
                string what = stars[i] == true  ? $"Star {i + 1}: positive."
                            : stars[i] == false ? $"Star {i + 1}: negative."
                            :                     $"Star {i + 1}: cleared.";
                Speech.Speak(what);
                Log($"[SessionSub] {what}");
            }
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

        private bool?[] ReadStars()
        {
            var result = new bool?[3];
            if (_wsStarsArrayField == null) return result;
            try
            {
                if (!(_wsStarsArrayField.GetValue(Parent) is Array arr)) return result;
                int n = Math.Min(3, arr.Length);
                for (int i = 0; i < n; i++)
                {
                    object? maybe = arr.GetValue(i);
                    if (maybe == null) continue;
                    if (_maybeBoolHasValue == null) continue;
                    if (!(bool)_maybeBoolHasValue.Invoke(maybe, null)!) continue;
                    if (_maybeBoolGetValue == null) continue;
                    if (_maybeBoolGetValue.Invoke(maybe, null) is bool b) result[i] = b;
                }
            }
            catch { }
            return result;
        }

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

        private static string Fmt(bool? b) => b == null ? "none" : (b.Value ? "good" : "bad");
        private static string Tag(object? x) => x != null ? "[found]" : "NOT FOUND";
    }
}
