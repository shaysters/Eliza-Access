using System.Runtime.InteropServices;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Per-tick cached keyboard state. <see cref="Refresh"/> is called
    /// exactly once at the top of each Main poll iteration and reads
    /// <c>GetAsyncKeyState</c> for every virtual key into a 256-slot
    /// array. <see cref="InputInterrupter"/> and <see cref="KeyEdge"/>
    /// then consume the cached snapshot.
    ///
    /// Why: <c>GetAsyncKeyState</c>'s low bit (0x0001 — "key was pressed
    /// since the last call for this VK") is consumed by EACH call.
    /// Before this cache existed, every reader (interrupter sweep,
    /// each KeyEdge.Pressed) called the API independently, and the
    /// first caller's read swallowed the rising-edge bit. For a
    /// sub-poll-interval tap that already released by the time the
    /// later readers got there, the later readers saw neither the
    /// "currently held" bit nor the "recently pressed" bit — the press
    /// was silently dropped, manifesting as "sometimes the Q hotkey
    /// doesn't fire when I press it" / arrow taps eaten in fast nav.
    ///
    /// With this cache, the bit is consumed exactly once per tick by
    /// the <see cref="Refresh"/> call and all subsequent readers see
    /// the same answer.
    ///
    /// Focus gate: <see cref="Refresh"/> short-circuits when the game
    /// window isn't foreground, leaving the snapshot at whatever the
    /// last focused tick observed. Without this, an unfocused tick
    /// would write zeros into the snapshot for keys the user is
    /// actually still physically holding, and the next focused tick
    /// would re-detect them as rising edges — firing spurious Stops
    /// and re-triggering held hotkeys.
    /// </summary>
    internal static class KeyboardSnapshot
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static readonly short[] _state = new short[256];

        /// <summary>Read every VK once into the cache. Call once per
        /// Main poll iteration, before any consumer. No-op when the
        /// game window isn't focused (snapshot is preserved).</summary>
        public static void Refresh()
        {
            if (!Keyboard.IsGameFocused()) return;
            // VK 0-7 are mouse buttons / unused; we don't need them.
            // 0xFF is reserved. Sweep the meaningful range.
            for (int vk = 0x08; vk < 0xFF; vk++)
                _state[vk] = GetAsyncKeyState(vk);
        }

        /// <summary>Raw GetAsyncKeyState return for this VK as of the
        /// most recent <see cref="Refresh"/>. Bit 0x8000 = currently
        /// held; bit 0x0001 = was pressed since the previous Refresh.</summary>
        public static short Get(int vk)
        {
            if ((uint)vk >= 256) return 0;
            return _state[vk];
        }

        /// <summary>True iff the VK was held at the most recent
        /// <see cref="Refresh"/>.</summary>
        public static bool IsHeld(int vk)
        {
            if ((uint)vk >= 256) return false;
            return (_state[vk] & 0x8000) != 0;
        }
    }
}
