using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Reference-counted registry of "the game should not see this input"
    /// rules, honored by the <see cref="Patches.KeyboardToController"/>
    /// Harmony patches.
    ///
    /// Use when a view or sub-view wants to take exclusive control of a
    /// keyboard direction or controller button — typically because the
    /// engine's own response to that input is wrong for the accessibility
    /// experience (e.g. the look-around scene's snap-to-nearest-hotspot
    /// cursor is too erratic to be useful for a blind player, so the
    /// view drives the cursor itself).
    ///
    /// What "suppressed" means in concrete terms:
    /// - <b>Axes</b>: <c>VirtualInput.smethod_19</c> returns 0 for the
    ///   axis. The engine sees no vertical / horizontal input, so its
    ///   own controller-cursor navigation and any axis-driven scroll
    ///   does nothing. Our keyboard injection of that axis is also
    ///   skipped, so the engine truly sees zero.
    /// - <b>Buttons</b>: <c>VirtualInput.smethod_18</c> returns false
    ///   for the button. The engine sees the button as not held, and
    ///   our keyboard injection for that button is also skipped.
    ///
    /// What suppression does NOT do:
    /// - It does not affect <see cref="KeyEdge"/> / <see cref="Keyboard.IsHeld"/>,
    ///   which read OS keyboard state directly. A view can keep
    ///   reading <c>VK_UP</c> for its own navigation even while the
    ///   engine sees the vertical axis as zero.
    /// - It does not stop <see cref="InputInterrupter"/> from
    ///   detecting key edges and cutting in-flight speech. The user's
    ///   intent to act always invalidates pending speech, suppressed
    ///   or not.
    ///
    /// Lifecycle pattern: claim in <c>OnFocus</c> / <c>OnEnter</c>,
    /// release in <c>OnUnfocus</c> / <c>OnLeave</c>. Ref-counting
    /// means two views can hold a claim on the same axis simultaneously
    /// without either accidentally letting the engine see input the
    /// other still wants suppressed.
    /// </summary>
    internal static class InputSuppressor
    {
        // ── Axis indices (matches GEnum5) ──────────────────────────────
        public const int AxisHorizontal = 0;
        public const int AxisVertical   = 1;

        // ── Button indices (matches GEnum22 — same conventions as
        //    KeyboardToController.ButtonInjection) ─────────────────────
        public const int ButtonDpadUp    = 0;
        public const int ButtonDpadDown  = 1;
        public const int ButtonDpadLeft  = 2;
        public const int ButtonDpadRight = 3;
        public const int ButtonConfirmA  = 4;
        public const int ButtonBackB     = 7;

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, int> _axisRefs   = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _buttonRefs = new Dictionary<int, int>();

        // Cursor-lock: when a view has claimed exclusive cursor control,
        // it sets a target position here and the CursorSnapLock patch
        // re-snaps the engine cursor to it as a postfix on
        // VirtualInput.smethod_9 every frame. Why this is needed even
        // with axes suppressed: smethod_9's controller-mode body
        // unconditionally snaps the cursor every frame based on
        // (a) the hotspot whose bounds currently contain the cursor, or
        // (b) the first hotspot in list_0 if cursor isn't in any.
        // Any transient drift off the focused entry's 20×20 bounds —
        // even a single frame of mouse-mode flip from real mouse jitter
        // — falls into case (b) and the engine permanently relocates
        // the cursor to the wrong entry until our SetCursor runs again.
        // The lock makes the postfix re-assert our intent every frame.
        private static bool  _hasCursorLock;
        private static float _cursorLockX;
        private static float _cursorLockY;

        // ── Axis API ──────────────────────────────────────────────────

        public static void SuppressAxis(int axis)
        {
            lock (_lock) { Bump(_axisRefs, axis); }
        }

        public static void ReleaseAxis(int axis)
        {
            lock (_lock) { Drop(_axisRefs, axis); }
        }

        public static bool IsAxisSuppressed(int axis)
        {
            lock (_lock) { return _axisRefs.ContainsKey(axis); }
        }

        // ── Button API ────────────────────────────────────────────────

        public static void SuppressButton(int button)
        {
            lock (_lock) { Bump(_buttonRefs, button); }
        }

        public static void ReleaseButton(int button)
        {
            lock (_lock) { Drop(_buttonRefs, button); }
        }

        public static bool IsButtonSuppressed(int button)
        {
            lock (_lock) { return _buttonRefs.ContainsKey(button); }
        }

        // ── Cursor-lock API ───────────────────────────────────────────

        public static void SetCursorLock(float x, float y)
        {
            lock (_lock)
            {
                _hasCursorLock = true;
                _cursorLockX = x;
                _cursorLockY = y;
            }
        }

        public static void ClearCursorLock()
        {
            lock (_lock) { _hasCursorLock = false; }
        }

        public static bool TryGetCursorLock(out float x, out float y)
        {
            lock (_lock)
            {
                x = _cursorLockX;
                y = _cursorLockY;
                return _hasCursorLock;
            }
        }

        // ── Internals ─────────────────────────────────────────────────

        private static void Bump(Dictionary<int, int> d, int key)
        {
            d.TryGetValue(key, out int n);
            d[key] = n + 1;
        }

        private static void Drop(Dictionary<int, int> d, int key)
        {
            if (!d.TryGetValue(key, out int n)) return;
            if (n <= 1) d.Remove(key);
            else        d[key] = n - 1;
        }
    }
}
