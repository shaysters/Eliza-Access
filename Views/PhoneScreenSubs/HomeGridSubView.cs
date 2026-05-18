using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.PhoneScreenSubs
{
    /// <summary>
    /// Announces focus on the phone's home grid — the 8 app icons shown
    /// when no app is open. Replaces V2PhoneObserver's <c>PollHomeFocus</c>
    /// path verbatim, just inside the SubView framework.
    ///
    /// Activation: <c>IsActive ⇔ PhoneState.activeApp</c> is no-value
    /// (i.e. no app is currently open).
    ///
    /// Focus model: the home grid doesn't use the engine's hotspot system
    /// — it uses direct cursor-position-in-polygon checks (PhoneScreen.cs:
    /// 621 reads the cursor each frame). We mirror that: read the cursor,
    /// transform world → phone-local Y-down, point-in-polygon against the
    /// 8 fixed slot shapes.
    ///
    /// Settle detector: a slot must hold focus for <see cref="SettleMs"/>
    /// before we speak. Quick cursor sweeps across the gap between two
    /// adjacent icons get filtered out so labels don't stutter.
    ///
    /// Slot labels are gated on:
    /// - <c>PhoneState.therapySession.HasValue</c>: therapy-active home
    ///   layout is different and unaudited — fall back to "Slot N".
    /// - <c>PhoneState.extrasUnlocked.HasValue</c>: gates Fortune Teller
    ///   (slot 3) and Solitaire (slot 5). When locked, slot still speaks
    ///   the real app's name with " locked" suffix.
    /// - <see cref="ExplorationState.CurrentSessionLabel"/>: intro
    ///   railway chapter ("light-rail*") is where the in-character
    ///   placeholder names ("Calendar" / "Weather" / etc.) are part of
    ///   the experience; after that they're flavor and we drop the suffix.
    /// </summary>
    internal sealed class HomeGridSubView : SubView
    {
        // ── Obfuscated names (stable across builds) ────────────────────
        // The phoneState_0 field exists on BOTH PhoneScreen and
        // WorldScreen at different obfuscated names. This sub can be
        // wired under either parent depending on context: PhoneScreen
        // for the normal phone, WorldScreen for the inline phone
        // rendered during Transparency Mode (which uses different XY
        // offsets — see HomeOffsetX/Y constructor parameters below).
        private const string PhoneScreenPhoneStateField = "#=qqzJofoxKCCizQVMcCRLhAA==";
        private const string WorldScreenPhoneStateField = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        // PhoneState.maybe_4 — Maybe<ScriptTransparency>. When set, the
        // home grid renders a different per-variant layout (PhoneScreen.
        // cs:477-506). The variant is identified by
        // ScriptTransparency.string_0.
        private const string TransparencyMaybeField     = "#=q_jj7ZK0kGqgOw$34jWXLPA==";
        private const string TransparencyStringField    = "#=qzTlRyAmYw3NooOj_3AHcpQ==";
        private const string ActiveAppFieldName     = "#=qS3nVDip0NyI_pzkywVHdyQ==";
        private const string ExtrasUnlockedField    = "#=q9$zL1VcseCl_TCWvb5A1MJEamMGM6lrO_HZIzAOsDj0=";
        private const string InputHelperTypeName    = "#=q$vgfL9f_YZ3eGAQMSYsnbQ==";
        private const string GetMousePosMethodName  = "#=qIhUnQ9sjXk9RKnq7Qfok0Crz9qvI1xhg$$SuphTVVlU=";

        // FakeAppScreen slot loc-keys from PhoneScreen.cs no-therapy mode
        // rendering (lines 540-561 in the decompile). Resolved once at
        // OnInit via the shared Localizer.
        private const int LocKeySlot3Fallback = 1497464147;  // Fortune Teller placeholder
        private const int LocKeySlot4          = 1497464504;  // permanent decorative
        private const int LocKeySlot5Fallback = 1497464091;  // Solitaire placeholder
        private const int LocKeySlot6          = 1497464407;  // permanent decorative
        private const int LocKeySlot7          = 1497464266;  // permanent decorative

        // ── Reflection (OnInit) ────────────────────────────────────────
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _activeAppField;
        private FieldInfo?  _extrasUnlockedField;
        private MethodInfo? _maybeAppHasValue;
        private MethodInfo? _extrasHasValue;
        private FieldInfo?  _transparencyMaybeField;
        private MethodInfo? _transparencyHasValue;
        private MethodInfo? _transparencyGetValue;
        private FieldInfo?  _transparencyStringField;
        private MethodInfo? _getMousePosMethod;
        private FieldInfo?  _vec2XField;
        private FieldInfo?  _vec2YField;
        private PropertyInfo? _vec2XProp;
        private PropertyInfo? _vec2YProp;

        // ── Slot label cache (resolved once via Localizer) ─────────────
        private string? _fakeAppNameSlot3Fallback;
        private string? _fakeAppNameSlot4;
        private string? _fakeAppNameSlot5Fallback;
        private string? _fakeAppNameSlot6;
        private string? _fakeAppNameSlot7;

        // ── Per-activation state ───────────────────────────────────────
        private int _focusedIdx  = -1;
        private int _pendingIdx  = -1;
        private int _pendingTick;
        private const int SettleMs = 60;

        // ── Optional additional IsActive gate ──────────────────────────
        // Passed by the parent view. Lets the WorldScreen-mounted
        // instance require TransparencyState.IsActive so it only fires
        // for the inline phone during Transparency Mode.
        private readonly Func<bool>? _additionalGate;

        public HomeGridSubView(
            Func<bool>? additionalGate = null,
            float homeOffsetX = 669f,
            float homeOffsetY = 268f)
        {
            _additionalGate = additionalGate;
            _homeOffsetX    = homeOffsetX;
            _homeOffsetY    = homeOffsetY;
        }

        // ── Home-grid geometry (verified from PhoneScreen.cs:540-621) ──
        // 8 slot polygons in phone-local Y-down coords. World cursor is
        // transformed via (mouseX - HomeOffsetX, PhoneHeightY - mouseY +
        // HomeOffsetY) before testing containment.
        //
        // PhoneScreen draws the phone at vector2_1 = (669, 268). During
        // Transparency Mode WorldScreen draws the inline phone at
        // (523, 268) (WorldScreen.cs:1329). The XY offset is configured
        // at construction so the polygon hit-test stays accurate under
        // either parent.
        private readonly float _homeOffsetX;
        private readonly float _homeOffsetY;
        private const float PhoneHeightY = 1866f;
        private static readonly float[][] HomePolyX = new float[8][]
        {
            new float[]{ 820, 1028,  819,  612 },
            new float[]{ 707,  916,  708,  501 },
            new float[]{ 365,  572,  365,  158 },
            new float[]{ 252,  458,  251,   44 },
            new float[]{ 936, 1145,  937,  730 },
            new float[]{ 594,  801,  595,  387 },
            new float[]{ 482,  689,  627,  335,  274 },
            new float[]{ 823, 1030,  857,  789,  616 },
        };
        private static readonly float[][] HomePolyY = new float[8][]
        {
            new float[]{  570,  778,  985,  777 },
            new float[]{  912, 1118, 1328, 1119 },
            new float[]{ 1026, 1233, 1439, 1232 },
            new float[]{ 1369, 1575, 1782, 1575 },
            new float[]{ 1141, 1349, 1558, 1348 },
            new float[]{ 1255, 1461, 1669, 1461 },
            new float[]{ 1598, 1805, 1866, 1866, 1805 },
            new float[]{ 1485, 1691, 1865, 1865, 1691 },
        };

        public override void OnInit()
        {
            Type tPS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _phoneStateField = tPS.GetField(PhoneScreenPhoneStateField, inst)
                              ?? tPS.GetField(WorldScreenPhoneStateField, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                _activeAppField      = tPhoneState.GetField(ActiveAppFieldName, inst);
                _extrasUnlockedField = tPhoneState.GetField(ExtrasUnlockedField, inst);
                _transparencyMaybeField = tPhoneState.GetField(TransparencyMaybeField, inst);

                if (_activeAppField != null)
                    _maybeAppHasValue = ResolveHasValue(_activeAppField.FieldType);
                if (_extrasUnlockedField != null)
                    _extrasHasValue   = ResolveHasValue(_extrasUnlockedField.FieldType);
                if (_transparencyMaybeField != null)
                {
                    Type tMaybe = _transparencyMaybeField.FieldType;
                    _transparencyHasValue = ResolveHasValue(tMaybe);
                    // GetValue: the Maybe<T>.GetValue method returns T.
                    // ScriptTransparency is a reference type; pick the
                    // method that returns it.
                    Type tArg = tMaybe.IsGenericType ? tMaybe.GetGenericArguments()[0] : typeof(object);
                    foreach (MethodInfo m in tMaybe.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 && m.ReturnType == tArg)
                        {
                            _transparencyGetValue = m;
                            break;
                        }
                    }
                    if (_transparencyGetValue != null)
                    {
                        Type tST = _transparencyGetValue.ReturnType;
                        _transparencyStringField = tST.GetField(TransparencyStringField, inst);
                    }
                }
            }

            // Cursor position helper — static method on a runtime-named
            // input helper type. Same lookup V2PhoneObserver used.
            Type? tInputHelper = Nagoya.FindType(InputHelperTypeName);
            if (tInputHelper != null)
            {
                _getMousePosMethod = tInputHelper.GetMethod(
                    GetMousePosMethodName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
            }
            if (_getMousePosMethod != null)
            {
                Type tVec2 = _getMousePosMethod.ReturnType;
                _vec2XField = tVec2.GetField("X") ?? tVec2.GetField("x");
                _vec2YField = tVec2.GetField("Y") ?? tVec2.GetField("y");
                if (_vec2XField == null) _vec2XProp = tVec2.GetProperty("X") ?? tVec2.GetProperty("x");
                if (_vec2YField == null) _vec2YProp = tVec2.GetProperty("Y") ?? tVec2.GetProperty("y");
            }

            // Slot fake-app names. Localizer is shared across the mod;
            // missing keys just return null and the label falls back.
            Localizer.TryInit();
            _fakeAppNameSlot3Fallback = Localizer.TryLocalize(LocKeySlot3Fallback);
            _fakeAppNameSlot4         = Localizer.TryLocalize(LocKeySlot4);
            _fakeAppNameSlot5Fallback = Localizer.TryLocalize(LocKeySlot5Fallback);
            _fakeAppNameSlot6         = Localizer.TryLocalize(LocKeySlot6);
            _fakeAppNameSlot7         = Localizer.TryLocalize(LocKeySlot7);

            Log($"[HomeGridSub] init: phoneState={(_phoneStateField != null)} " +
                $"activeApp={(_activeAppField != null)} " +
                $"extras={(_extrasUnlockedField != null)} " +
                $"getMousePos={(_getMousePosMethod != null)} " +
                $"vec2.X={(_vec2XField != null || _vec2XProp != null)} " +
                $"slot3=\"{_fakeAppNameSlot3Fallback}\" slot4=\"{_fakeAppNameSlot4}\" " +
                $"slot5=\"{_fakeAppNameSlot5Fallback}\" slot6=\"{_fakeAppNameSlot6}\" slot7=\"{_fakeAppNameSlot7}\"");
        }

        public override bool IsActive
        {
            get
            {
                if (_additionalGate != null && !_additionalGate()) return false;
                if (_phoneStateField == null || _activeAppField == null
                    || _maybeAppHasValue == null) return false;
                try
                {
                    object? phoneState = _phoneStateField.GetValue(Parent);
                    if (phoneState == null) return false;
                    object? maybeApp = _activeAppField.GetValue(phoneState);
                    if (maybeApp == null) return false;
                    return !(bool)_maybeAppHasValue.Invoke(maybeApp, null)!;
                }
                catch { return false; }
            }
        }

        public override void OnEnter()
        {
            _focusedIdx = -1;
            _pendingIdx = -1;

            // Announce the screen context so the user knows where they
            // landed — whether they just opened the phone or just
            // closed an app back to the grid.
            Speech.Speak("Phone home screen.");

            // Pre-seed _focusedIdx with whichever slot the cursor is
            // currently over (without announcing the slot). The home
            // grid becoming active means the user just closed an app
            // or just opened the phone — the cursor is wherever it
            // was left, not somewhere they actively navigated to. The
            // engine's app-close animation also leaves the phone on
            // screen for ~150 ms after activeApp clears, during which
            // OnUpdate would otherwise settle on the stale cursor's
            // slot and announce it. Seeding _focusedIdx suppresses
            // that first slot announcement; only when the user
            // actually moves the cursor to a different slot will we
            // speak again.
            if (!TryReadCursor(out float mouseX, out float mouseY)) return;
            float localX = mouseX - _homeOffsetX;
            float localY = PhoneHeightY - mouseY + _homeOffsetY;
            for (int i = 0; i < 8; i++)
            {
                if (PointInPolygon(localX, localY, HomePolyX[i], HomePolyY[i]))
                {
                    _focusedIdx = i;
                    break;
                }
            }
        }

        public override void OnLeave()
        {
            _focusedIdx = -1;
            _pendingIdx = -1;
        }

        public override void OnUpdate()
        {
            if (_phoneStateField == null) return;
            object? phoneState;
            try { phoneState = _phoneStateField.GetValue(Parent); }
            catch { return; }
            if (phoneState == null) return;

            if (!TryReadCursor(out float mouseX, out float mouseY)) return;

            // World → phone-local Y-down transform (PhoneScreen.cs:621).
            float localX = mouseX - _homeOffsetX;
            float localY = PhoneHeightY - mouseY + _homeOffsetY;

            int found = -1;
            for (int i = 0; i < 8; i++)
            {
                if (PointInPolygon(localX, localY, HomePolyX[i], HomePolyY[i]))
                { found = i; break; }
            }

            // When the cursor briefly leaves all polygons (found=-1),
            // keep the last announced slot rather than resetting —
            // otherwise dragging across the gap between two adjacent
            // icons would re-announce the destination slot.
            if (found < 0) return;
            if (found == _focusedIdx) return;

            // Settle detector: a slot must hold focus for SettleMs before
            // we speak. Quick cursor sweeps (50–100ms transitions) get
            // filtered out so "Decorative app" doesn't start three times
            // in a row when dragging across the grid.
            int now = Environment.TickCount;
            if (found != _pendingIdx)
            {
                _pendingIdx  = found;
                _pendingTick = now;
                return;
            }
            if (now - _pendingTick < SettleMs) return;

            _focusedIdx = found;
            _pendingIdx = -1;

            string label = ResolveSlotLabel(phoneState, found);
            Speech.Speak(label);
            Log($"[HomeGridSub] focus → [{found}] {label}");
        }

        // ── Slot label resolution ──────────────────────────────────────

        private string ResolveSlotLabel(object phoneState, int slot)
        {
            bool extrasUnlocked = false;
            if (_extrasUnlockedField != null && _extrasHasValue != null)
            {
                try
                {
                    object? extras = _extrasUnlockedField.GetValue(phoneState);
                    if (extras != null)
                        extrasUnlocked = (bool)_extrasHasValue.Invoke(extras, null)!;
                }
                catch { }
            }

            // Transparency Mode swaps in a per-variant icon layout
            // (PhoneScreen.cs:483-505). Decode the variant via
            // ScriptTransparency.string_0 and look up the icon at this
            // slot. The textures themselves are loaded by GClass60 with
            // human-readable asset paths ("textures/phone/app_icons/X"),
            // so we know exactly which icon is at each slot per variant.
            string? variant = ReadTransparencyVariant(phoneState);
            if (variant != null)
            {
                string? texture = TransparencyIcon(variant, slot);
                if (texture != null) return IconLabel(texture);
                return $"Slot {slot + 1}";
            }

            // Default (no Transparency Mode) layout. Slots 0/1/2 are
            // always Eliza / Messages / Mail. Slots 3/5 swap between
            // a real app (Fortune Teller / Solitaire) and a fake
            // placeholder (yokan / kabufuda textures) based on
            // extrasUnlocked. 4/6/7 are decorative-only.
            switch (slot)
            {
                case 0: return "Eliza";
                case 1: return "Messages";
                case 2: return "Mail";
                case 3:
                    return extrasUnlocked
                        ? "Fortune Teller"
                        : (IsIntroChapter() && !string.IsNullOrWhiteSpace(_fakeAppNameSlot3Fallback)
                            ? $"Fortune Teller, locked. Currently shown as {NormalizeAppText(_fakeAppNameSlot3Fallback)}"
                            : "Fortune Teller, locked");
                case 4: return "Zenny app"; // texture_32/33 = zenny
                case 5:
                    return extrasUnlocked
                        ? "Solitaire"
                        : (IsIntroChapter() && !string.IsNullOrWhiteSpace(_fakeAppNameSlot5Fallback)
                            ? $"Solitaire, locked. Currently shown as {NormalizeAppText(_fakeAppNameSlot5Fallback)}"
                            : "Solitaire, locked");
                case 6: return "Bijoux app";          // texture_0/1 = bijoux
                case 7: return "Skandha Wellness app"; // texture_28/29 = wellness
                default: return $"Slot {slot + 1}";
            }
        }

        // ── Transparency-Mode slot maps (PhoneScreen.cs:483-505) ───────
        // Each variant renders a different set of slots with different
        // textures. The mapping below is keyed by ScriptTransparency.
        // string_0 — the three variants come from ScriptTransparencies.
        // cs ("maya-transparency", "holiday-transparency",
        // "soren-transparency"). Slot indices not in a map are slots
        // the engine doesn't draw for that variant (no hotspot, but
        // the polygon will still match — return null so we can fall
        // back to a generic "Slot N" announcement).

        private static string? TransparencyIcon(string variant, int slot)
        {
            switch (variant)
            {
                case "maya-transparency":
                    switch (slot)
                    {
                        case 0: return "mail_maya";  // PhoneScreen.cs:483
                        case 1: return "chat";       // :484
                        case 2: return "wellness";   // :485
                        case 3: return "sketch";     // :486
                        case 4: return "palki";      // :487
                        case 5: return "vido";       // :488
                    }
                    break;
                case "holiday-transparency":
                    switch (slot)
                    {
                        case 0: return "steward";    // PhoneScreen.cs:492
                        case 1: return "chat";       // :493
                        case 2: return "mail";       // :494
                        case 5: return "wellness";   // :495
                    }
                    break;
                case "soren-transparency":
                    switch (slot)
                    {
                        case 0: return "proxy";      // PhoneScreen.cs:499
                        case 1: return "chat";       // :500
                        case 2: return "mail";       // :501
                        case 3: return "neat";       // :502
                        case 4: return "madrona";    // :503
                        case 5: return "camera";     // :504
                        case 6: return "montlake";   // :505
                    }
                    break;
            }
            return null;
        }

        // Texture file name → human-readable app label. Texture names
        // come from GClass60's TextureLoader paths
        // ("textures/phone/app_icons/X").
        private static string IconLabel(string texture)
        {
            switch (texture)
            {
                case "mail":       return "Mail";
                case "mail_maya":  return "Mail";
                case "chat":       return "Messages";
                case "proxy":      return "Eliza";
                case "yokan":      return "Fortune Teller";
                case "kabufuda":   return "Solitaire";
                case "wellness":   return "Skandha Wellness";
                case "sketch":     return "Sketch Layer";
                case "palki":      return "Palki";
                case "vido":       return "Vido";
                case "neat":       return "Neat";
                case "madrona":    return "Madrona";
                case "camera":     return "Camera";
                case "montlake":   return "Montlake";
                case "steward":    return "Steward";
                case "bijoux":     return "Bijoux";
                case "zenny":      return "Zenny";
                default:           return texture;
            }
        }

        private string? ReadTransparencyVariant(object phoneState)
        {
            if (_transparencyMaybeField == null
                || _transparencyHasValue == null
                || _transparencyGetValue == null
                || _transparencyStringField == null) return null;
            try
            {
                object? maybe = _transparencyMaybeField.GetValue(phoneState);
                if (maybe == null) return null;
                if (!(bool)_transparencyHasValue.Invoke(maybe, null)!) return null;
                object? st = _transparencyGetValue.Invoke(maybe, null);
                if (st == null) return null;
                return _transparencyStringField.GetValue(st) as string;
            }
            catch { return null; }
        }

        // The intro railway chapter ("light-rail*") is the only point
        // where the fake-app placeholder text is part of the player's
        // narrative experience — the phone is presented as in-character.
        // Once past the railway, those placeholder names stop being
        // relevant and we drop the "Currently shown as ..." suffix.
        private static bool IsIntroChapter()
        {
            string? lbl = ExplorationState.CurrentSessionLabel;
            return !string.IsNullOrEmpty(lbl) && lbl.StartsWith("light-rail", StringComparison.Ordinal);
        }

        // ── Helpers ────────────────────────────────────────────────────

        private bool TryReadCursor(out float x, out float y)
        {
            x = y = 0f;
            if (_getMousePosMethod == null) return false;
            if ((_vec2XField == null && _vec2XProp == null)
                || (_vec2YField == null && _vec2YProp == null)) return false;
            try
            {
                object mouse = _getMousePosMethod.Invoke(null, null)!;
                x = (float)(_vec2XField != null
                    ? _vec2XField.GetValue(mouse)!
                    : _vec2XProp!.GetValue(mouse)!);
                y = (float)(_vec2YField != null
                    ? _vec2YField.GetValue(mouse)!
                    : _vec2YProp!.GetValue(mouse)!);
                return true;
            }
            catch { return false; }
        }

        private static MethodInfo? ResolveHasValue(Type maybeType)
        {
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool))
                    return m;
            }
            return null;
        }

        // Strip trailing ellipsis (… or ...) so TTS doesn't say "dot" —
        // the caller's suffix or sentence context provides closure.
        private static string NormalizeAppText(string? s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith("…")) s = s.Substring(0, s.Length - 1).TrimEnd();
            else if (s.EndsWith("...")) s = s.Substring(0, s.Length - 3).TrimEnd();
            return s;
        }

        // Standard ray-cast point-in-polygon. Polygons here are convex
        // (4-5 vertices).
        private static bool PointInPolygon(float x, float y, float[] vx, float[] vy)
        {
            bool inside = false;
            int n = vx.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (((vy[i] > y) != (vy[j] > y)) &&
                    (x < (vx[j] - vx[i]) * (y - vy[i]) / (vy[j] - vy[i]) + vx[i]))
                    inside = !inside;
            }
            return inside;
        }
    }
}
