using System;
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
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly int _myPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>True iff the foreground window belongs to the game
        /// process. Used as a focus gate on every keyboard read so that
        /// keys pressed in other apps after alt-tabbing out don't
        /// inject into the game.
        ///
        /// <c>GetAsyncKeyState</c> reads hardware state regardless of
        /// which window has focus — without this gate, pressing Enter
        /// in a Notepad window would fire the in-game confirm action.
        ///
        /// Fails CLOSED (returns false) on error — better to drop a
        /// stray keystroke than to inject one when we can't be sure
        /// the game is focused. This is the opposite trade-off from
        /// SpeechEngine's identical check, where missing speech is
        /// worse than speaking when the user might be away.</summary>
        public static bool IsGameFocused()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return (int)pid == _myPid;
            }
            catch { return false; }
        }

        /// <summary>True iff the given virtual key is currently held
        /// down AND the game window has focus.</summary>
        public static bool IsHeld(int vk)
        {
            if (!IsGameFocused()) return false;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>Raw <c>GetAsyncKeyState</c> return — bit 0x8000 means
        /// "currently held", bit 0x0001 means "was pressed since the last
        /// call to <c>GetAsyncKeyState</c> for this key." Used by
        /// <see cref="KeyEdge"/> to catch presses that happen entirely
        /// between two polls — at 200 Hz polling, a fast tap can be
        /// shorter than 5 ms and would otherwise be missed by an
        /// IsHeld-only check.
        ///
        /// Returns 0 when the game is not foregrounded — KeyEdge then
        /// sees no held bit, no recently-pressed bit, and treats the
        /// key as inactive. Without this, alt-tabbing to another app
        /// and pressing keys there would still drive in-game navigation.
        ///
        /// MSDN warns the "recently pressed" bit can be consumed by
        /// other processes that also call <c>GetAsyncKeyState</c> for
        /// the same key — so this is best-effort, not guaranteed. In
        /// practice the mod is the only thing in-process polling these
        /// virtual keys at this rate, so it works.</summary>
        public static short GetRawState(int vk)
        {
            if (!IsGameFocused()) return 0;
            return GetAsyncKeyState(vk);
        }

        // ── Virtual key codes used by observers ─────────────────────────────
        // (Avoids each file redeclaring the same constants.)
        public const int VK_BACK   = 0x08;
        public const int VK_TAB    = 0x09;
        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SPACE  = 0x20;
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
        public const int VK_J      = 0x4A;
        public const int VK_K      = 0x4B;
        public const int VK_L      = 0x4C;
        public const int VK_Q      = 0x51;
        public const int VK_R      = 0x52;
        public const int VK_F5     = 0x74;
        public const int VK_F7     = 0x76;
        public const int VK_F8     = 0x77;
        public const int VK_F9     = 0x78;
        public const int VK_F10    = 0x79;
        public const int VK_F11    = 0x7A;
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

        // Global gate flipped on by modal overlays (e.g.
        // ModSettingsMenu) so every view's own KeyEdges quietly
        // return false instead of running their navigation while the
        // overlay is up. The overlay sets BypassGlobalSuppress=true
        // on its OWN KeyEdges so they keep working. Held-state still
        // updates underneath — when the gate releases, no stale
        // "rising edge" from a key the user happened to be holding
        // during the overlay falsely fires.
        private static bool _globalSuppress;
        public static void SetGlobalSuppress(bool on) { _globalSuppress = on; }

        /// <summary>When true, this edge ignores
        /// <c>SetGlobalSuppress</c> — used by the overlay that
        /// switched suppression on so its own keys still drive its
        /// navigation.</summary>
        public bool BypassGlobalSuppress { get; set; }

        // Arrow keys also fire on the matching controller direction, so
        // every view that drives its own arrow navigation (look-around,
        // Eliza profile grid, Solitaire, History) works on a controller
        // with no per-view code.
        private readonly PadDirection _pad;
        // Optional right-stick direction or controller button that also
        // fires this key, for mod-only keys a controller otherwise
        // couldn't reach.
        private readonly PadDirection _rightStick;
        private readonly PadButton _button;

        public KeyEdge(int vk) : this(vk, PadDirection.None, PadButton.None) { }

        public KeyEdge(int vk, PadDirection rightStick) : this(vk, rightStick, PadButton.None) { }

        public KeyEdge(int vk, PadButton button) : this(vk, PadDirection.None, button) { }

        private KeyEdge(int vk, PadDirection rightStick, PadButton button)
        {
            _vk  = vk;
            _pad = ControllerInput.DirectionForKey(vk);
            _rightStick = rightStick;
            _button = button;
        }

        private bool PadHeld => ControllerInput.IsDirectionHeld(_pad)
                             || ControllerInput.IsRightStickHeld(_rightStick)
                             || ControllerInput.IsButtonHeld(_button);

        /// <summary>True iff the key (or its controller direction) is
        /// currently held.</summary>
        public bool IsHeld => Keyboard.IsHeld(_vk) || PadHeld;

        /// <summary>
        /// True iff the key was held at the previous call to <see cref="Pressed"/>.
        /// Useful for falling-edge detection without consuming the rising edge:
        /// <c>bool released = wasHeldPrev &amp;&amp; !key.IsHeld;</c>
        /// </summary>
        public bool WasHeldPrev => _wasHeld;

        /// <summary>
        /// Returns true on the poll where the key transitions from not-held
        /// to held. Updates internal state — call once per poll iteration.
        ///
        /// Also fires when the key was pressed and released entirely
        /// between this poll and the previous one (Windows reports
        /// "key was pressed since last call" via the low bit of
        /// <c>GetAsyncKeyState</c>). Without this, a fast tap shorter
        /// than the 5 ms poll interval is silently dropped — which
        /// shows up to the user as a navigation key that didn't take.
        /// Counterintuitively, fast navigation gets WORSE if this
        /// isn't handled, because a "down then up" tap pair where one
        /// of the two ends in the gap between polls registers as a
        /// single press in the opposite direction.
        /// </summary>
        public bool Pressed()
        {
            // Skip when unfocused. The snapshot is preserved across
            // focus blips so _wasHeld stays in sync; returning here
            // just suppresses the comparison until focus comes back.
            if (!Keyboard.IsGameFocused()) return false;
            // Read from the per-tick snapshot (KeyboardSnapshot.Refresh
            // is called once at the top of Main's poll iteration) so
            // multiple readers in the same tick see the same 0x0001
            // "pressed since last call" bit — without the cache, the
            // first GetAsyncKeyState caller consumed it and any later
            // KeyEdge silently missed the tap.
            short state = KeyboardSnapshot.Get(_vk);
            bool nowHeld = (state & 0x8000) != 0 || PadHeld;
            bool pressedSinceLast = (state & 0x0001) != 0;
            // Rising edge if either the key is currently held (and wasn't
            // last poll), or it was pressed since the last call (catches
            // sub-poll-interval taps where currently-held is false but
            // the key fired).
            bool rising = (nowHeld || pressedSinceLast) && !_wasHeld;
            // Only carry forward "currently held" — so a brief tap that
            // already released doesn't block the next rising edge.
            _wasHeld = nowHeld;
            // Honour the global gate AFTER updating _wasHeld so that
            // releasing the gate doesn't fire a spurious edge for a
            // key the user kept held the whole time.
            if (_globalSuppress && !BypassGlobalSuppress) return false;
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
