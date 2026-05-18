using System;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Honor <see cref="InputSuppressor"/>'s cursor-lock by re-snapping the
    /// engine cursor to the locked position as a postfix on
    /// <c>VirtualInput.smethod_9</c> (per-frame input update).
    ///
    /// Without this, axis suppression alone is not enough to keep the
    /// cursor stable on the look-around screen. <c>smethod_9</c>'s
    /// controller-mode body unconditionally snaps the cursor every
    /// frame using two paths:
    ///
    ///   1. <c>list_0.Where(h =&gt; h.bounds2_0.Contains(smethod_4()))
    ///      .FirstOrDefault()</c> — pick the hotspot whose bounds
    ///      currently contain the cursor. If found and axes are zero
    ///      (suppressed), snap to that hotspot's <c>vector2_0</c>.
    ///
    ///   2. If no hotspot contains the cursor, fall back to
    ///      <c>list_0.First()</c> and snap to its <c>vector2_0</c>.
    ///
    /// Any one-frame drift off the focused entry's 20×20 bounds —
    /// from real mouse jitter triggering a mouse-mode flip, or from
    /// the lock not being asserted yet — drops the engine into case
    /// (2) and the cursor is permanently relocated to the first
    /// hotspot until something explicit moves it. Confirm-clicks
    /// would then activate the wrong entry.
    ///
    /// Solution: a postfix on <c>smethod_9</c> that runs after the
    /// engine's snap logic and calls <c>SetCursor(lock)</c>, restoring
    /// our intended position before the frame renders.
    ///
    /// Target: <c>VirtualInput.smethod_9(float)</c> — public static
    /// void, one float param. Found by signature; the runtime name is
    /// obfuscated and we don't know it offhand, but the signature is
    /// unique on VirtualInput.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorSnapLock
    {
        // Same name SolitaireView / ExplorationSubView use for the
        // cursor setter (smethod_12 in the clean decompile).
        private const string SetCursorMethodName = "#=qT_SlnKGmQty0k3OLe9Ydng==";

        private static MethodInfo? _setCursor;
        private static Type?       _tVector2;
        private static bool        _resolveAttempted;

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return null;

            // Signature: public static void Method(float). There's only
            // one such method on VirtualInput in the clean decompile
            // (smethod_9 — the per-frame update loop).
            MethodInfo? found = null;
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType != typeof(void)) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length != 1 || p[0].ParameterType != typeof(float)) continue;
                if (found != null) return null; // ambiguous — bail rather than pick the wrong one
                found = m;
            }
            return found;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!InputSuppressor.TryGetCursorLock(out float x, out float y)) return;
            if (!EnsureResolved()) return;
            try
            {
                object pos = Activator.CreateInstance(_tVector2!, new object[] { x, y })!;
                _setCursor!.Invoke(null, new[] { pos });
            }
            catch
            {
                // Per-frame call site — swallow rather than spam the log.
                // If SetCursor reliably throws, the cursor will still
                // be wherever the engine put it, which is the same
                // degraded state we'd have without this patch.
            }
        }

        private static bool EnsureResolved()
        {
            if (_setCursor != null && _tVector2 != null) return true;
            if (_resolveAttempted) return _setCursor != null && _tVector2 != null;
            _resolveAttempted = true;
            try
            {
                Type? tVI = Nagoya.FindType("VirtualInput");
                _tVector2 = Nagoya.FindType("Vector2");
                if (tVI == null || _tVector2 == null) return false;
                _setCursor = tVI.GetMethod(SetCursorMethodName,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { _tVector2 }, null);
            }
            catch { }
            return _setCursor != null && _tVector2 != null;
        }
    }
}
