using System;
using System.Collections;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Resolves "which hotspot index is currently under the cursor?".
    ///
    /// Shared by every cursor-based <see cref="ScreenView"/> so each one
    /// doesn't have to re-bind <c>VirtualInput.list_0</c>, the hotspot's
    /// bounds field, <c>Bounds2.Contains(Vector2)</c>, and the cursor-
    /// position getter from scratch (which is most of the boilerplate in
    /// the legacy observers).
    ///
    /// Indexing matches <c>VirtualInput.list_0</c> exactly, so a returned
    /// index can be handed straight to
    /// <see cref="Patches.HotspotLabelCapture.GetLabel"/> to recover the
    /// label that capture paired with this slot.
    /// </summary>
    internal static class CursorFocus
    {
        private static bool _resolved;
        private static FieldInfo?  _list0Field;        // VirtualInput.list_0 (List<VirtualInputHotspot>)
        private static FieldInfo?  _hotspotBoundsField; // VirtualInputHotspot.bounds2_0
        private static MethodInfo? _boundsContainsMethod;  // Bounds2.Contains(Vector2)
        private static MethodInfo? _cursorPosMethod;       // VirtualInput.smethod_4() → Vector2

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return;

            // list_0: the non-public static List<VirtualInputHotspot>
            foreach (FieldInfo f in tVI.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!f.FieldType.IsGenericType) continue;
                if (f.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) continue;
                Type elem = f.FieldType.GetGenericArguments()[0];
                if (elem.Name != "VirtualInputHotspot") continue;
                _list0Field = f;
                break;
            }

            // VirtualInputHotspot bounds field: the only Bounds2-typed
            // public instance field on the hotspot type.
            Type? tHotspot = Nagoya.FindType("VirtualInputHotspot");
            if (tHotspot != null)
            {
                foreach (FieldInfo f in tHotspot.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.Name == "Bounds2") { _hotspotBoundsField = f; break; }
                }
            }

            // Bounds2.Contains(Vector2)
            Type? tBounds = Nagoya.FindType("Bounds2");
            Type? tVec    = Nagoya.FindType("Vector2");
            if (tBounds != null && tVec != null)
            {
                _boundsContainsMethod = tBounds.GetMethod(
                    "Contains", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { tVec }, null);
            }

            // Cursor position getter: a static no-arg method on VirtualInput
            // returning Vector2. Exactly one such method exists (smethod_4,
            // which delegates to GClass5.smethod_23 and handles the
            // controller-vs-mouse mode selection).
            //
            // Use NonPublic | Public: the de4dot clean decompile labels this
            // as `public static`, but the runtime obfuscated DLL has it as
            // private static — de4dot widens visibility during decompile
            // and matching the decompile's surface gets a null lookup.
            // Same trap we hit with Vector2.float_0 vs the obfuscated field
            // name earlier.
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType.Name != "Vector2") continue;
                _cursorPosMethod = m;
                break;
            }
        }

        /// <summary>
        /// Diagnostic: returns the current cursor as a "(x, y)" string, or
        /// "<unresolved>" if the binding hasn't resolved yet. Useful when
        /// a view sees idx=-1 and we need to know whether the cursor is
        /// off-screen, at TopLeft, or somewhere unexpected.
        /// </summary>
        public static string CursorPositionString()
        {
            EnsureResolved();
            if (_cursorPosMethod == null) return "<unresolved>";
            object? cursor;
            try { cursor = _cursorPosMethod.Invoke(null, null); }
            catch { return "<throw>"; }
            return cursor?.ToString() ?? "<null>";
        }

        /// <summary>
        /// Diagnostic: returns the hotspot count from VirtualInput.list_0.
        /// </summary>
        public static int HotspotCount()
        {
            EnsureResolved();
            if (_list0Field == null) return -1;
            try
            {
                IList? list = _list0Field.GetValue(null) as IList;
                return list?.Count ?? -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Returns the index into <c>VirtualInput.list_0</c> of the hotspot
        /// whose bounds currently contain the cursor, or -1 if the cursor
        /// is outside every hotspot / the binding hasn't been resolved.
        ///
        /// First-match wins (matches the game's own focus semantics —
        /// hotspots don't normally overlap, and when they do the first
        /// registered is the conceptually-foreground one).
        /// </summary>
        public static int FindHotspotIndex()
        {
            EnsureResolved();
            if (_list0Field == null || _hotspotBoundsField == null
                || _boundsContainsMethod == null || _cursorPosMethod == null)
                return -1;

            object? cursor;
            try { cursor = _cursorPosMethod.Invoke(null, null); }
            catch { return -1; }
            if (cursor == null) return -1;

            IList? list;
            try { list = _list0Field.GetValue(null) as IList; }
            catch { return -1; }
            if (list == null) return -1;

            int count = list.Count;
            object[] containsArgs = new object[] { cursor };
            for (int i = 0; i < count; i++)
            {
                object? hotspot;
                try { hotspot = list[i]; }
                catch { break; }   // list mutated mid-iteration; bail
                if (hotspot == null) continue;

                object? bounds;
                try { bounds = _hotspotBoundsField.GetValue(hotspot); }
                catch { continue; }
                if (bounds == null) continue;

                bool contains;
                try { contains = (bool)_boundsContainsMethod.Invoke(bounds, containsArgs)!; }
                catch { continue; }
                if (contains) return i;
            }
            return -1;
        }

        // VirtualInputHotspot.vector2_0 (snap point the engine moves the
        // controller cursor to) and VirtualInput.SetCursor(Vector2).
        private const string HotspotCenterField = "#=q3L3_c1bsKCjNb4hkOQx8vg==";
        private const string SetCursorMethod    = "#=qT_SlnKGmQty0k3OLe9Ydng==";
        private static bool        _moveResolved;
        private static FieldInfo?  _hotspotCenterField;
        private static MethodInfo? _setCursor;

        /// <summary>
        /// Move the controller cursor onto hotspot <paramref name="index"/>
        /// of <c>VirtualInput.list_0</c> (negative counts from the end, so
        /// -1 is the last). The engine keeps the cursor on whichever
        /// hotspot contains it, so this is a real focus move; the view's
        /// normal focus-change speech then announces it. Returns false if
        /// there's no such hotspot.
        /// </summary>
        public static bool MoveCursorToHotspot(int index)
        {
            EnsureResolved();
            if (!_moveResolved)
            {
                _moveResolved = true;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                _hotspotCenterField = Nagoya.FindType("VirtualInputHotspot")?.GetField(HotspotCenterField, any | BindingFlags.Instance);
                Type? tVec = Nagoya.FindType("Vector2");
                if (tVec != null)
                    _setCursor = Nagoya.FindType("VirtualInput")?.GetMethod(SetCursorMethod, any | BindingFlags.Static, null, new[] { tVec }, null);
            }
            if (_list0Field == null || _hotspotCenterField == null || _setCursor == null) return false;
            try
            {
                if (!(_list0Field.GetValue(null) is IList list) || list.Count == 0) return false;
                if (index < 0) index += list.Count;
                if (index < 0 || index >= list.Count) return false;
                object? center = _hotspotCenterField.GetValue(list[index]);
                if (center == null) return false;
                _setCursor.Invoke(null, new[] { center });
                return true;
            }
            catch { return false; }
        }

        // PhoneScreen.vector2_2 — the phone's on-screen size.
        private const string PhoneSizeField = "#=q2N_S4MJ2CIwFAxov_uuTdA==";
        private static bool          _phoneSizeResolved;
        private static object?       _phoneSize;
        private static PropertyInfo? _boundsSizeProp;

        /// <summary>
        /// True iff the cursor is on the inline phone's own hotspot. In
        /// Transparency Mode the open Mail/Chat app registers one hotspot
        /// covering the whole phone (Bounds2.WithSize(origin,
        /// PhoneScreen.vector2_2)), and only scrolls while the cursor is
        /// inside it; the question buttons are separate hotspots.
        /// </summary>
        public static bool IsCursorOnPhoneHotspot()
        {
            int idx = FindHotspotIndex();
            if (idx < 0 || _list0Field == null || _hotspotBoundsField == null) return false;
            if (!_phoneSizeResolved)
            {
                _phoneSizeResolved = true;
                try
                {
                    _phoneSize = Nagoya.FindType("PhoneScreen")?.GetField(PhoneSizeField,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                }
                catch { }
            }
            if (_phoneSize == null) return false;
            try
            {
                if (!(_list0Field.GetValue(null) is IList list) || idx >= list.Count) return false;
                object? bounds = _hotspotBoundsField.GetValue(list[idx]);
                if (bounds == null) return false;
                _boundsSizeProp ??= bounds.GetType().GetProperty("Size", BindingFlags.Public | BindingFlags.Instance);
                return Equals(_boundsSizeProp?.GetValue(bounds), _phoneSize);
            }
            catch { return false; }
        }
    }
}
