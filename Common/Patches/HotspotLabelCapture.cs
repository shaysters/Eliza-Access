using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Generic hotspot-to-label pairing for every screen in the game.
    ///
    /// The game's hotspot system (<c>VirtualInput</c>'s <c>list_0</c>) stores
    /// bounds + center + two booleans for each focusable element. There is no
    /// label attached. Screens draw text and register hotspots independently;
    /// the label is just a draw call near the hotspot's bounds.
    ///
    /// The pattern in <c>SettingsScreen.method_0</c> (and analogous code in
    /// every other screen) is tightly temporal: the screen calls
    /// <c>GClass105.smethod_41</c> to draw a localized string, captures the
    /// returned <c>Bounds2</c>, then calls <c>VirtualInput.smethod_7</c> with
    /// a hotspot bounds that contains the text bounds (it's
    /// <c>textBounds.Expanded(...).UnionedWith(iconBounds)</c>). So if we
    /// observe both calls and pair them by spatial containment, we recover
    /// the label for free — no hardcoded tables, no per-screen mapping.
    ///
    /// Patches:
    ///   1. <c>smethod_41</c> postfix — append (text, bounds) to a per-frame
    ///      pending-texts list.
    ///   2. <c>smethod_7</c> postfix — find the most recent pending text
    ///      whose bounds is contained in the hotspot bounds; append the
    ///      label (or null) to a per-frame hotspot-labels list, indexed in
    ///      the same order as <c>list_0</c>.
    ///   3. <c>smethod_9</c> postfix — frame boundary; clear both lists so
    ///      next frame's screen render starts fresh. (Mirrors <c>list_0</c>'s
    ///      own <c>Clear</c> at line 432.)
    ///
    /// Observers call <see cref="GetLabel"/> with a hotspot index and get
    /// either the captured label or <c>null</c> if no text was paired (slider
    /// tracks are the common case — the label is drawn above the track, not
    /// inside it, so containment fails; observers handle sliders separately).
    ///
    /// Why this is safer than the existing per-observer mapping tables:
    /// labels survive layout changes. Zachtronics inserting a Display
    /// Quality section between Window Size and Effect Volume bumped the
    /// hotspot count from 17 to 20 and silently broke every speech
    /// announcement in <c>V2SettingsObserver</c>. With label capture, that
    /// kind of change doesn't break anything.
    ///
    /// Targets resolved by signature match (no obfuscated names hardcoded
    /// for the cross-type ones, because the type names themselves are
    /// obfuscated and we can't rely on de4dot's renames matching what's
    /// loaded at runtime).
    /// </summary>
    internal static class HotspotLabelCapture
    {
        public static string? LogPath;

        // Per-frame state — guarded by _lock because observers poll on a
        // separate thread from the game's render thread.
        private static readonly object _lock = new object();
        private static readonly List<TextRecord> _pendingTexts = new List<TextRecord>(64);
        private static readonly List<string?>   _hotspotLabels = new List<string?>(64);

        private sealed class TextRecord
        {
            public readonly string Text;
            public readonly float MinX, MinY, MaxX, MaxY;
            // Consumed flag: a label can pair with at most one hotspot.
            // Section headings (drawn via smethod_41 but never paired) stay
            // un-consumed and never accidentally match — once newer texts
            // are drawn, they'll be picked first by the most-recent walk.
            public bool Consumed;
            // IsHeading: text drawn with EnableMeasuring=false (smethod_41
            // bool_1 arg = false) so smethod_49 returned an empty Bounds2.
            // SettingsScreen uses this for section headings ("Display Mode",
            // "Display Quality", "Auto-Advance", "Language") and slider
            // labels ("Effect Volume", "Voice Volume", "Music Volume").
            // Button texts go through smethod_41 with bool_1=true so they
            // have W>0 and H>0.
            public readonly bool IsHeading;
            public TextRecord(string t, float minX, float minY, float maxX, float maxY)
            { Text = t; MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
              IsHeading = (maxX - minX) < 1f && (maxY - minY) < 1f; }
        }

        // Most recently drawn section heading. Updated each time a
        // heading-style text fires through smethod_49. Cleared at frame
        // boundary so labels from frame N don't bleed into frame N+1.
        // Used to prefix button labels (e.g. "Display Quality: High (4K)").
        private static string? _currentSection;

        // Reflection cache for Bounds2 / Vector2 field access. Lazy; needs
        // Nagoya loaded so it can resolve the types by short name.
        private static bool _cached;
        private static FieldInfo? _boundsMin, _boundsMax, _vec2X, _vec2Y;

        // VirtualInput.list_0 — the actual hotspot list. We mirror its
        // Count to detect smethod_7 calls that were suppressed (gclass6_0
        // bool_1 set under stacked screens) and didn't grow list_0.
        private static FieldInfo? _list0Field;
        private static bool _list0Resolved;

        private static int ReadList0Count()
        {
            if (!_list0Resolved)
            {
                _list0Resolved = true;
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI != null)
                {
                    foreach (FieldInfo f in tVI.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!f.FieldType.IsGenericType) continue;
                        if (f.FieldType.GetGenericTypeDefinition() != typeof(List<>)) continue;
                        Type elem = f.FieldType.GetGenericArguments()[0];
                        if (elem.Name == "VirtualInputHotspot")
                        {
                            _list0Field = f;
                            break;
                        }
                    }
                }
            }
            if (_list0Field == null) return -1;
            try
            {
                if (_list0Field.GetValue(null) is IList list) return list.Count;
            }
            catch { }
            return -1;
        }

        private static bool TryReadBounds(object boundsBoxed,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = minY = maxX = maxY = 0f;
            if (boundsBoxed == null) return false;
            if (!_cached)
            {
                // Resolve fields from the boxed instance's runtime type —
                // no dependence on Nagoya.FindType matching a name.
                Type tB = boundsBoxed.GetType();
                _boundsMin = tB.GetField("Min", BindingFlags.Public | BindingFlags.Instance);
                _boundsMax = tB.GetField("Max", BindingFlags.Public | BindingFlags.Instance);
                if (_boundsMin != null)
                {
                    // Vector2's field names ARE obfuscated at runtime (the
                    // de4dot decompile shows "float_0" / "float_1" but
                    // those are renames; runtime names are #=q...).
                    // Find by type: the first two public instance float
                    // fields, in declaration order = X then Y.
                    Type tV = _boundsMin.FieldType;
                    FieldInfo[] vecFields = tV.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    int floatsFound = 0;
                    foreach (FieldInfo f in vecFields)
                    {
                        if (f.FieldType != typeof(float)) continue;
                        if (floatsFound == 0) _vec2X = f;
                        else if (floatsFound == 1) { _vec2Y = f; break; }
                        floatsFound++;
                    }
                }
                _cached = true;
            }
            if (_boundsMin == null || _boundsMax == null || _vec2X == null || _vec2Y == null)
                return false;
            try
            {
                object? min = _boundsMin.GetValue(boundsBoxed);
                object? max = _boundsMax.GetValue(boundsBoxed);
                if (min == null || max == null) return false;
                minX = (float)_vec2X.GetValue(min)!;
                minY = (float)_vec2Y.GetValue(min)!;
                maxX = (float)_vec2X.GetValue(max)!;
                maxY = (float)_vec2Y.GetValue(max)!;
                return true;
            }
            catch { return false; }
        }

        // Last-seen timestamps + most-recent draw position (centroid X)
        // per unique text string. Other observers ask "was this drawn
        // recently?" and "where was it drawn?" so they can tell on-screen
        // content from off-screen draws (the engine keeps issuing
        // draw calls for the in-session Eliza panel after it animates
        // off the right edge — without position, we'd announce labels
        // that are no longer visible).
        //
        // The dictionary grows by unique strings drawn over the session
        // (every label, every character glyph). Memory is a non-issue
        // in practice — a few thousand entries at most.
        private static readonly Dictionary<string, long>  _lastSeenTick = new Dictionary<string, long>();
        private static readonly Dictionary<string, float> _lastSeenX    = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _lastSeenY    = new Dictionary<string, float>();

        /// <summary>Returns true if <paramref name="text"/> was drawn on
        /// screen within the last <paramref name="withinSeconds"/> seconds.
        /// Match is exact-string. Use for detecting whether a specific
        /// localized label is currently visible (e.g. "Session Results"
        /// for the post-session panel) without coupling to its hotspot
        /// pairing.</summary>
        public static bool WasTextDrawnRecently(string text, double withinSeconds = 0.25)
        {
            if (string.IsNullOrEmpty(text)) return false;
            long now = DateTime.UtcNow.Ticks;
            long thresholdTicks = (long)(withinSeconds * 10_000_000);
            lock (_lock)
            {
                if (!_lastSeenTick.TryGetValue(text, out long lastTick)) return false;
                return (now - lastTick) <= thresholdTicks;
            }
        }

        /// <summary>Returns the most recent draw position (centroid) for
        /// <paramref name="text"/> if it was drawn within
        /// <paramref name="withinSeconds"/> seconds; null otherwise. Use
        /// to filter texts by visible screen area (the engine keeps
        /// drawing some panels at off-screen X coords after they animate
        /// out — same text, different position).</summary>
        public static (float X, float Y)? LastDrawnPosition(string text, double withinSeconds = 0.25)
        {
            if (string.IsNullOrEmpty(text)) return null;
            long now = DateTime.UtcNow.Ticks;
            long thresholdTicks = (long)(withinSeconds * 10_000_000);
            lock (_lock)
            {
                if (!_lastSeenTick.TryGetValue(text, out long lastTick)) return null;
                if (now - lastTick > thresholdTicks) return null;
                _lastSeenX.TryGetValue(text, out float x);
                _lastSeenY.TryGetValue(text, out float y);
                return (x, y);
            }
        }

        /// <summary>One snapshot entry — the rendered string and the
        /// centroid screen position where the engine most recently drew
        /// it.</summary>
        public readonly struct DrawnText
        {
            public readonly string Text;
            public readonly float  X;
            public readonly float  Y;
            public DrawnText(string text, float x, float y) { Text = text; X = x; Y = y; }
        }

        /// <summary>Returns distinct strings drawn within
        /// <paramref name="withinSeconds"/> seconds whose centroid X
        /// falls in <c>[minX, maxX]</c> and Y in <c>[minY, maxY]</c>,
        /// each paired with its draw position. Use to scope a panel-
        /// content dump to the visible screen region the panel
        /// occupies, filtering out off-screen-drawn labels and other
        /// UI bleed-through. Position lets the caller sort by reading
        /// order or group glyphs the engine drew separately (e.g.
        /// dollar amounts rendered as "$", "0", ".", "0", "0").</summary>
        public static DrawnText[] SnapshotRecentTextsInBoundsWithPosition(
            float minX, float maxX, float minY, float maxY, double withinSeconds = 0.25)
        {
            long now = DateTime.UtcNow.Ticks;
            long thresholdTicks = (long)(withinSeconds * 10_000_000);
            lock (_lock)
            {
                var result = new List<DrawnText>(_lastSeenTick.Count);
                foreach (var kv in _lastSeenTick)
                {
                    if (now - kv.Value > thresholdTicks) continue;
                    if (!_lastSeenX.TryGetValue(kv.Key, out float x)) continue;
                    if (!_lastSeenY.TryGetValue(kv.Key, out float y)) continue;
                    if (x < minX || x > maxX) continue;
                    if (y < minY || y > maxY) continue;
                    result.Add(new DrawnText(kv.Key, x, y));
                }
                return result.ToArray();
            }
        }

        // Called from TextDrawPatch.Postfix.
        public static void OnTextDraw(string? text, object? boundsBoxed)
        {
            if (string.IsNullOrEmpty(text) || boundsBoxed == null) return;
            if (!TryReadBounds(boundsBoxed, out float minX, out float minY, out float maxX, out float maxY))
                return;
            lock (_lock)
            {
                _lastSeenTick[text!] = DateTime.UtcNow.Ticks;
                _lastSeenX[text!]    = (minX + maxX) * 0.5f;
                _lastSeenY[text!]    = (minY + maxY) * 0.5f;
                var rec = new TextRecord(text!, minX, minY, maxX, maxY);
                _pendingTexts.Add(rec);
                if (rec.IsHeading)
                {
                    // Track the most recent heading as the "current section".
                    // Headings are unbounded text (W=H=0) drawn at the left
                    // of the menu before each section's buttons. The PAGE
                    // title ("Options") also matches this pattern but is
                    // overwritten before any hotspot registers, so it never
                    // ends up prefixing a button label.
                    _currentSection = text;
                }
            }
        }

        // Called from HotspotRegisterPatch.Postfix.
        public static void OnHotspotRegister(object? boundsBoxed)
        {
            // Sync to list_0: smethod_7 doesn't always add (gclass6_0.bool_1
            // suppresses under stacked screens). If list_0 didn't grow,
            // this call was a no-op — don't append a label.
            int liveCount = ReadList0Count();
            string? matched = null;
            float hMinX = 0f, hMinY = 0f, hMaxX = 0f, hMaxY = 0f;
            bool haveBounds = boundsBoxed != null
                && TryReadBounds(boundsBoxed, out hMinX, out hMinY, out hMaxX, out hMaxY);

            lock (_lock)
            {
                if (liveCount >= 0 && liveCount <= _hotspotLabels.Count) return;
                // Pass 1: containment match. Buttons in method_0 have
                // hotspot bounds = textBounds.UnionedWith(iconBounds), so
                // text bounds ⊆ hotspot bounds. Highest confidence.
                if (haveBounds)
                {
                    for (int i = _pendingTexts.Count - 1; i >= 0; i--)
                    {
                        TextRecord r = _pendingTexts[i];
                        if (r.Consumed) continue;
                        if (r.MinX >= hMinX && r.MinY >= hMinY
                            && r.MaxX <= hMaxX && r.MaxY <= hMaxY)
                        {
                            matched = r.Text;
                            r.Consumed = true;
                            break;
                        }
                    }
                }
                // Pass 2: most-recent-unmatched fallback, gated by Y
                // proximity. Sliders draw their label at the LEFT of the
                // screen (X≈1874) but the slider hotspot is the wide
                // track on the right (X center ≈3600) — same Y row but
                // ~1700 X apart. A pure-X gate would reject them; instead
                // we use a tight Y gate that still rejects cross-screen
                // text bleed (e.g., a stale SettingsScreen "High (4K)"
                // label at Y=1335 won't match a confirmation-dialog button
                // at a different Y).
                //
                // Y tolerance ~80 covers slider labels (Y offset ~17 from
                // track), section headings drawn above their buttons,
                // and small font-baseline differences, without bridging
                // across menu rows (which are separated by ~130+ units).
                if (matched == null && haveBounds)
                {
                    float hCenterY = (hMinY + hMaxY) * 0.5f;
                    const float MaxYOffset = 80f;
                    for (int i = _pendingTexts.Count - 1; i >= 0; i--)
                    {
                        TextRecord r = _pendingTexts[i];
                        if (r.Consumed) continue;
                        float tCenterY = (r.MinY + r.MaxY) * 0.5f;
                        if (Math.Abs(tCenterY - hCenterY) > MaxYOffset) continue;
                        matched = r.Text;
                        r.Consumed = true;
                        break;
                    }
                }
                // Prefix with the current section heading so screen-reader
                // users hear "Display Quality: High (4K)" instead of just
                // "High (4K)". If the matched text IS the section heading
                // itself (sliders: "Effect Volume" matches its own header),
                // don't double up. If we have no heading (e.g. Title menu),
                // leave the label as-is.
                if (matched != null && _currentSection != null && matched != _currentSection)
                {
                    matched = $"{_currentSection}: {matched}";
                }
                _hotspotLabels.Add(matched);
            }
        }

        // Called from FrameBoundaryPatch.Postfix.
        public static void OnFrameBoundary()
        {
            lock (_lock)
            {
                _pendingTexts.Clear();
                _hotspotLabels.Clear();
                _currentSection = null;
            }
        }

        // Observer API. Returns the label captured for the given hotspot
        // index in list_0, or null if no text was paired or the index is
        // out of range.
        public static string? GetLabel(int hotspotIndex)
        {
            lock (_lock)
            {
                if (hotspotIndex < 0 || hotspotIndex >= _hotspotLabels.Count) return null;
                return _hotspotLabels[hotspotIndex];
            }
        }

        public static int LabelCount
        {
            get { lock (_lock) { return _hotspotLabels.Count; } }
        }

        // Diagnostic: snapshot of current frame's labels for log dumps.
        public static string[] SnapshotLabels()
        {
            lock (_lock) { return _hotspotLabels.ToArray(); }
        }

        // ── Patch 1: GClass105.smethod_49 (text render entry point) ────
        // Located by signature: static, returns Bounds2, 1 param of a
        // struct type with a public string field named "Text". This is
        // the centralized renderer that smethod_41, TextDrawingOptions.
        // Draw(), Measure(), and DrawAndMeasure() all call. Hooking it
        // catches every text the game draws regardless of which screen-
        // local helper produced it.

        [HarmonyPatch]
        internal static class TextDrawPatch
        {
            internal static MethodInfo? Resolved;
            internal static FieldInfo?  TextField;        // TextDrawingOptions.Text
            internal static FieldInfo?  EnableDrawField;  // TextDrawingOptions.EnableDrawing

            [HarmonyTargetMethod]
            internal static MethodBase? Target()
            {
                if (Resolved != null) return Resolved;
                foreach (Type t in Nagoya.Types)
                {
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (MethodInfo m in methods)
                    {
                        if (m.ReturnType.Name != "Bounds2") continue;
                        ParameterInfo[] p = m.GetParameters();
                        if (p.Length != 1) continue;
                        Type pt = p[0].ParameterType;
                        if (!pt.IsValueType) continue;
                        FieldInfo? text = pt.GetField("Text", BindingFlags.Public | BindingFlags.Instance);
                        if (text == null || text.FieldType != typeof(string)) continue;
                        FieldInfo? enableDraw = pt.GetField("EnableDrawing", BindingFlags.Public | BindingFlags.Instance);
                        if (enableDraw == null) continue;
                        TextField = text;
                        EnableDrawField = enableDraw;
                        Resolved = m;
                        return m;
                    }
                }
                return null;
            }

            [HarmonyPostfix]
            private static void Postfix(object __0, object __result)
            {
                // Filter to actual-draw calls; Measure() also goes here
                // but with EnableDrawing=false and we don't want those.
                if (__0 == null || TextField == null || EnableDrawField == null) return;
                try
                {
                    if (EnableDrawField.GetValue(__0) is bool ed && !ed) return;
                }
                catch { return; }

                string? text = TextField.GetValue(__0) as string;
                OnTextDraw(text, __result);
            }
        }

        // ── Patch 2: VirtualInput.smethod_7 (hotspot register) ─────────
        // Static, void return, 4 params: (Bounds2, Vector2, bool, bool).

        [HarmonyPatch]
        internal static class HotspotRegisterPatch
        {
            internal static MethodInfo? Resolved;

            [HarmonyTargetMethod]
            internal static MethodBase? Target()
            {
                if (Resolved != null) return Resolved;
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI == null) return null;
                foreach (MethodInfo m in tVI.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.ReturnType != typeof(void)) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length != 4) continue;
                    if (p[0].ParameterType.Name != "Bounds2") continue;
                    if (p[1].ParameterType.Name != "Vector2") continue;
                    if (p[2].ParameterType != typeof(bool)) continue;
                    if (p[3].ParameterType != typeof(bool)) continue;
                    Resolved = m;
                    return m;
                }
                return null;
            }

            [HarmonyPostfix]
            private static void Postfix(object __0)
            {
                OnHotspotRegister(__0);
            }
        }

        // ── Patch 3: VirtualInput.smethod_9 postfix (frame boundary) ───
        // smethod_9 itself clears list_0 at line 432; we mirror that by
        // clearing our per-frame state at the same point. Targeted by
        // obfuscated name (already known from InputFlipGuard).

        [HarmonyPatch]
        internal static class FrameBoundaryPatch
        {
            internal static MethodInfo? Resolved;

            [HarmonyTargetMethod]
            internal static MethodBase? Target()
            {
                if (Resolved != null) return Resolved;
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI == null) return null;
                foreach (MethodInfo m in tVI.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != InputFlipGuard.UpdateMethodName) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == typeof(float))
                    {
                        Resolved = m;
                        return m;
                    }
                }
                return null;
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                OnFrameBoundary();
            }
        }
    }
}
