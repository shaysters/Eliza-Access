using System;
using System.Collections;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>D-pad / left-stick direction, for matching a real
    /// controller against an arrow key.</summary>
    internal enum PadDirection { None, Up, Down, Left, Right }

    /// <summary>Controller buttons the mod reads straight from SDL. The
    /// values are SDL_GameControllerButton indices.</summary>
    internal enum PadButton { None = -1, A = 0, B = 1, Back = 4 }

    /// <summary>
    /// State of any connected real controller, as of the current poll
    /// tick.
    ///
    /// Left stick + D-pad and buttons are recorded by the
    /// <see cref="Patches.KeyboardToController"/> postfixes, which see the
    /// engine's own SDL reads (<c>VirtualInput.smethod_19</c> for axes,
    /// <c>smethod_18</c> for buttons) on the game thread BEFORE our
    /// keyboard injection or any <see cref="InputSuppressor"/> masking is
    /// applied. So a view that has suppressed an axis to drive navigation
    /// itself can still see what the controller is doing, exactly as it
    /// reads the arrow keys via <see cref="KeyEdge"/>.
    ///
    /// The right stick, which the game never reads (smethod_19 only looks
    /// at SDL axes 0/1), is read straight from <c>SdlController</c> — it's
    /// free for mod controls.
    ///
    /// <see cref="Refresh"/> runs once at the top of each poll tick,
    /// beside <see cref="KeyboardSnapshot.Refresh"/>, and fixes which
    /// directions count as held for the whole tick. Every reader in a tick
    /// then agrees — otherwise a quick flick could change between the
    /// ImageBrowser's read and the InputInterrupter's, and the interrupter
    /// would see a late "press" and cut off what was just spoken.
    /// </summary>
    internal static class ControllerInput
    {
        // A direction becomes held past PressThreshold and stays held until
        // the stick falls back under ReleaseThreshold (near the deadzone),
        // so a stick hovering around the press point doesn't chatter, and
        // returning to centre always counts as a release. The D-pad
        // reports exactly ±1.
        private const float PressThreshold = 0.5f;
        private const float ReleaseThreshold = 0.25f;

        // Raw values, written on the game thread.
        private static volatile float _horizontal;
        private static volatile float _vertical;
        // Bit n set ⇔ GEnum22 button n held. Only buttons ≥ 4 are
        // recorded: 0–3 are the D-pad, which smethod_18 derives from the
        // (keyboard-injected) axes — directions come from the raw axes.
        private static volatile int _buttons;

        // Per-tick snapshot, written and read on the poll thread.
        private static PadDirection _left = PadDirection.None;
        private static PadDirection _right = PadDirection.None;
        private static int _buttonsSnapshot;
        // Bit n set = SDL button n held this tick (A, B, Back only).
        private static int _sdlButtons;

        private static MethodInfo? _getControllers;   // SdlController.GetAllConnectedControllers()
        private static MethodInfo? _getAxisRaw;       // SdlController.GetAxisRaw(axis enum)
        private static MethodInfo? _getButton;        // SdlController.GetButton(button enum)
        private static object? _axisRightX, _axisRightY;
        private static readonly PadButton[] SdlButtonsRead = { PadButton.A, PadButton.B, PadButton.Back };
        private static object?[]? _sdlButtonArgs;
        private static bool _rightStickBroken;

        // Set by HarmonyBootstrap alongside the patches that call in here.
        public static Action<string>? Log;

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

        /// <summary>Fix this tick's controller state. Call once per poll
        /// tick, before any reader.</summary>
        public static void Refresh()
        {
            _left = Resolve(_left, _horizontal, _vertical);
            _buttonsSnapshot = _buttons;
            if (ReadSdl(out float h, out float v, out int sdlButtons))
            {
                _right = Resolve(_right, h, v);
                _sdlButtons = sdlButtons;
            }
        }

        /// <summary>Which direction a stick at (h, v) holds, given what it
        /// held last tick. A held direction persists until its axis drops
        /// under the release threshold; a new one needs the press threshold
        /// on the dominant axis, so a diagonal never fires two.</summary>
        private static PadDirection Resolve(PadDirection was, float h, float v)
        {
            switch (was)
            {
                case PadDirection.Up:    if (v <= -ReleaseThreshold) return was; break;
                case PadDirection.Down:  if (v >=  ReleaseThreshold) return was; break;
                case PadDirection.Left:  if (h <= -ReleaseThreshold) return was; break;
                case PadDirection.Right: if (h >=  ReleaseThreshold) return was; break;
            }
            if (Math.Abs(v) >= Math.Abs(h))
            {
                if (v <= -PressThreshold) return PadDirection.Up;
                if (v >=  PressThreshold) return PadDirection.Down;
            }
            else
            {
                if (h <= -PressThreshold) return PadDirection.Left;
                if (h >=  PressThreshold) return PadDirection.Right;
            }
            return PadDirection.None;
        }

        /// <summary>Sum the right stick over every connected controller
        /// (SDL axes 2 = right X, 3 = right Y, Y positive down), and collect
        /// the SDL buttons in <see cref="SdlButtonsRead"/> held on any of
        /// them. The engine's per-controller state arrays are only written
        /// by its event pump, so reading them from the poll thread is safe;
        /// a controller plugged in mid-read just skips this tick. Any
        /// reflection failure turns these reads off for the session.</summary>
        private static bool ReadSdl(out float h, out float v, out int buttons)
        {
            h = v = 0f;
            buttons = 0;
            if (_rightStickBroken) return false;
            try
            {
                if (_getAxisRaw == null)
                {
                    Type? t = Nagoya.FindType("SdlController");
                    _getControllers = t?.GetMethod("GetAllConnectedControllers",
                        BindingFlags.Public | BindingFlags.Static);
                    _getAxisRaw = t?.GetMethod("GetAxisRaw",
                        BindingFlags.Public | BindingFlags.Instance);
                    _getButton = t?.GetMethod("GetButton",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_getControllers == null || _getAxisRaw == null || _getButton == null)
                    {
                        _rightStickBroken = true;
                        Log?.Invoke("[Controller] SdlController API not found; right stick and controller buttons unavailable");
                        return false;
                    }
                    Type axisType = _getAxisRaw.GetParameters()[0].ParameterType;
                    _axisRightX = Enum.ToObject(axisType, 2);
                    _axisRightY = Enum.ToObject(axisType, 3);
                    Type buttonType = _getButton.GetParameters()[0].ParameterType;
                    _sdlButtonArgs = new object?[SdlButtonsRead.Length];
                    for (int i = 0; i < SdlButtonsRead.Length; i++)
                        _sdlButtonArgs[i] = Enum.ToObject(buttonType, (int)SdlButtonsRead[i]);
                }

                if (_getControllers!.Invoke(null, null) is IEnumerable controllers)
                {
                    foreach (object c in controllers)
                    {
                        h += (int)_getAxisRaw.Invoke(c, new[] { _axisRightX }) / 32767f;
                        v += (int)_getAxisRaw.Invoke(c, new[] { _axisRightY }) / 32767f;
                        for (int i = 0; i < SdlButtonsRead.Length; i++)
                            if ((bool)_getButton!.Invoke(c, new[] { _sdlButtonArgs![i] }))
                                buttons |= 1 << (int)SdlButtonsRead[i];
                    }
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                // Controller list changed under us (hotplug); try next tick.
                return false;
            }
            catch (Exception e)
            {
                _rightStickBroken = true;
                Log?.Invoke($"[Controller] controller read failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>True iff the left stick / D-pad holds this direction
        /// this tick.</summary>
        public static bool IsDirectionHeld(PadDirection dir)
            => dir != PadDirection.None && _left == dir;

        /// <summary>True iff this controller button is held this
        /// tick.</summary>
        public static bool IsButtonHeld(PadButton button)
            => button != PadButton.None && (_sdlButtons & (1 << (int)button)) != 0;

        /// <summary>Same as <see cref="IsDirectionHeld"/> for the right
        /// stick.</summary>
        public static bool IsRightStickHeld(PadDirection dir)
            => dir != PadDirection.None && _right == dir;

        /// <summary>Bitmask of everything held this tick: bits 0–3 are the
        /// left stick's Up/Down/Left/Right, bits 4–30 are GEnum22 buttons,
        /// bits 32–35 are the right stick's directions. Used by
        /// <see cref="InputInterrupter"/> to spot any new press.</summary>
        public static long HeldMask()
        {
            long mask = _buttonsSnapshot;
            if (_left != PadDirection.None)  mask |= 1L << ((int)_left - 1);
            if (_right != PadDirection.None) mask |= 1L << (32 + (int)_right - 1);
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
