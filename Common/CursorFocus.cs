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
    }
}
