using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views.PhoneScreenSubs
{
    /// <summary>
    /// Announces the ElizaApp (the AI dashboard) inside the phone — a
    /// three-tab UI:
    ///   tab 0 = profile + 4×4 achievement grid
    ///   tab 1 = Eliza inbox (ScriptEmail list, distinct from MailApp's
    ///           inbox — these are AI-generated messages)
    ///   tab 2 = session history (PhoneState.TherapySession entries
    ///           with per-session star ratings)
    ///
    /// Activation: <c>IsActive ⇔ PhoneState.activeApp</c> has value AND
    /// the active app's runtime type is <c>ElizaApp</c>.
    ///
    /// Tab switching: the game uses FAKE_KEY_4/5/6 controller buttons.
    /// The active tab is an enum field on the ElizaApp instance
    /// (default 1, inbox). We poll the field for changes and announce
    /// the new tab's name plus a short context summary on entry.
    ///
    /// Per-tab handling:
    /// - <b>Profile</b> — read achievement name + description when the
    ///   hovered achievement changes. On entry announce session count.
    /// - <b>Inbox</b> — list/detail like MailApp. On entry announce
    ///   message count. List view announces sender + subject on index
    ///   change; detail view speaks "From X. Date. Subject." + body
    ///   lines on entry.
    /// - <b>Session history</b> — read every recorded session inline
    ///   on entry (the list is short and the player wants the full
    ///   picture without scrolling). List is rendered newest-first
    ///   (Reverse() in the engine); we match that.
    ///
    /// Migrated from <c>V2PhoneObserver</c>'s Eliza section.
    /// </summary>
    internal sealed class ElizaAppSubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        private const string PhoneScreenPhoneStateField = "#=qqzJofoxKCCizQVMcCRLhAA==";
        private const string WorldScreenPhoneStateField = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string ActiveAppFieldName         = "#=qS3nVDip0NyI_pzkywVHdyQ==";
        private const string ElizaAppTypeName           = "ElizaApp";
        private const string ElizaTabField              = "#=q3MnqVLLCtnEcSUr7fqQtQQ==";
        private const string ElizaSelectedEmailField    = "#=qbYlytfAZ0Ogw23um81BgMw==";
        private const string ElizaSelectedIndexField    = "#=q4yGblOWKvfzO2$xbKtr7yR7XmI3405kT02R2gcah2eU=";
        private const string ElizaHoveredAchField       = "#=q30BMtiujWVmTMFZl0G6yhA==";
        private const string AchievementNameField       = "#=qHsTjiFKVEKoKYH35a5ldKQ==";
        private const string AchievementDescField       = "#=qjQW7__BYNjGboD17ww2YCQ==";
        // PhoneState members. The Eliza inbox is a different IEnumerable
        // from MailApp's; sessionsHolder is the List<TherapySession>
        // directly (not wrapped in a method).
        private const string ElizaInboxMethod           = "#=qO2JDPsXCIY$Ba6KxZIfYEkaBCVGn1qU5XJQREYgM8oc=";
        private const string SessionsHolderField        = "#=qbKHlo689MZLcwopVAqFTlfmVi1PcGmOEIM9Sf8iXAXw=";
        // PhoneState.TherapySession nested-class fields.
        private const string TherapyEntryNestedType     = "TherapySession";
        private const string TherapyEntryCharField      = "#=qLFu0a0kAeGPfR7pdByCKRg==";
        private const string TherapyEntryDateField      = "#=quD4fi0rzKP0G3eXdUIydwA==";
        private const string TherapyEntryProgressField  = "#=qRjgOIUotpixwH_ebqFNRQQ==";
        // ScriptCharacter name plumbing (shared with ChatAppSubView /
        // TextingOverlaySubView constants).
        private const string CharacterNameField         = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string CharacterLastNameField     = "#=q0XfuGfoz9CcWkzQ03CuAxw==";
        private const string LocalizedTextToStringName  = "#=qTivbHO_rGrDAcTGXa1E4rg==";
        // ScriptEmail fields for the inbox.
        private const string EmailSenderField           = "#=qqyehqnoGNtxinWDp6xth0w==";
        private const string EmailSubjectField          = "#=qdlh3n2uNGIux6bl0e6K7uA==";
        private const string EmailBodyField             = "#=qj9q0554wTUaB3Ay8iPJ80g==";
        private const string EmailPlaceholderField      = "#=q6jB6zTsQ0W9G3bubWwlN3ph0MHOJjGshAIKC8O5xXg4==";
        private const string EmailDateField             = "#=qwg$KVuFrMweHKbfK0W5mdA==";
        // Achievement registry (used by our own profile-tab grid
        // navigation — the engine only supports mouse hover here).
        // The static class holds 15 individual achievement instances
        // plus the array field that orders them for display. Element
        // type is GClass27 (the class with name/desc LocalizedText
        // fields and texture_0/texture_1).
        //
        // A previous mapping pointed at a sibling static class
        // (#=qdTN9La3...) whose only [15] array is an enum-typed
        // input-sequence buffer used by VirtualInput, not the
        // achievements — symptom was GetType() on array elements
        // returning an enum with only value__:Int32.
        private const string AchievementRegistryType    = "#=q7ufzlbSTfFsdflJfuP6ZYA==";
        private const string AchievementArrayField      = "#=q$e5LeN7pfGdSK29KEpXw9A==";
        private const string UnlockedAchievementsField  = "#=qD5GEXV97kYKjlT2S96NGdA==";
        // VirtualInput.SetCursor(Vector2) — private static. Same
        // method V2ChapterObserver uses for keyboard-driven episode
        // selection (InjectEpisodeClick).
        private const string VirtualInputType           = "VirtualInput";
        private const string SetCursorMethod            = "#=qT_SlnKGmQty0k3OLe9Ydng==";
        // Maybe<T> methods — direct names so Maybe<int> doesn't trip up
        // signature matching.
        private const string MaybeHasValueName          = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValueName          = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        // ── Reflection ─────────────────────────────────────────────────
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _activeAppField;
        private MethodInfo? _maybeAppHasValue;
        private MethodInfo? _maybeAppGetValue;
        private Type?       _tElizaApp;
        private FieldInfo?  _elizaTabField;
        private FieldInfo?  _elizaSelectedEmailField;
        private FieldInfo?  _elizaSelectedIndexField;
        private MethodInfo? _maybeElizaEmailHasValue;
        private MethodInfo? _maybeElizaEmailGetValue;
        private FieldInfo?  _elizaHoveredAchField;
        private MethodInfo? _elizaHoveredAchHasValue;
        private MethodInfo? _elizaHoveredAchGetValue;
        private FieldInfo?  _achievementNameField;
        private FieldInfo?  _achievementDescField;
        private MethodInfo? _elizaInboxMethod;
        private FieldInfo?  _sessionsHolderField;
        private FieldInfo?  _therapyEntryCharField;
        private FieldInfo?  _therapyEntryDateField;
        private FieldInfo?  _therapyEntryProgressField;
        private MethodInfo? _therapyProgressHasValue;
        private MethodInfo? _therapyProgressGetValue;
        private FieldInfo?  _charNameField;
        private FieldInfo?  _charLastNameField;
        private MethodInfo? _charNameToString;
        private FieldInfo?  _emailSenderField;
        private FieldInfo?  _emailSubjectField;
        private FieldInfo?  _emailBodyField;
        private FieldInfo?  _emailPlaceholderField;
        private FieldInfo?  _emailDateField;
        private FieldInfo?  _achievementArrayField;     // GClass26.gclass27_15 (static)
        private FieldInfo?  _unlockedAchievementsField; // PhoneState.hashSet_0
        private MethodInfo? _setCursorMethod;           // VirtualInput.SetCursor(Vector2)
        private Type?       _tVector2;                  // Vector2 struct
        // Profile-header data sources (resolved by signature off
        // PhoneState — see ElizaApp.cs:103-111 for the original use).
        // _playerCharMethod is PhoneState.method_10 — returns the
        // active player ScriptCharacter (the inline transparency-mode
        // proxy if one is set, otherwise GClass146.scriptCharacter_0).
        // _proxyLevelMethod is method_16(out int level, out _, out _).
        // _proxyRatingMethod is method_14() — Maybe<float> averaged
        // across recorded sessions; "N/A" if no value.
        private MethodInfo? _playerCharMethod;
        private MethodInfo? _proxyLevelMethod;
        private MethodInfo? _proxyRatingMethod;
        private MethodInfo? _maybeFloatHasValue;
        private MethodInfo? _maybeFloatGetValue;

        // ── Per-activation state ───────────────────────────────────────
        private int     _lastTab            = -1;
        private int     _lastInboxIndex     = -1;
        private bool    _inboxDetailActive;

        // Profile-tab grid navigation. The engine renders the
        // achievements as a 4×4 grid (16 slots, last one empty since
        // there are only 15 achievements) and supports only mouse
        // hover — no D-pad bindings. We implement our own focus by
        // watching keyboard arrows directly and announcing the
        // focused achievement.
        private const int ProfileGridCols  = 4;
        private const int ProfileGridCount = 15;
        private int _profileFocusIdx = 0;
        private readonly KeyEdge _kbLeft  = new KeyEdge(Keyboard.VK_LEFT);
        private readonly KeyEdge _kbRight = new KeyEdge(Keyboard.VK_RIGHT);
        private readonly KeyEdge _kbUp    = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown  = new KeyEdge(Keyboard.VK_DOWN);

        // Sessions-tab list navigation. The engine renders a scrollable
        // list with no selection model — no D-pad bindings, no click-to-
        // open, no detail view. We own focus entirely: arrows step by
        // one row, Home/End jump to ends. Index is over the engine's
        // display order (newest-first, mirroring its Reverse()).
        private int _sessionFocusIdx = 0;
        private readonly KeyEdge _kbHome = new KeyEdge(Keyboard.VK_HOME);
        private readonly KeyEdge _kbEnd  = new KeyEdge(Keyboard.VK_END);

        // ── Optional additional IsActive gate ──────────────────────────
        private readonly Func<bool>? _additionalGate;

        // Phone top-left in world coords — used as the origin for the
        // achievement-grid slot positions when we move the cursor to
        // highlight a focused slot. PhoneScreen draws the phone at
        // (669, 268) — its vector2_1. WorldScreen's inline phone
        // (Transparency Mode) draws at (523, 268 + animOffset); the
        // animation is small enough that 268 is close enough for
        // hit-testing.
        private readonly float _phoneOriginX;
        private readonly float _phoneOriginY;

        public ElizaAppSubView(
            Func<bool>? additionalGate = null,
            float phoneOriginX = 669f,
            float phoneOriginY = 268f)
        {
            _additionalGate = additionalGate;
            _phoneOriginX   = phoneOriginX;
            _phoneOriginY   = phoneOriginY;
        }

        public override void OnInit()
        {
            Type tParent = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _phoneStateField = tParent.GetField(PhoneScreenPhoneStateField, inst)
                               ?? tParent.GetField(WorldScreenPhoneStateField, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                _activeAppField  = tPhoneState.GetField(ActiveAppFieldName, inst);
                if (_activeAppField != null)
                {
                    _maybeAppHasValue = ResolveMaybeMethod(_activeAppField.FieldType, MaybeHasValueName);
                    _maybeAppGetValue = ResolveMaybeMethod(_activeAppField.FieldType, MaybeGetValueName);
                }
                _elizaInboxMethod    = tPhoneState.GetMethod(ElizaInboxMethod, inst);
                _sessionsHolderField = tPhoneState.GetField(SessionsHolderField, inst);

                // Profile-header methods, signature-resolved.
                Type? tScriptCharacter = Nagoya.FindType("ScriptCharacter");
                foreach (MethodInfo m in tPhoneState.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 0 && m.ReturnType == typeof(void)) continue;
                    if (p.Length == 0 && tScriptCharacter != null && m.ReturnType == tScriptCharacter)
                    {
                        if (_playerCharMethod == null) _playerCharMethod = m;
                    }
                    else if (p.Length == 0 && m.ReturnType.IsGenericType
                             && m.ReturnType.GetGenericArguments().Length == 1
                             && m.ReturnType.GetGenericArguments()[0] == typeof(float))
                    {
                        if (_proxyRatingMethod == null)
                        {
                            _proxyRatingMethod = m;
                            _maybeFloatHasValue = ResolveMaybeMethod(m.ReturnType, MaybeHasValueName);
                            _maybeFloatGetValue = ResolveMaybeMethod(m.ReturnType, MaybeGetValueName);
                        }
                    }
                    else if (p.Length == 3 && m.ReturnType == typeof(void)
                             && p[0].IsOut && p[0].ParameterType == typeof(int).MakeByRefType()
                             && p[1].IsOut && p[1].ParameterType == typeof(int).MakeByRefType()
                             && p[2].IsOut && p[2].ParameterType == typeof(float).MakeByRefType())
                    {
                        if (_proxyLevelMethod == null) _proxyLevelMethod = m;
                    }
                }

                Type? tTherapy = tPhoneState.GetNestedType(TherapyEntryNestedType,
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (tTherapy != null)
                {
                    _therapyEntryCharField     = tTherapy.GetField(TherapyEntryCharField, inst);
                    _therapyEntryDateField     = tTherapy.GetField(TherapyEntryDateField, inst);
                    _therapyEntryProgressField = tTherapy.GetField(TherapyEntryProgressField, inst);
                    if (_therapyEntryProgressField != null)
                    {
                        _therapyProgressHasValue = ResolveMaybeMethod(_therapyEntryProgressField.FieldType, MaybeHasValueName);
                        _therapyProgressGetValue = ResolveMaybeMethod(_therapyEntryProgressField.FieldType, MaybeGetValueName);
                    }
                }
            }

            _tElizaApp = Nagoya.FindType(ElizaAppTypeName);
            if (_tElizaApp != null)
            {
                _elizaTabField           = _tElizaApp.GetField(ElizaTabField, inst);
                _elizaSelectedEmailField = _tElizaApp.GetField(ElizaSelectedEmailField, inst);
                _elizaSelectedIndexField = _tElizaApp.GetField(ElizaSelectedIndexField, inst);
                if (_elizaSelectedEmailField != null)
                {
                    _maybeElizaEmailHasValue = ResolveMaybeMethod(_elizaSelectedEmailField.FieldType, MaybeHasValueName);
                    _maybeElizaEmailGetValue = ResolveMaybeMethod(_elizaSelectedEmailField.FieldType, MaybeGetValueName);
                }
                _elizaHoveredAchField = _tElizaApp.GetField(ElizaHoveredAchField, inst);
                if (_elizaHoveredAchField != null)
                {
                    _elizaHoveredAchHasValue = ResolveMaybeMethod(_elizaHoveredAchField.FieldType, MaybeHasValueName);
                    _elizaHoveredAchGetValue = ResolveMaybeMethod(_elizaHoveredAchField.FieldType, MaybeGetValueName);
                }
            }

            // Achievement registry — the array is a public static
            // field on a static class. Element type is the achievement
            // record class with name/desc LocalizedText fields.
            Type? tRegistry = Nagoya.FindType(AchievementRegistryType);
            if (tRegistry != null)
            {
                _achievementArrayField = tRegistry.GetField(AchievementArrayField,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (_achievementArrayField != null)
                {
                    Type tAchievement = _achievementArrayField.FieldType.GetElementType()!;
                    _achievementNameField = tAchievement.GetField(AchievementNameField, inst);
                    _achievementDescField = tAchievement.GetField(AchievementDescField, inst);
                }
            }

            // Unlocked-achievement set on PhoneState.
            if (_phoneStateField != null)
            {
                _unlockedAchievementsField = _phoneStateField.FieldType.GetField(
                    UnlockedAchievementsField, inst);
            }

            // VirtualInput.SetCursor — moves the virtual cursor so the
            // engine renders the focused achievement's highlight and
            // description panel. Lets sighted observers track our
            // arrow-key navigation visually.
            _tVector2 = Nagoya.FindType("Vector2");
            Type? tVirtualInput = Nagoya.FindType(VirtualInputType);
            if (tVirtualInput != null && _tVector2 != null)
            {
                _setCursorMethod = tVirtualInput.GetMethod(SetCursorMethod,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { _tVector2 }, null);
            }

            Type? tChar = Nagoya.FindType("ScriptCharacter");
            if (tChar != null)
            {
                _charNameField     = tChar.GetField(CharacterNameField,     inst);
                _charLastNameField = tChar.GetField(CharacterLastNameField, inst);
                if (_charNameField != null)
                {
                    _charNameToString = _charNameField.FieldType.GetMethod(
                        LocalizedTextToStringName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                }
            }

            Type? tScriptEmail = Nagoya.FindType("ScriptEmail");
            if (tScriptEmail != null)
            {
                _emailSenderField      = tScriptEmail.GetField(EmailSenderField, inst);
                _emailSubjectField     = tScriptEmail.GetField(EmailSubjectField, inst);
                _emailBodyField        = tScriptEmail.GetField(EmailBodyField, inst);
                _emailPlaceholderField = tScriptEmail.GetField(EmailPlaceholderField, inst);
                _emailDateField        = tScriptEmail.GetField(EmailDateField, inst);
            }

            Log($"[ElizaAppSub] init: phoneState={(_phoneStateField != null)} " +
                $"elizaApp={(_tElizaApp != null)} tab={(_elizaTabField != null)} " +
                $"selEmail={(_elizaSelectedEmailField != null)} selIdx={(_elizaSelectedIndexField != null)} " +
                $"hoveredAch={(_elizaHoveredAchField != null)} ach.name={(_achievementNameField != null)} " +
                $"achArray={(_achievementArrayField != null)} unlocked={(_unlockedAchievementsField != null)} " +
                $"inboxMethod={(_elizaInboxMethod != null)} sessions={(_sessionsHolderField != null)} " +
                $"therapy.char={(_therapyEntryCharField != null)} date={(_therapyEntryDateField != null)} " +
                $"prog.has={(_therapyProgressHasValue != null)} get={(_therapyProgressGetValue != null)} " +
                $"scriptEmail={(_emailSenderField != null)} " +
                $"playerChar={(_playerCharMethod != null)} proxyLevel={(_proxyLevelMethod != null)} " +
                $"proxyRating={(_proxyRatingMethod != null)}");
        }

        public override bool IsActive
        {
            get
            {
                if (_additionalGate != null && !_additionalGate()) return false;
                object? app = TryGetActiveApp();
                return app != null && _tElizaApp != null && app.GetType() == _tElizaApp;
            }
        }

        public override void OnEnter()
        {
            _lastTab           = -1;
            _lastInboxIndex    = -1;
            _inboxDetailActive = false;
            Speech.Speak("Eliza. Three tabs: 1 profile, 2 inbox, 3 session history.");
            Log("[ElizaAppSub] entered.");
        }

        public override void OnLeave()
        {
            Log("[ElizaAppSub] left.");
        }

        public override void OnUpdate()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return;
            object? elizaApp = TryGetActiveApp();
            if (elizaApp == null) return;

            int tab = ReadTab(elizaApp);
            if (tab != _lastTab)
            {
                _lastTab           = tab;
                _lastInboxIndex    = -1;
                _inboxDetailActive = false;
                AnnounceTab(tab, phoneState);
            }

            if      (tab == 0) HandleProfileTab(elizaApp);
            else if (tab == 1) HandleInboxTab(elizaApp, phoneState);
            else if (tab == 2) HandleSessionHistoryTab(elizaApp, phoneState);
        }

        // ── Tab dispatch ───────────────────────────────────────────────

        private int ReadTab(object elizaApp)
        {
            if (_elizaTabField == null) return -1;
            try
            {
                object? v = _elizaTabField.GetValue(elizaApp);
                if (v is Enum e) return Convert.ToInt32(e);
                if (v is int i)  return i;
            }
            catch { }
            return -1;
        }

        private void AnnounceTab(int tab, object phoneState)
        {
            switch (tab)
            {
                case 0:
                    Speech.Speak("Profile tab.");
                    AnnounceProfileHeader(phoneState);
                    AnnounceSessionCount(phoneState, prefix: "");
                    // Reset grid focus to the top-left achievement and
                    // announce it. The engine doesn't track focus for
                    // this tab, so we own it — arrow keys move focus,
                    // and HandleProfileTab speaks each new focus.
                    _profileFocusIdx = 0;
                    _kbLeft.Reset(); _kbRight.Reset(); _kbUp.Reset(); _kbDown.Reset();
                    SpeakFocusedAchievement(phoneState);
                    MoveCursorToFocusedSlot();
                    Log("[ElizaAppSub] tab: profile");
                    break;
                case 1:
                    Speech.Speak("Inbox tab.");
                    AnnounceInboxCount(phoneState);
                    Log("[ElizaAppSub] tab: inbox");
                    break;
                case 2:
                    Speech.Speak("Session history tab.");
                    AnnounceSessionCount(phoneState, prefix: "");
                    _sessionFocusIdx = 0;
                    _kbUp.Reset(); _kbDown.Reset();
                    _kbHome.Reset(); _kbEnd.Reset();
                    SpeakSessionRow(phoneState, _sessionFocusIdx);
                    Log("[ElizaAppSub] tab: sessions");
                    break;
                default:
                    Speech.Speak($"Tab {tab}.");
                    Log($"[ElizaAppSub] tab: unknown ({tab})");
                    break;
            }
        }

        // ── Profile tab ────────────────────────────────────────────────

        /// <summary>
        /// Speaks the visible header info on the profile tab — player
        /// name, Proxy Level, Proxy Rating. Mirrors ElizaApp.cs:103-111.
        /// Silently skips any field that couldn't be reflected (the
        /// profile tab still works, just with that piece missing).
        /// </summary>
        private void AnnounceProfileHeader(object phoneState)
        {
            string? name   = ReadPlayerName(phoneState);
            string? level  = ReadProxyLevel(phoneState);
            string? rating = ReadProxyRating(phoneState);

            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(name))   parts.Add(name!);
            if (!string.IsNullOrWhiteSpace(level))  parts.Add($"Proxy Level {level}");
            if (!string.IsNullOrWhiteSpace(rating)) parts.Add($"Proxy Rating {rating}");
            if (parts.Count == 0) return;

            string spoken = string.Join(". ", parts) + ".";
            Speech.Speak(spoken);
            Log($"[ElizaAppSub] profile header: \"{spoken}\"");
        }

        private string? ReadPlayerName(object phoneState)
        {
            if (_playerCharMethod == null) return null;
            try
            {
                object? c = _playerCharMethod.Invoke(phoneState, null);
                if (c == null) return null;
                // ScriptCharacter.method_0 returns "{last}, {first}" —
                // the same string the engine renders at vector+(671, -115).
                return ResolveCharFullName(c);
            }
            catch { return null; }
        }

        private string? ReadProxyLevel(object phoneState)
        {
            if (_proxyLevelMethod == null) return null;
            try
            {
                object[] args = new object[] { 0, 0, 0f };
                _proxyLevelMethod.Invoke(phoneState, args);
                return args[0]?.ToString();
            }
            catch { return null; }
        }

        private string? ReadProxyRating(object phoneState)
        {
            if (_proxyRatingMethod == null || _maybeFloatHasValue == null
                || _maybeFloatGetValue == null) return "N/A";
            try
            {
                object? maybe = _proxyRatingMethod.Invoke(phoneState, null);
                if (maybe == null) return "N/A";
                if (!(bool)_maybeFloatHasValue.Invoke(maybe, null)!) return "N/A";
                float v = (float)_maybeFloatGetValue.Invoke(maybe, null)!;
                // Engine formats the rating as a plain number (no
                // decimals trimmed) via smethod_36 on the raw float.
                // Round to one decimal for speech clarity.
                return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return "N/A"; }
        }


        // The engine renders the 4×4 achievement grid using only mouse-
        // hover detection (ElizaApp.cs:120 — `bounds2_.Contains(cursor)`)
        // and registers a click bounds via VirtualInput.smethod_6.
        // There's no smethod_8 binding for D-pad navigation, so the tab
        // is unreachable from a controller. We implement our own focus:
        // arrow keys move through the 4×4 grid (left/right by 1, up/
        // down by 4, clamped to 0..14 — slot 15 is intentionally empty
        // since there are only 15 achievements).
        private void HandleProfileTab(object elizaApp)
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return;

            int prev = _profileFocusIdx;
            if (_kbRight.Pressed()) _profileFocusIdx = Math.Min(ProfileGridCount - 1, _profileFocusIdx + 1);
            if (_kbLeft.Pressed())  _profileFocusIdx = Math.Max(0, _profileFocusIdx - 1);
            if (_kbDown.Pressed())  _profileFocusIdx = Math.Min(ProfileGridCount - 1, _profileFocusIdx + ProfileGridCols);
            if (_kbUp.Pressed())    _profileFocusIdx = Math.Max(0, _profileFocusIdx - ProfileGridCols);

            if (_profileFocusIdx != prev)
                SpeakFocusedAchievement(phoneState);

            // Move the virtual cursor over the focused slot every tick
            // so the engine renders the highlight + description panel
            // for any sighted observer. (Pure visual feedback — TTS
            // already carried the focused content.)
            MoveCursorToFocusedSlot();
        }

        private void MoveCursorToFocusedSlot()
        {
            if (_setCursorMethod == null || _tVector2 == null) return;
            try
            {
                int col = _profileFocusIdx % ProfileGridCols;
                int row = _profileFocusIdx / ProfileGridCols;
                // Slot origin from ElizaApp.cs:116. Y axis: the engine
                // uses local-Y-up here (row 0 has higher Y than row 3);
                // the value is added directly to the world phone-top
                // and the same coord system that smethod_23 returns,
                // so we don't have to flip anything ourselves.
                float minX = _phoneOriginX + 66f + col * 276f;
                float minY = _phoneOriginY + 1143f - row * 286f;
                // Offset to the badge-holder center. Approximate — the
                // texture is roughly cell-sized (276x286 cells) and
                // typically square-ish. (122, 122) lands well inside
                // any plausible badge bounds.
                object centerVec = Activator.CreateInstance(
                    _tVector2, new object[] { minX + 122f, minY + 122f })!;
                _setCursorMethod.Invoke(null, new[] { centerVec });
            }
            catch { }
        }

        private void SpeakFocusedAchievement(object phoneState)
        {
            if (_achievementArrayField == null) return;
            try
            {
                if (!(_achievementArrayField.GetValue(null) is Array arr)) return;
                if (_profileFocusIdx < 0 || _profileFocusIdx >= arr.Length) return;
                object? ach = arr.GetValue(_profileFocusIdx);
                if (ach == null) return;

                if (_achievementNameField == null || _achievementDescField == null) return;

                string name = _achievementNameField.GetValue(ach)?.ToString() ?? "";
                string desc = _achievementDescField.GetValue(ach)?.ToString() ?? "";

                bool unlocked = IsAchievementUnlocked(phoneState, ach);
                string status = unlocked ? "unlocked" : "locked";

                string label = !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"Achievement {_profileFocusIdx + 1}";
                string? tooltip = !string.IsNullOrWhiteSpace(desc) ? desc : null;
                string utterance = new ButtonElement(label, status: status, tooltip: tooltip)
                    .ResolveFocusText();

                Speech.Speak(utterance);
                Log($"[ElizaAppSub] profile focus [{_profileFocusIdx}]: \"{utterance}\"");
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] SpeakFocusedAchievement error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool IsAchievementUnlocked(object phoneState, object achievement)
        {
            if (_unlockedAchievementsField == null) return false;
            try
            {
                object? set = _unlockedAchievementsField.GetValue(phoneState);
                if (set is IEnumerable e)
                {
                    foreach (object? item in e)
                    {
                        if (ReferenceEquals(item, achievement)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ── Inbox tab ──────────────────────────────────────────────────

        private void HandleInboxTab(object elizaApp, object phoneState)
        {
            if (_elizaSelectedEmailField == null || _elizaSelectedIndexField == null
                || _maybeElizaEmailHasValue == null) return;
            object? maybe;
            try { maybe = _elizaSelectedEmailField.GetValue(elizaApp); }
            catch { return; }
            if (maybe == null) return;

            bool open;
            try { open = (bool)_maybeElizaEmailHasValue.Invoke(maybe, null)!; }
            catch { return; }

            if (!open)
            {
                if (_inboxDetailActive)
                {
                    _inboxDetailActive = false;
                    Log("[ElizaAppSub] inbox: returned to list.");
                }
                int idx;
                try { idx = (int)_elizaSelectedIndexField.GetValue(elizaApp)!; }
                catch { return; }
                if (idx != _lastInboxIndex)
                {
                    _lastInboxIndex = idx;
                    SpeakInboxListItem(phoneState, idx);
                }
            }
            else
            {
                if (!_inboxDetailActive)
                {
                    _inboxDetailActive = true;
                    if (_maybeElizaEmailGetValue == null) return;
                    object? email;
                    try { email = _maybeElizaEmailGetValue.Invoke(maybe, null); }
                    catch { return; }
                    if (email != null) SpeakMailDetail(email);
                }
            }
        }

        private void AnnounceInboxCount(object phoneState)
        {
            if (_elizaInboxMethod == null) return;
            try
            {
                object? raw = _elizaInboxMethod.Invoke(phoneState, null);
                if (raw == null) return;
                int count = 0;
                foreach (object? _ in (IEnumerable)raw) count++;
                string ctx = count == 0 ? "No messages."
                           : count == 1 ? "1 message."
                           : $"{count} messages.";
                Speech.Speak(ctx);
                Log($"[ElizaAppSub] inbox count: {count}");
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] AnnounceInboxCount error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SpeakInboxListItem(object phoneState, int idx)
        {
            if (_elizaInboxMethod == null || _emailSenderField == null || _emailSubjectField == null) return;
            try
            {
                object? raw = _elizaInboxMethod.Invoke(phoneState, null);
                if (raw == null) return;
                var emails = ((IEnumerable)raw).Cast<object>().Reverse().ToList();
                if (idx < 0 || idx >= emails.Count) return;
                object email = emails[idx];

                if (_emailPlaceholderField != null)
                {
                    object? isPlaceholder = _emailPlaceholderField.GetValue(email);
                    if (isPlaceholder is bool b && b) return;
                }
                string sender  = _emailSenderField.GetValue(email)?.ToString() ?? "";
                string subject = _emailSubjectField.GetValue(email)?.ToString() ?? "";
                string date    = ReadEmailDate(email);

                // Mirror MailAppSubView's inbox-row format: comma-
                // joined fields wrapped in a ButtonElement so the
                // type marker appends.
                var labelParts = new List<string>(3);
                if (!string.IsNullOrEmpty(sender))  labelParts.Add(sender);
                if (!string.IsNullOrEmpty(subject)) labelParts.Add(subject);
                if (!string.IsNullOrEmpty(date))    labelParts.Add(date);
                string label = AnnouncementComposer.JoinSpeechParts(labelParts);
                Speech.Speak(new ButtonElement(label).ResolveFocusText());
                Log($"[ElizaAppSub] inbox focus: [{idx}] {sender} / {subject} / {date}");
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] SpeakInboxListItem error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Mirror of MailAppSubView.SpeakMailDetail. Kept separate so the
        // ElizaApp inbox can evolve independently if its formatting
        // needs to differ (e.g. distinguishing AI-generated from human-
        // authored emails). For now identical wording.
        private void SpeakMailDetail(object email)
        {
            if (_emailSenderField == null || _emailSubjectField == null || _emailBodyField == null) return;
            try
            {
                string sender  = _emailSenderField.GetValue(email)?.ToString() ?? "";
                string subject = _emailSubjectField.GetValue(email)?.ToString() ?? "";
                string body    = _emailBodyField.GetValue(email)?.ToString() ?? "";
                string date    = ReadEmailDate(email);
                string dateChunk = date.Length > 0 ? $" {date}." : "";

                Speech.Speak($"{sender}.{dateChunk} {subject}.");

                int spoken = 0;
                foreach (string raw in body.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    // Embedded image token — see MailAppSubView for
                    // the full rationale. "{hedonic}" → "Image:
                    // hedonic" so TTS doesn't read literal braces.
                    if (line.StartsWith("{") && line.EndsWith("}") && line.Length > 2)
                    {
                        string key = line.Substring(1, line.Length - 2).Trim();
                        string? desc = EmailImageDescriptions.TryGet(key);
                        if (desc != null)
                        {
                            Speech.Speak($"Image: {desc}");
                        }
                        else if (key.Length > 0)
                        {
                            Speech.Speak($"Image: {key.Replace("_", " ")}");
                        }
                        spoken++;
                        continue;
                    }
                    if (line.StartsWith(":") && line.EndsWith(":") && line.Length > 2)
                        line = line.Substring(1, line.Length - 2);
                    Speech.Speak(line);
                    spoken++;
                }

                Log($"[ElizaAppSub] detail: {sender} / {subject} / {date} ({spoken} body lines)");
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] SpeakMailDetail error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string ReadEmailDate(object email)
        {
            if (_emailDateField == null) return "";
            try
            {
                object? raw = _emailDateField.GetValue(email);
                if (raw is DateTime dt)
                    return dt.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        // ── Session history tab ────────────────────────────────────────

        // Engine doesn't bind D-pad or update _elizaSelectedIndexField
        // on this tab — selection model is ours alone. Arrows step by
        // one row, Home/End jump to the newest/oldest. Index is into
        // the display-ordered (newest-first) list, matching the order
        // SpeakSessionRow uses.
        private void HandleSessionHistoryTab(object elizaApp, object phoneState)
        {
            int count = SessionCount(phoneState);
            if (count <= 0) return;

            int prev = _sessionFocusIdx;
            bool up    = _kbUp.Pressed();
            bool down  = _kbDown.Pressed();
            bool home  = _kbHome.Pressed();
            bool end   = _kbEnd.Pressed();

            if (up)        _sessionFocusIdx--;
            else if (down) _sessionFocusIdx++;
            else if (home) _sessionFocusIdx = 0;
            else if (end)  _sessionFocusIdx = count - 1;

            if (_sessionFocusIdx < 0)        _sessionFocusIdx = 0;
            if (_sessionFocusIdx >= count)   _sessionFocusIdx = count - 1;

            if (_sessionFocusIdx != prev)
            {
                SpeakSessionRow(phoneState, _sessionFocusIdx);
            }
        }

        private int SessionCount(object phoneState)
        {
            if (_sessionsHolderField == null) return 0;
            try
            {
                object? raw = _sessionsHolderField.GetValue(phoneState);
                if (raw == null) return 0;
                int n = 0;
                foreach (object? _ in (IEnumerable)raw) n++;
                return n;
            }
            catch { return 0; }
        }

        private void AnnounceSessionCount(object phoneState, string prefix)
        {
            if (_sessionsHolderField == null) return;
            try
            {
                object? raw = _sessionsHolderField.GetValue(phoneState);
                if (raw == null) return;
                int count = 0;
                foreach (object? _ in (IEnumerable)raw) count++;
                string ctx = count == 0 ? "No sessions recorded yet."
                           : count == 1 ? "1 session recorded."
                           : $"{count} sessions recorded.";
                Speech.Speak(prefix + ctx);
                Log($"[ElizaAppSub] session count: {count}");
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] AnnounceSessionCount error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SpeakSessionRow(object phoneState, int idx)
        {
            if (_sessionsHolderField == null) return;
            try
            {
                object? raw = _sessionsHolderField.GetValue(phoneState);
                if (raw == null) return;
                var list = new List<object>();
                foreach (object? e in (IEnumerable)raw) if (e != null) list.Add(e);
                // Engine renders newest-first via Reverse(); mirror so
                // the index we announce matches what the player sees.
                list.Reverse();
                if (idx < 0 || idx >= list.Count) return;
                SpeakSessionEntry(list[idx], idx, list.Count);
            }
            catch (Exception ex)
            {
                Log($"[ElizaAppSub] SpeakSessionRow error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SpeakSessionEntry(object entry, int idx, int total)
        {
            string charName = "";
            string dateStr  = "";
            string rating   = "no rating given";
            try
            {
                if (_therapyEntryCharField != null)
                {
                    object? c = _therapyEntryCharField.GetValue(entry);
                    // Engine renders the session-row name as
                    // "{last}, {first}" (ScriptCharacter.method_0,
                    // ScriptCharacter.cs:39-42), so match that here
                    // rather than the first-name-only ResolveCharName
                    // path used by the conversational subs.
                    if (c != null) charName = ResolveCharFullName(c);
                }
                if (_therapyEntryDateField != null)
                {
                    object? d = _therapyEntryDateField.GetValue(entry);
                    if (d is DateTime dt)
                        dateStr = dt.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
                    else if (d != null)
                        dateStr = d.ToString() ?? "";
                }
                if (_therapyEntryProgressField != null && _therapyProgressHasValue != null
                    && _therapyProgressGetValue != null)
                {
                    object? prog = _therapyEntryProgressField.GetValue(entry);
                    if (prog != null && (bool)_therapyProgressHasValue.Invoke(prog, null)!)
                    {
                        int n = (int)_therapyProgressGetValue.Invoke(prog, null)!;
                        rating = $"{n} of 5 stars";
                    }
                }
            }
            catch { }

            string header = $"Session {idx + 1} of {total}.";
            string body = string.IsNullOrWhiteSpace(charName)
                ? rating
                : $"{charName}, {dateStr}. {rating}";
            string spoken = $"{header} {body}.";
            Speech.Speak(spoken);
            Log($"[ElizaAppSub] session row {idx}: \"{spoken}\"");
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

        // "{last}, {first}" — what the engine shows on the session-list
        // row (and elsewhere in tabular listings).
        private string ResolveCharFullName(object character)
        {
            string first = ResolveCharName(character);
            string last  = "";
            try
            {
                if (_charLastNameField != null && _charNameToString != null)
                {
                    object? lastObj = _charLastNameField.GetValue(character);
                    if (lastObj != null)
                        last = (string)_charNameToString.Invoke(lastObj, null)! ?? "";
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(last))  return first;
            if (string.IsNullOrWhiteSpace(first)) return last;
            return $"{last}, {first}";
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

        // ── Maybe<T> method resolution ─────────────────────────────────

        private static MethodInfo? ResolveMaybeMethod(Type maybeType, string obfuscatedName)
        {
            return maybeType.GetMethod(obfuscatedName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
        }
    }
}
