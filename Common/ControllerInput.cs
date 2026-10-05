namespace ElizaAccess.Common
{
    /// <summary>D-pad / left-stick direction, for matching a real
    /// controller against an arrow key.</summary>
    internal enum PadDirection { None, Up, Down, Left, Right }

    /// <summary>
    /// Latest raw state of any connected real controller, as the game
    /// itself read it this frame.
    ///
    /// Filled by the <see cref="Patches.KeyboardToController"/> postfixes,
    /// which see the engine's own SDL reads (<c>VirtualInput.smethod_19</c>
    /// for axes — left stick + D-pad combined — and <c>smethod_18</c> for
    /// buttons) BEFORE our keyboard injection or any
    /// <see cref="InputSuppressor"/> masking is applied. So a view that
    /// has suppressed an axis to drive navigation itself can still see
    /// what the controller is doing, exactly as it reads the arrow keys
    /// via <see cref="KeyEdge"/>.
    ///
    /// Written on the game thread every frame (the engine's per-frame
    /// input update polls every button, and buttons 0–3 read both axes),
    /// read from the mod's poll thread. Single-word volatile fields, so
    /// no lock needed.
    /// </summary>
    internal static class ControllerInput
    {
        // Stick deflection past this counts as a direction press. The
        // engine has already applied its own deadzone, and the D-pad
        // reports exactly ±1.
        private const float DirectionThreshold = 0.5f;

        private static volatile float _horizontal;
        private static volatile float _vertical;
        // Bit n set ⇔ GEnum22 button n held. Only buttons ≥ 4 are
        // recorded: 0–3 are the D-pad, which smethod_18 derives from the
        // (keyboard-injected) axes — directions come from the raw axes.
        private static volatile int _buttons;

        internal static void RecordAxis(int axis, float value)
        {
            if (axis == 0) _horizontal = value;
            else if (axis == 1) _vertical = value;
        }

        internal static void RecordButton(int button, bool held)
        {
            if (button < 4 || button > 30) return;
            int bit = 1 << button;
            int cur = _buttons;
            _buttons = held ? (cur | bit) : (cur & ~bit);
        }

        /// <summary>True iff the controller is pushed in this direction.
        /// Diagonal stick input resolves to the dominant axis so one push
        /// never fires two directions.</summary>
        public static bool IsDirectionHeld(PadDirection dir)
        {
            float h = _horizontal, v = _vertical;
            bool vertical = System.Math.Abs(v) >= System.Math.Abs(h);
            switch (dir)
            {
                case PadDirection.Up:    return  vertical && v <= -DirectionThreshold;
                case PadDirection.Down:  return  vertical && v >=  DirectionThreshold;
                case PadDirection.Left:  return !vertical && h <= -DirectionThreshold;
                case PadDirection.Right: return !vertical && h >=  DirectionThreshold;
                default: return false;
            }
        }

        /// <summary>Bitmask of everything currently held: bits 0–3 are
        /// Up/Down/Left/Right, higher bits are GEnum22 buttons. Used by
        /// <see cref="InputInterrupter"/> to spot any new press.</summary>
        public static int HeldMask()
        {
            int mask = _buttons;
            if (IsDirectionHeld(PadDirection.Up))    mask |= 1 << 0;
            if (IsDirectionHeld(PadDirection.Down))  mask |= 1 << 1;
            if (IsDirectionHeld(PadDirection.Left))  mask |= 1 << 2;
            if (IsDirectionHeld(PadDirection.Right)) mask |= 1 << 3;
            return mask;
        }

        /// <summary>The controller direction an arrow key stands for, or
        /// <see cref="PadDirection.None"/> for any other key.</summary>
        public static PadDirection DirectionForKey(int vk)
        {
            switch (vk)
            {
                case Keyboard.VK_UP:    return PadDirection.Up;
                case Keyboard.VK_DOWN:  return PadDirection.Down;
                case Keyboard.VK_LEFT:  return PadDirection.Left;
                case Keyboard.VK_RIGHT: return PadDirection.Right;
                default: return PadDirection.None;
            }
        }
    }
}
