using System.Runtime.InteropServices;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Wraps Windows <c>GetAsyncKeyState</c> for the mod's keyboard polling.
    /// Replaces seven separate per-observer copies of the DllImport + edge
    /// tracking pair.
    ///
    /// For one-shot detection of a key press transition, instantiate a
    /// <see cref="KeyEdge"/> per key once and call <c>.Pressed()</c> each
    /// poll. For raw "is this key currently held?" queries, use
    /// <see cref="IsHeld(int)"/>.
    ///
    /// Note: <c>GetAsyncKeyState</c> reports the key's *current physical
    /// state* — it is not buffered, so calling code must poll at a high
    /// enough rate not to miss a key tap that happens entirely between
    /// polls. The 50 ms main loop is fast enough in practice.
    /// </summary>
    internal static class Keyboard
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>True iff the given virtual key is currently held down.</summary>
        public static bool IsHeld(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ── Virtual key codes used by observers ─────────────────────────────
        // (Avoids each file redeclaring the same constants.)
        public const int VK_BACK   = 0x08;
        public const int VK_TAB    = 0x09;
        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_PRIOR  = 0x21; // Page Up
        public const int VK_NEXT   = 0x22; // Page Down
        public const int VK_END    = 0x23;
        public const int VK_HOME   = 0x24;
        public const int VK_LEFT   = 0x25;
        public const int VK_UP     = 0x26;
        public const int VK_RIGHT  = 0x27;
        public const int VK_DOWN   = 0x28;
        public const int VK_1      = 0x31;
        public const int VK_2      = 0x32;
        public const int VK_3      = 0x33;
        public const int VK_R      = 0x52;
        public const int VK_F8     = 0x77;
    }

    /// <summary>
    /// Rising-edge detector for a single virtual key. Owns the "was held last
    /// poll" flag so callers don't have to manage the pair of booleans.
    ///
    /// Standard pattern in a poll loop:
    /// <code>
    ///   private readonly KeyEdge _kbUp = new(Keyboard.VK_UP);
    ///   ...
    ///   if (_kbUp.Pressed()) { /* fire once per press */ }
    /// </code>
    /// Call <see cref="Reset"/> on screen exit so that re-entering the screen
    /// with the key already held doesn't immediately fire.
    /// </summary>
    internal sealed class KeyEdge
    {
        private readonly int _vk;
        private bool _wasHeld;

        public KeyEdge(int vk) { _vk = vk; }

        /// <summary>True iff the key is currently held.</summary>
        public bool IsHeld => Keyboard.IsHeld(_vk);

        /// <summary>
        /// True iff the key was held at the previous call to <see cref="Pressed"/>.
        /// Useful for falling-edge detection without consuming the rising edge:
        /// <c>bool released = wasHeldPrev &amp;&amp; !key.IsHeld;</c>
        /// </summary>
        public bool WasHeldPrev => _wasHeld;

        /// <summary>
        /// Returns true on the poll where the key transitions from not-held
        /// to held. Updates internal state — call once per poll iteration.
        /// </summary>
        public bool Pressed()
        {
            bool now = IsHeld;
            bool rising = now && !_wasHeld;
            _wasHeld = now;
            return rising;
        }

        /// <summary>
        /// Forget the last-held state. Use on screen entry/exit so a key
        /// already held when the observer becomes active does not fire
        /// spuriously.
        /// </summary>
        public void Reset() { _wasHeld = false; }
    }
}
