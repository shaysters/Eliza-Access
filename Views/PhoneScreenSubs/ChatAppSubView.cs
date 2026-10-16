using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views.PhoneScreenSubs
{
    /// <summary>
    /// Announces the ChatApp (Messages app) inside the phone — both the
    /// conversation list and the conversation view.
    ///
    /// Activation: <c>IsActive ⇔ PhoneState.activeApp</c> has value AND
    /// the active app's runtime type is <c>ChatApp</c>.
    ///
    /// Two states inside the app:
    /// - <b>List</b> — <c>ChatApp.selectedCharacter</c> is no-value.
    ///   Player is scrolling through conversations. We announce the
    ///   character name as the selected index changes.
    /// - <b>Conversation</b> — <c>ChatApp.selectedCharacter</c> has
    ///   value. Player is reading a conversation. On entry we speak the
    ///   conversation header and every message the player hasn't read
    ///   yet (per-character tracker). New arrivals while in this view
    ///   are spoken as they're appended.
    ///
    /// Player messages (type==1) are filtered out — the user just chose
    /// the line themselves and the echo gets cut off mid-speech by the
    /// next incoming NPC reply. Skipping player echo lets NPC replies
    /// land cleanly. Separator entries (type==0) are also dropped.
    ///
    /// Migrated from <c>V2PhoneObserver</c>'s ChatApp section.
    /// </summary>
    internal sealed class ChatAppSubView : SubView
    {
        // ── Obfuscated names (stable across builds) ────────────────────
        // The phoneState_0 field exists on BOTH PhoneScreen and
        // WorldScreen at different obfuscated names. Same PhoneState
        // instance behind both — so this sub can be wired under either
        // parent depending on context. PhoneScreen path is the normal
        // phone open; WorldScreen path is the inline phone rendered
        // during Transparency Mode.
        private const string PhoneScreenPhoneStateField = "#=qqzJofoxKCCizQVMcCRLhAA==";
        private const string WorldScreenPhoneStateField = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string ActiveAppFieldName        = "#=qS3nVDip0NyI_pzkywVHdyQ==";
        private const string ChatAppTypeName           = "ChatApp";
        private const string ChatSelectedCharField     = "#=qh8A1KwzBeaDlKgPpDZayD4J8XX4mtHZuhfyRHUzPK5o=";
        private const string ChatSelectedIndexField    = "#=qbZKH8GYLVLdLXovHSphmN6YYB6bkM9m1irTiwdE_GoQ=";
        private const string ChatHistoryMethodName     = "#=qZgd2CiTPtk7XHtkZLehK97OCgnLoGwT3R_ZjmbiiG68=";
        private const string CharacterNameField        = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string LocalizedTextToStringName = "#=qTivbHO_rGrDAcTGXa1E4rg==";
        private const string EntryTypeField            = "#=qaG2NfIrHQHsZSpfYKbj8jQ==";
        private const string EntryTextField            = "#=qyUpzQtPynmEGvfYtNiwRrg==";
        // ChatApp.gclass45_1 — the open conversation's scroller.
        // PhoneState.method_10 — the phone owner's ScriptCharacter.
        private const string OwnerCharMethodName       = "#=qRIcHaxHuMyXhtWXCNPkUnt$mTokvAQPYcxZ2vK0abnI=";
        private const string ConvoScrollerField        = "#=qEP1zIByRl_j0OYx4rj3Nah_jGHnOPzI8wvonwKp1spI=";

        // Player character display name. The game hardcodes this in
        // GClass146.scriptCharacter_0 — the localized-text wrapper is
        // built from the literal "Evelyn" with empty alternatives for
        // every other locale.
        private const string PlayerName = "Evelyn";

        // ── Reflection (OnInit + deferred) ─────────────────────────────
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _activeAppField;
        private MethodInfo? _maybeAppHasValue;
        private MethodInfo? _maybeAppGetValue;
        private Type?       _tChatApp;
        private FieldInfo?  _chatSelectedCharField;
        private FieldInfo?  _chatSelectedIndexField;
        private MethodInfo? _maybeCharHasValue;
        private MethodInfo? _maybeCharGetValue;
        private MethodInfo? _chatHistoryMethod;
        private FieldInfo?  _convoScrollerField;
        private MethodInfo? _ownerCharMethod;   // PhoneState.method_10 → phone owner
        private LineReader? _reader;

        // ScriptCharacter → name string. Reflected on first chat-app entry.
        private bool        _charReflectDone;
        private FieldInfo?  _charNameField;
        private MethodInfo? _charNameToStringMethod;

        // TextingHistoryEntry fields. Reflected on first history access.
        private bool        _entryReflectDone;
        private FieldInfo?  _entryTypeField;
        private FieldInfo?  _entryTextField;

        // ── Per-activation state ───────────────────────────────────────
        private int  _focusedListIdx       = -1;
        private bool _inConversation;
        private int  _conversationLastMsgCount = -1;

        // Per-character "we've already read up to this many messages"
        // tracker. Persists across conversation views so re-entering an
        // existing conversation only speaks NEW messages since last
        // visit. Survives mid-session app exits/re-entries.
        private readonly Dictionary<string, int> _readCountByChar = new Dictionary<string, int>();

        // ── Optional additional IsActive gate ──────────────────────────
        // Passed by the parent view. Lets the WorldScreen-mounted
        // instance require TransparencyState.IsActive on top of the
        // built-in "activeApp == ChatApp" check.
        private readonly Func<bool>? _additionalGate;

        public ChatAppSubView(Func<bool>? additionalGate = null)
        {
            _additionalGate = additionalGate;
        }

        public override void OnInit()
        {
            Type tPS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // Try PhoneScreen's name first, then WorldScreen's. Only
            // one will resolve depending on which view wired us.
            _phoneStateField = tPS.GetField(PhoneScreenPhoneStateField, inst)
                              ?? tPS.GetField(WorldScreenPhoneStateField, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                _activeAppField  = tPhoneState.GetField(ActiveAppFieldName, inst);
                if (_activeAppField != null)
                {
                    _maybeAppHasValue = ResolveMaybeMethod(_activeAppField.FieldType, typeof(bool));
                    _maybeAppGetValue = ResolveMaybeGetValue(_activeAppField.FieldType);
                }
                _chatHistoryMethod = tPhoneState.GetMethod(ChatHistoryMethodName, inst);
                _ownerCharMethod   = tPhoneState.GetMethod(OwnerCharMethodName, inst);
            }

            _tChatApp = Nagoya.FindType(ChatAppTypeName);
            if (_tChatApp != null)
            {
                _chatSelectedCharField  = _tChatApp.GetField(ChatSelectedCharField, inst);
                _chatSelectedIndexField = _tChatApp.GetField(ChatSelectedIndexField, inst);
                _convoScrollerField     = _tChatApp.GetField(ConvoScrollerField, inst);
                if (_chatSelectedCharField != null)
                {
                    _maybeCharHasValue = ResolveMaybeMethod(_chatSelectedCharField.FieldType, typeof(bool));
                    _maybeCharGetValue = ResolveMaybeGetValue(_chatSelectedCharField.FieldType);
                }
            }

            _reader = new LineReader(Speech, Log, "ChatAppSub");

            Log($"[ChatAppSub] init: phoneState={(_phoneStateField != null)} " +
                $"convoScroller={(_convoScrollerField != null)} " +
                $"activeApp={(_activeAppField != null)} " +
                $"chatApp={(_tChatApp != null)} " +
                $"chatHistory={(_chatHistoryMethod != null)} " +
                $"selectedChar={(_chatSelectedCharField != null)} " +
                $"selectedIndex={(_chatSelectedIndexField != null)} " +
                $"maybeChar.Has={(_maybeCharHasValue != null)} Get={(_maybeCharGetValue != null)}");
        }

        public override bool IsActive
        {
            get
            {
                if (_additionalGate != null && !_additionalGate()) return false;
                object? app = TryGetActiveApp();
                return app != null && _tChatApp != null && app.GetType() == _tChatApp;
            }
        }

        public override void OnEnter()
        {
            _focusedListIdx           = -1;
            _inConversation           = false;
            _conversationLastMsgCount = -1;
            Speech.Speak(Loc.Get("chat.title"));
            DoCharReflect();
            if (IsChatHistoryEmpty())
            {
                // Match the engine's empty-state text verbatim
                // (ChatApp.cs:179-180). The screen is otherwise inert
                // — no list to navigate, no hotspots — so this is the
                // only thing the player can know about the app right
                // now.
                Speech.Speak(Loc.Get("chat.no_recent"));
            }
            Log("[ChatAppSub] chat app entered.");
        }

        private bool IsChatHistoryEmpty()
        {
            if (_chatHistoryMethod == null) return false;
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return false;
            try
            {
                if (_chatHistoryMethod.Invoke(phoneState, null) is IDictionary dict)
                    return dict.Count == 0;
            }
            catch { }
            return false;
        }

        public override void OnLeave()
        {
            _focusedListIdx           = -1;
            _inConversation           = false;
            _conversationLastMsgCount = -1;
            _reader?.Close();
            PhoneAvatarState.Clear();
            Log("[ChatAppSub] chat app left.");
        }

        public override void OnUpdate()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return;
            object? chatApp = TryGetActiveApp();
            if (chatApp == null) return;

            if (_chatSelectedCharField == null || _chatSelectedIndexField == null) return;

            object? charMaybe;
            try { charMaybe = _chatSelectedCharField.GetValue(chatApp); }
            catch { return; }
            if (charMaybe == null) return;

            bool charOpen;
            try { charOpen = _maybeCharHasValue != null && (bool)_maybeCharHasValue.Invoke(charMaybe, null)!; }
            catch { return; }

            if (!charOpen)
            {
                // State A — character list
                if (_inConversation)
                {
                    _inConversation           = false;
                    _conversationLastMsgCount = -1;
                    _reader?.Close();
                    PhoneAvatarState.Clear();
                    Log("[ChatAppSub] returned to list.");
                }
                int idx;
                try { idx = (int)_chatSelectedIndexField.GetValue(chatApp)!; }
                catch { return; }
                if (idx != _focusedListIdx)
                {
                    _focusedListIdx = idx;
                    SpeakChatListItem(phoneState, idx);
                }
            }
            else
            {
                // State B — conversation view
                if (_maybeCharGetValue == null) return;
                object? character;
                try { character = _maybeCharGetValue.Invoke(charMaybe, null); }
                catch { return; }
                if (character == null) return;

                if (!_inConversation)
                {
                    _inConversation           = true;
                    _conversationLastMsgCount = -1;
                    SpeakConversationEntry(phoneState, character);
                    // Line cursor starts on the newest message; Up walks
                    // back through the thread.
                    var lines = BuildConversationLines(phoneState, character, GetCharName(character));
                    _reader?.Open(lines, lines.Count - 1, () => ReadConvoScroller(chatApp));
                    // J/K/L: the two avatars in this conversation.
                    // ScriptCharacter.ToString() is its internal name.
                    PhoneAvatarState.PartnerKey = character.ToString();
                    try { PhoneAvatarState.OwnerKey = _ownerCharMethod?.Invoke(phoneState, null)?.ToString(); }
                    catch { PhoneAvatarState.OwnerKey = null; }
                }
                if (SpeakNewMessages(phoneState, character))
                {
                    var lines = BuildConversationLines(phoneState, character, GetCharName(character));
                    _reader?.Update(lines, lines.Count - 1);
                }
                _reader?.Poll();
            }
        }

        // ── List view ──────────────────────────────────────────────────

        private void SpeakChatListItem(object phoneState, int idx)
        {
            if (_chatHistoryMethod == null) return;
            try
            {
                if (!(_chatHistoryMethod.Invoke(phoneState, null) is IDictionary dict)) return;
                List<object> sortedKeys = SortedChatKeys(dict);
                if (idx < 0 || idx >= sortedKeys.Count) return;

                string name = GetCharName(sortedKeys[idx]);
                // Each list row opens a conversation when clicked,
                // so it's a button. Wrap in ButtonElement for the
                // standard "name, button" focus format used
                // everywhere else.
                Speech.Speak(new ButtonElement(name).ResolveFocusText());
                Log($"[ChatAppSub] list focus: [{idx}] {name}");
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] SpeakChatListItem error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Conversation view ──────────────────────────────────────────

        private void SpeakConversationEntry(object phoneState, object character)
        {
            try
            {
                string name = GetCharName(character);
                List<object> msgs = GetMessages(phoneState, character);
                int curCount = msgs.Count;

                // Speak everything the player hasn't read yet for this
                // character. First-ever entry → lastSpoken=0 → speak the
                // whole conversation (context for what they're replying
                // to). Re-entry → speak only the delta since last visit.
                //
                // Exception: in Transparency Mode the blind player can't
                // "glance" at a chat to remind themselves what's in it.
                // Each visit needs to re-read the whole thread, and the
                // engine's "all texts read" gate (PhoneState.hashSet_3)
                // is tracked separately so re-reading doesn't double-count.
                int lastSpoken = TransparencyState.IsActive
                    ? 0
                    : (_readCountByChar.TryGetValue(name, out int v) ? v : 0);
                int newCount   = curCount - lastSpoken;

                Speech.Speak(Loc.Fmt("chat.conversation_with", name));
                // History read: speak player lines too. The echo-
                // suppression that SpeakNewMessages applies is only
                // valid for messages the player JUST chose — for the
                // historical thread the player needs to hear both
                // sides to follow the conversation.
                for (int i = lastSpoken; i < curCount; i++)
                    SpeakChatEntry(msgs[i], name, suppressPlayer: false);

                _readCountByChar[name]    = curCount;
                _conversationLastMsgCount = curCount;

                Log($"[ChatAppSub] convo: {name}, {curCount} msgs, {newCount} new since last visit.");
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] SpeakConversationEntry error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Returns true when new messages arrived (and were spoken).
        private bool SpeakNewMessages(object phoneState, object character)
        {
            bool arrived = false;
            try
            {
                List<object> msgs = GetMessages(phoneState, character);
                if (_conversationLastMsgCount >= 0 && msgs.Count > _conversationLastMsgCount)
                {
                    int newCount = msgs.Count - _conversationLastMsgCount;
                    if (newCount > 1)
                        Log($"[ChatAppSub] batch detected: count={newCount}");

                    string charName = GetCharName(character);
                    // Live arrivals: keep dropping player echoes (the
                    // player just confirmed that line via ChoiceSub —
                    // re-speaking it gets cut off by the next NPC
                    // bubble and is annoying).
                    for (int i = _conversationLastMsgCount; i < msgs.Count; i++)
                        SpeakChatEntry(msgs[i], charName, suppressPlayer: true);

                    // Keep the per-character unread tracker in sync so
                    // re-entering this conversation later doesn't re-speak
                    // these messages.
                    _readCountByChar[charName] = msgs.Count;
                    arrived = true;
                }
                _conversationLastMsgCount = msgs.Count;
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] SpeakNewMessages error: {ex.GetType().Name}: {ex.Message}");
            }
            return arrived;
        }

        // Speak a single TextingHistoryEntry with speaker attribution.
        // Live arrivals pass suppressPlayer=true because the player just
        // confirmed the reply via ChoiceSub and an immediate echo would
        // get cut off by the next NPC bubble; history reads pass
        // suppressPlayer=false so the player hears both sides.
        private void SpeakChatEntry(object entry, string partnerName, bool suppressPlayer)
        {
            string? speech = FormatChatEntry(entry, partnerName, out bool isPlayer);
            if (speech == null) return;
            if (isPlayer && suppressPlayer)
            {
                Log($"[ChatAppSub] msg (player, suppressed): \"{speech}\"");
                return;
            }
            Speech.Speak(speech);
            Log($"[ChatAppSub] msg: \"{speech}\"");
        }

        // "Speaker: text" for one TextingHistoryEntry, or null for a
        // separator / empty entry. PhoneState.cs:53 / ChatApp.cs:83-84:
        // type==2 is NPC (the conversation partner); type==1 is the
        // player; type==0 is a separator.
        private string? FormatChatEntry(object entry, string partnerName, out bool isPlayer)
        {
            isPlayer = false;
            if (_entryTextField == null) return null;
            try
            {
                int t = ReadEntryType(entry);
                if (t == 0) return null;
                // The text field is a localized-text wrapper (GClass23 in
                // the decompile) with a user-defined `implicit operator
                // string`. `as string` does NOT trigger user-defined
                // conversions, so ToString() (which GClass23 overrides to
                // return the current-language string) is the fallback.
                object? raw = _entryTextField.GetValue(entry);
                if (raw == null) return null;
                string? text = raw as string ?? raw.ToString();
                if (string.IsNullOrEmpty(text)) return null;

                isPlayer = t == 1;
                // In Transparency Mode the inline phone shows the
                // patient's chat history, not Evelyn's — so player-
                // side messages (type==1) were written BY the patient
                // (Maya / Holiday / Soren) on their device. Attribute
                // accordingly. SessionSubView writes the subject name
                // each tick via TransparencyState.SubjectName.
                string playerLabel = !string.IsNullOrEmpty(TransparencyState.SubjectName)
                    ? TransparencyState.SubjectName
                    : PlayerName;
                string speaker = isPlayer ? playerLabel : partnerName;
                return Loc.Fmt("dialogue.line", speaker, text);
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] FormatChatEntry error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private int ReadEntryType(object entry)
        {
            if (_entryTypeField == null) return -1;
            object? typeVal = _entryTypeField.GetValue(entry);
            return typeVal is Enum e ? Convert.ToInt32(e)
                 : typeVal is int  i ? i
                 : -1;
        }

        // ── PhoneState navigation ──────────────────────────────────────

        private object? TryGetPhoneState()
        {
            if (_phoneStateField == null) return null;
            try { return _phoneStateField.GetValue(Parent); }
            catch { return null; }
        }

        private object? TryGetActiveApp()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return null;
            if (_activeAppField == null || _maybeAppHasValue == null || _maybeAppGetValue == null) return null;

            try
            {
                object? maybe = _activeAppField.GetValue(phoneState);
                if (maybe == null) return null;
                if (!(bool)_maybeAppHasValue.Invoke(maybe, null)!) return null;
                return _maybeAppGetValue.Invoke(maybe, null);
            }
            catch { return null; }
        }

        // ── Chat history navigation ────────────────────────────────────

        /// <summary>Returns non-divider TextingHistoryEntry objects for
        /// the given character, ordered by their position in the history
        /// list (oldest first).</summary>
        private List<object> GetMessages(object phoneState, object character)
        {
            var result = new List<object>();
            foreach (object entry in GetHistory(phoneState, character))
            {
                if (ReadEntryType(entry) == 0) continue;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>The character's full TextingHistoryEntry list in the
        /// current language — separators included, exactly what
        /// ChatApp.smethod_0 lays out.</summary>
        private List<object> GetHistory(object phoneState, object character)
        {
            var result = new List<object>();
            if (_chatHistoryMethod == null) return result;

            if (!(_chatHistoryMethod.Invoke(phoneState, null) is IDictionary outerDict)) return result;
            if (!outerDict.Contains(character)) return result;
            if (!(outerDict[character] is IDictionary innerDict)) return result;

            // Inner dictionary is keyed by game Language; ChatApp reads
            // the current language's list. Fall back to the first list.
            object? historyList = null;
            object? lang = CurrentGameLanguage(innerDict);
            if (lang != null && innerDict.Contains(lang)) historyList = innerDict[lang];
            if (historyList == null)
            {
                foreach (DictionaryEntry de in innerDict) { historyList = de.Value; break; }
            }
            if (historyList == null) return result;

            // Deferred reflection: capture TextingHistoryEntry fields
            // from the first live element.
            if (!_entryReflectDone) DoEntryReflect(historyList);

            foreach (object entry in (IEnumerable)historyList)
                if (entry != null) result.Add(entry);
            return result;
        }

        // The game's current-Language getter (GClass147.smethod_2 — a
        // public static no-arg method returning the Language enum).
        // Found by return type the first time, then cached; the old
        // per-call scan walked every type in every loaded assembly.
        private MethodInfo? _currentLanguageMethod;
        private bool        _currentLanguageSearched;

        private object? CurrentGameLanguage(IDictionary innerDict)
        {
            if (!_currentLanguageSearched)
            {
                Type? keyType = null;
                foreach (DictionaryEntry de in innerDict) { keyType = de.Key.GetType(); break; }
                if (keyType == null) return null;
                _currentLanguageSearched = true;
                foreach (Type t in Nagoya.Types)
                {
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (MethodInfo m in methods)
                    {
                        if (m.GetParameters().Length != 0 || m.ReturnType != keyType) continue;
                        try { if (m.Invoke(null, null) != null) { _currentLanguageMethod = m; break; } }
                        catch { }
                    }
                    if (_currentLanguageMethod != null) break;
                }
                Log($"[ChatAppSub] language getter: {(_currentLanguageMethod != null)}");
            }
            try { return _currentLanguageMethod?.Invoke(null, null); }
            catch { return null; }
        }

        // ── Conversation layout (ChatApp.smethod_0) ────────────────────
        // Content starts 74 below the scroller top. A separator advances
        // 50. A message measures its text (phone font, scale 1, double-
        // newline 0.6, wrap 955; left-aligned for the partner, right for
        // the player), rounds the height to even (-8 for CJK), draws its
        // bubble from 48 above the text origin, and advances height + 42.
        private const float ConvoTopOffset   = 74f;
        private const float SeparatorAdvance = 50f;
        private const float BubbleAboveText  = 48f;
        private const float MessageGap       = 42f;
        private const float ConvoWrapWidth   = 955f;

        private List<LineReader.Line> BuildConversationLines(object phoneState, object character, string partnerName)
        {
            var lines = new List<LineReader.Line>();
            string lang = Loc.CurrentLanguage;
            bool cjk = lang == "Chinese" || lang == "Japanese" || lang == "Korean";
            float y = ConvoTopOffset;
            foreach (object entry in GetHistory(phoneState, character))
            {
                if (ReadEntryType(entry) == 0) { y += SeparatorAdvance; continue; }
                string? speech = FormatChatEntry(entry, partnerName, out bool isPlayer);
                if (speech == null) continue;
                lines.Add(new LineReader.Line(speech, y - BubbleAboveText));

                string text = _entryTextField?.GetValue(entry)?.ToString() ?? "";
                float h = TextLayout.MeasureHeight(text, TextLayout.Font.Phone, isPlayer ? 2 : 0,
                                                   1f, 0.6f, ConvoWrapWidth) ?? 0f;
                h = (float)(Math.Round(h / 2f) * 2f);
                if (cjk) h -= 8f;
                y += h + MessageGap;
            }
            return lines;
        }

        private object? ReadConvoScroller(object chatApp)
        {
            try { return _convoScrollerField?.GetValue(chatApp); }
            catch { return null; }
        }

        private List<object> SortedChatKeys(IDictionary dict)
        {
            // Replicate ChatApp visual order: OrderByDescending by last
            // message DateTime.
            var keys = new List<object>();
            foreach (object k in dict.Keys) keys.Add(k);
            keys.Sort((a, b) =>
            {
                DateTime dtA = GetLastMessageDateTime(dict, a);
                DateTime dtB = GetLastMessageDateTime(dict, b);
                return dtB.CompareTo(dtA);
            });
            return keys;
        }

        private DateTime GetLastMessageDateTime(IDictionary outerDict, object character)
        {
            try
            {
                object? innerRaw = outerDict[character];
                if (innerRaw == null) return DateTime.MinValue;
                IDictionary innerDict = (IDictionary)innerRaw;
                object? historyList   = null;
                foreach (DictionaryEntry de in innerDict) { historyList = de.Value; break; }
                if (historyList == null) return DateTime.MinValue;

                DateTime last = DateTime.MinValue;
                foreach (object entry in (IEnumerable)historyList)
                {
                    if (entry == null) continue;
                    foreach (FieldInfo fi in entry.GetType().GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (fi.FieldType == typeof(DateTime))
                        {
                            DateTime dt = (DateTime)fi.GetValue(entry)!;
                            if (dt > last) last = dt;
                        }
                    }
                }
                return last;
            }
            catch { return DateTime.MinValue; }
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
                Log($"[ChatAppSub] charReflect: name={_charNameField != null} toString={_charNameToStringMethod != null}");
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] DoCharReflect error: {ex.GetType().Name}: {ex.Message}");
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
                Log($"[ChatAppSub] entryReflect: type={_entryTypeField != null} text={_entryTextField != null}");
            }
            catch (Exception ex)
            {
                Log($"[ChatAppSub] DoEntryReflect error: {ex.GetType().Name}: {ex.Message}");
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

        private static MethodInfo? ResolveMaybeMethod(Type maybeType, Type returnType)
        {
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == returnType) return m;
            }
            return null;
        }

        private static MethodInfo? ResolveMaybeGetValue(Type maybeType)
        {
            // Maybe<T>.GetValue returns T (the inner type). Find a no-arg
            // method whose return type is NOT bool and matches the generic
            // type argument.
            Type tArg = maybeType.IsGenericType ? maybeType.GetGenericArguments()[0] : typeof(object);
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == tArg) return m;
            }
            return null;
        }
    }
}
