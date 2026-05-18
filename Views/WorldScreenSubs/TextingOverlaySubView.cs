using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Announces in-VN text-message overlays — the chat UI that pops up
    /// during normal VN scenes when a character texts the player. The
    /// rendering is done by <c>ChatApp.smethod_0(...)</c> called inline
    /// from <c>WorldScreen</c> (WorldScreen.cs:1759/1762), so the player
    /// never leaves WorldScreen — there is no PhoneScreen on the stack.
    /// That's why <see cref="PhoneScreenSubs.ChatAppSubView"/> doesn't
    /// see these: its IsActive requires PhoneScreen + activeApp==ChatApp.
    ///
    /// Trigger: <c>WorldScreen.maybe_14</c> is the texting partner
    /// <c>ScriptCharacter</c> (declared at WorldScreen.cs:763, set at
    /// :377, cleared at :4339). When it has value, the overlay is up.
    /// The corresponding message body is appended to
    /// <c>PhoneState.chatHistory[character][language]</c> via
    /// <c>PhoneState.method_13</c> — the same dictionary
    /// <see cref="PhoneScreenSubs.ChatAppSubView"/> reads from.
    ///
    /// Sub-state per overlay activation:
    /// - OnEnter: announce "Text from {name}." then speak every message
    ///   in the conversation the player hasn't already heard for that
    ///   character (per-character <see cref="_readCountByChar"/>
    ///   tracker). First contact reads the whole thread; subsequent
    ///   overlays for the same character on the same play session read
    ///   only the delta.
    /// - OnUpdate: poll the same chat-history list for new entries
    ///   appended while the overlay is still up (back-and-forth flow
    ///   while the player is reading and replying).
    ///
    /// Type filter mirrors <see cref="PhoneScreenSubs.ChatAppSubView"/>:
    /// type==0 (separator) dropped, type==1 (player echo) suppressed
    /// because the player just confirmed that line through ChoiceSub,
    /// type==2 (NPC) spoken with "{partnerName}:" prefix.
    /// </summary>
    internal sealed class TextingOverlaySubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        private const string PhoneStateFieldName       = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string ChatHistoryMethodName     = "#=qZgd2CiTPtk7XHtkZLehK97OCgnLoGwT3R_ZjmbiiG68=";
        private const string CharacterNameField        = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string LocalizedTextToStringName = "#=qTivbHO_rGrDAcTGXa1E4rg==";
        private const string EntryTypeField            = "#=qaG2NfIrHQHsZSpfYKbj8jQ==";
        private const string EntryTextField            = "#=qyUpzQtPynmEGvfYtNiwRrg==";

        // Player character display name. The game hardcodes this in
        // GClass146.scriptCharacter_0 — the localized-text wrapper is
        // built from the literal "Evelyn" with empty alternatives for
        // every other locale, so there's no live game state to read
        // that would give a different answer.
        private const string PlayerName = "Evelyn";

        // Cap how many messages the OnEnter history read speaks. The
        // thread history can grow to dozens of messages across a
        // playthrough; reading the full backlog on every overlay open
        // is too much. Latest few give enough context to answer.
        private const int MaxOpenReadMessages = 3;

        // ── WorldScreen-level reflection (OnInit) ──────────────────────
        // First Maybe<ScriptCharacter> field by declaration order on
        // WorldScreen. SessionSubView documents that this is maybe_14
        // (texting) and the LAST one is maybe_20 (therapy). We want the
        // first.
        private FieldInfo?  _textingCharField;
        private MethodInfo? _maybeCharHasValue;
        private MethodInfo? _maybeCharGetValue;
        private FieldInfo?  _phoneStateField;
        private MethodInfo? _chatHistoryMethod;

        // ── Deferred reflection (first live instance) ──────────────────
        private bool        _charReflectDone;
        private FieldInfo?  _charNameField;
        private MethodInfo? _charNameToStringMethod;
        private bool        _entryReflectDone;
        private FieldInfo?  _entryTypeField;
        private FieldInfo?  _entryTextField;

        // ── Per-activation state ───────────────────────────────────────
        private int  _conversationLastMsgCount = -1;
        private readonly Dictionary<string, int> _readCountByChar = new Dictionary<string, int>();

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // First Maybe<ScriptCharacter> field on WorldScreen — that's
            // maybe_14 (texting partner). Mirrors SessionSubView's
            // declaration-order heuristic; that sub picks the LAST one
            // (maybe_20 / therapy patient), we pick the FIRST.
            Type? tScriptChar = Nagoya.FindType("ScriptCharacter");
            if (tScriptChar != null)
            {
                foreach (FieldInfo f in tWS.GetFields(inst))
                {
                    Type ft = f.FieldType;
                    if (!ft.IsGenericType) continue;
                    Type[] args = ft.GetGenericArguments();
                    if (args.Length != 1 || args[0] != tScriptChar) continue;
                    _textingCharField = f;
                    ResolveMaybeMethods(ft, out _maybeCharHasValue, out _maybeCharGetValue);
                    break;
                }
            }

            _phoneStateField = tWS.GetField(PhoneStateFieldName, inst);
            if (_phoneStateField != null)
            {
                _chatHistoryMethod = _phoneStateField.FieldType.GetMethod(
                    ChatHistoryMethodName, inst);
            }

            Log($"[TextingOverlay] init: textingChar={(_textingCharField != null)} " +
                $"maybeChar.Has={(_maybeCharHasValue != null)} Get={(_maybeCharGetValue != null)} " +
                $"phoneState={(_phoneStateField != null)} chatHistory={(_chatHistoryMethod != null)}");
        }

        public override bool IsActive
        {
            get
            {
                // Transparency Mode also sets maybe_14 (the same field
                // we track) to package the chat-history view as part of
                // its survey UI. We don't want a stray "Text from X."
                // announcement when the player opens that screen — the
                // survey is a different concern. TransparencyState is
                // written by SessionSubView's OnPoll, which runs before
                // this sub each tick.
                if (TransparencyState.IsActive) return false;
                if (_textingCharField == null || _maybeCharHasValue == null) return false;
                try
                {
                    object? maybe = _textingCharField.GetValue(Parent);
                    if (maybe == null) return false;
                    return (bool)_maybeCharHasValue.Invoke(maybe, null)!;
                }
                catch { return false; }
            }
        }

        public override void OnPoll()
        {
            // Publish the overlay state every tick so DialogueSubView
            // can gate its dialogue-line speech path off. OnPoll runs
            // every tick regardless of IsActive, so the value stays
            // current both when the overlay is up and after it closes.
            TextingState.IsActive = IsActive;
        }

        public override void OnEnter()
        {
            _conversationLastMsgCount = -1;
            DoCharReflect();

            object? character = TryGetTextingCharacter();
            if (character == null)
            {
                Log("[TextingOverlay] entered but character resolution failed");
                return;
            }
            object? phoneState = TryGetPhoneState();
            if (phoneState == null)
            {
                Log("[TextingOverlay] entered but phoneState resolution failed");
                return;
            }

            SpeakConversationEntry(phoneState, character);
        }

        public override void OnLeave()
        {
            _conversationLastMsgCount = -1;
            Log("[TextingOverlay] overlay closed.");
        }

        public override void OnUpdate()
        {
            object? character = TryGetTextingCharacter();
            if (character == null) return;
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return;
            SpeakNewMessages(phoneState, character);
        }

        // ── Reads ──────────────────────────────────────────────────────

        private object? TryGetPhoneState()
        {
            if (_phoneStateField == null) return null;
            try { return _phoneStateField.GetValue(Parent); }
            catch { return null; }
        }

        private object? TryGetTextingCharacter()
        {
            if (_textingCharField == null ||
                _maybeCharHasValue == null || _maybeCharGetValue == null) return null;
            try
            {
                object? maybe = _textingCharField.GetValue(Parent);
                if (maybe == null) return null;
                if (!(bool)_maybeCharHasValue.Invoke(maybe, null)!) return null;
                return _maybeCharGetValue.Invoke(maybe, null);
            }
            catch { return null; }
        }

        private void SpeakConversationEntry(object phoneState, object character)
        {
            try
            {
                string name = GetCharName(character);
                List<object> msgs = GetMessages(phoneState, character);
                int curCount = msgs.Count;

                // Per-character read tracker: first overlay for this
                // character reads the whole thread (context for what
                // the player is replying to); a later overlay for the
                // same character — possible if a script triggers them
                // back-to-back — reads only what's new since last visit.
                //
                // Also cap to the most recent few messages on first
                // open. The thread can grow to dozens of messages over
                // the course of a playthrough, and dumping all of them
                // at once when the overlay pops up is too much for the
                // player to follow — the latest few are what they need
                // for context to answer.
                int lastSpoken = _readCountByChar.TryGetValue(name, out int v) ? v : 0;
                int newCount   = curCount - lastSpoken;
                int startIdx   = Math.Max(lastSpoken, curCount - MaxOpenReadMessages);
                if (startIdx < 0) startIdx = 0;

                Speech.Speak($"Text from {name}.");
                // History read: speak player lines too. The echo-
                // suppression that SpeakNewMessages applies is only
                // valid for messages the player JUST chose — for the
                // historical thread the player needs to hear both
                // sides to follow the conversation.
                for (int i = startIdx; i < curCount; i++)
                    SpeakChatEntry(msgs[i], name, suppressPlayer: false);

                _readCountByChar[name]    = curCount;
                _conversationLastMsgCount = curCount;

                Log($"[TextingOverlay] opened: {name}, {curCount} msgs, {newCount} new since last visit, " +
                    $"spoke msgs[{startIdx}..{curCount}) ({curCount - startIdx} of last {MaxOpenReadMessages}).");
            }
            catch (Exception ex)
            {
                Log($"[TextingOverlay] SpeakConversationEntry error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SpeakNewMessages(object phoneState, object character)
        {
            try
            {
                List<object> msgs = GetMessages(phoneState, character);
                if (_conversationLastMsgCount >= 0 && msgs.Count > _conversationLastMsgCount)
                {
                    int newCount = msgs.Count - _conversationLastMsgCount;
                    string charName = GetCharName(character);

                    // Only speak the most recent entry. If multiple
                    // arrived in a single tick (the NPC dropped a
                    // multi-bubble burst, or a slow poll batched a few
                    // together), reading all of them queues a long
                    // stream where each new arrival sounds like it
                    // appended onto the previous one. Player wants the
                    // current message, not a recap — they can re-open
                    // via the phone Chat app for earlier history. The
                    // OnEnter path (SpeakConversationEntry) is the
                    // exception: it speaks everything for context.
                    if (newCount > 1)
                        Log($"[TextingOverlay] batch detected: count={newCount}, speaking only latest");

                    // Live arrivals: keep dropping player echoes (the
                    // player just confirmed that line via ChoiceSub —
                    // re-speaking it gets cut off by the next NPC
                    // bubble and is annoying).
                    SpeakChatEntry(msgs[msgs.Count - 1], charName, suppressPlayer: true);

                    _readCountByChar[charName] = msgs.Count;
                }
                _conversationLastMsgCount = msgs.Count;
            }
            catch (Exception ex)
            {
                Log($"[TextingOverlay] SpeakNewMessages error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SpeakChatEntry(object entry, string partnerName, bool suppressPlayer)
        {
            if (_entryTextField == null) return;
            try
            {
                // The text field is a localized-text wrapper (GClass23 in
                // the decompile) with a user-defined `implicit operator
                // string` that returns the current-language string. The
                // C# `as` cast does NOT trigger user-defined conversions
                // — it only does reference / null casts — so a naive
                // `as string` returns null and every message is silently
                // dropped. Use ToString() (GClass23 overrides it to call
                // vmethod_0, which returns the current-language string)
                // as the universal fallback, and still accept a raw
                // string field in case a future build stores it directly.
                object? raw = _entryTextField.GetValue(entry);
                if (raw == null) return;
                string? text = raw as string ?? raw.ToString();
                if (string.IsNullOrEmpty(text)) return;

                int t = -1;
                if (_entryTypeField != null)
                {
                    object? typeVal = _entryTypeField.GetValue(entry);
                    t = typeVal is Enum e ? Convert.ToInt32(e)
                      : typeVal is int   i ? i
                      : -1;
                }
                bool isPlayer = t == 1;
                if (isPlayer && suppressPlayer)
                {
                    Log($"[TextingOverlay] msg (player, suppressed): \"{text}\"");
                    return;
                }
                string speaker = isPlayer ? PlayerName : partnerName;
                Speech.Speak($"{speaker}: {text}");
                Log($"[TextingOverlay] msg: {speaker}: \"{text}\"");
            }
            catch (Exception ex)
            {
                Log($"[TextingOverlay] SpeakChatEntry error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Chat-history navigation (mirrors ChatAppSubView) ───────────

        private List<object> GetMessages(object phoneState, object character)
        {
            if (_chatHistoryMethod == null) return new List<object>();

            object? dictRaw = _chatHistoryMethod.Invoke(phoneState, null);
            if (dictRaw == null) return new List<object>();

            IDictionary outerDict = (IDictionary)dictRaw;
            if (!outerDict.Contains(character)) return new List<object>();

            object? innerRaw = outerDict[character];
            if (innerRaw == null) return new List<object>();

            IDictionary innerDict = (IDictionary)innerRaw;
            object? historyList   = null;

            // Language-singleton probe: the inner dict is keyed by
            // Language; ChatApp uses a static no-arg method returning
            // Language to fetch the current one. We don't know the
            // singleton type at compile time, so we probe via the
            // inner-dict key type for any static no-arg method
            // returning that type. Fall back to the first key on
            // failure (good enough — the same content is duplicated
            // across all languages by PhoneState.method_13).
            try
            {
                Type keyType = null!;
                foreach (DictionaryEntry de in innerDict) { keyType = de.Key.GetType(); break; }

                if (keyType != null)
                {
                    object? langValue = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (langValue != null) break;
                        try
                        {
                            foreach (Type t in a.GetTypes())
                            {
                                if (langValue != null) break;
                                foreach (MethodInfo m in t.GetMethods(
                                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                                {
                                    if (m.GetParameters().Length == 0 && m.ReturnType == keyType)
                                    {
                                        try { langValue = m.Invoke(null, null); } catch { }
                                        if (langValue != null) break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (langValue != null && innerDict.Contains(langValue))
                        historyList = innerDict[langValue];
                }
            }
            catch { }

            if (historyList == null)
            {
                foreach (DictionaryEntry de in innerDict) { historyList = de.Value; break; }
            }

            if (historyList == null) return new List<object>();

            if (!_entryReflectDone) DoEntryReflect(historyList);

            var result = new List<object>();
            foreach (object entry in (IEnumerable)historyList)
            {
                if (entry == null) continue;
                if (_entryTypeField != null)
                {
                    object? typeVal = _entryTypeField.GetValue(entry);
                    if (typeVal is int t && t == 0) continue;
                    if (typeVal is Enum e && Convert.ToInt32(e) == 0) continue;
                }
                result.Add(entry);
            }
            return result;
        }

        // ── Deferred reflection ────────────────────────────────────────

        private void DoCharReflect()
        {
            if (_charReflectDone) return;
            _charReflectDone = true;
            try
            {
                Type? tChar = Nagoya.FindType("ScriptCharacter");
                if (tChar == null) return;
                _charNameField = tChar.GetField(CharacterNameField,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (_charNameField != null)
                {
                    _charNameToStringMethod = _charNameField.FieldType.GetMethod(
                        LocalizedTextToStringName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                }
                Log($"[TextingOverlay] charReflect: name={_charNameField != null} toString={_charNameToStringMethod != null}");
            }
            catch (Exception ex)
            {
                Log($"[TextingOverlay] DoCharReflect error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void DoEntryReflect(object historyList)
        {
            _entryReflectDone = true;
            try
            {
                Type? entryType = null;
                foreach (object entry in (IEnumerable)historyList)
                {
                    if (entry != null) { entryType = entry.GetType(); break; }
                }
                if (entryType == null) return;

                const BindingFlags instAll =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                _entryTypeField = entryType.GetField(EntryTypeField, instAll);
                _entryTextField = entryType.GetField(EntryTextField, instAll);
                Log($"[TextingOverlay] entryReflect: type={_entryTypeField != null} text={_entryTextField != null}");
            }
            catch (Exception ex)
            {
                Log($"[TextingOverlay] DoEntryReflect error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string GetCharName(object character)
        {
            try
            {
                if (_charNameField != null && _charNameToStringMethod != null)
                {
                    object? nameObj = _charNameField.GetValue(character);
                    if (nameObj != null)
                        return (string)_charNameToStringMethod.Invoke(nameObj, null)! ?? character.ToString()!;
                }
            }
            catch { }
            return character.ToString() ?? "";
        }

        // ── Maybe<T> method resolution ─────────────────────────────────

        private static void ResolveMaybeMethods(Type maybeType,
            out MethodInfo? hasValue, out MethodInfo? getValue)
        {
            hasValue = null;
            getValue = null;
            Type tArg = maybeType.IsGenericType ? maybeType.GetGenericArguments()[0] : typeof(object);
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool)) { hasValue = m; continue; }
                if (m.ReturnType == tArg)         { getValue = m; }
            }
        }
    }
}
