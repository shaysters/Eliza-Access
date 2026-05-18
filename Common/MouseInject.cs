using System;
using System.Collections.Generic;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Single-source binding for the deep mouse-button injection chain:
    ///   GameLogic.singleton → InputState → MouseStatePc → HashSet&lt;MouseButton&gt; Held/PrevHeld
    ///
    /// Title and Escape both need this path to inject a synthetic left-click
    /// that bypasses the JustPressed gating in <c>VirtualInput</c>. Each had
    /// its own ~75-line FindDirectMouseInjectTargets() + TryInjectMouseHeld()
    /// helper pair. This consolidates both.
    ///
    /// Chapter uses a different injection path — <c>VirtualInput.SetCursor()</c>
    /// followed by the public <c>VirtualInput.Inject(MouseButton)</c> — and is
    /// not covered here.
    ///
    /// To press: add <c>MouseButton.Left</c> to <c>Held</c> and remove from
    /// <c>PrevHeld</c> (so the game's JustPressed check sees the transition).
    /// To release: remove from <c>Held</c>.
    /// </summary>
    internal static class MouseInject
    {
        private static FieldInfo? _gameLogicSingletonField;
        private static FieldInfo? _inputStatePcField;
        private static FieldInfo? _inputPcMouseStateField;
        private static FieldInfo? _mouseHeldField;
        private static FieldInfo? _mousePrevHeldField;
        private static MethodInfo? _hashSetAdd;
        private static MethodInfo? _hashSetRemove;
        private static MethodInfo? _hashSetContains;
        private static object? _mouseButtonLeft;

        private static bool _initTried;
        private static bool _ready;
        private static readonly object _gate = new object();

        /// <summary>True iff the full injection chain has been resolved.</summary>
        public static bool IsReady => _ready;

        /// <summary>The resolved MouseButton.Left enum value, or null if not ready.</summary>
        public static object? MouseButtonLeft => _mouseButtonLeft;

        /// <summary>
        /// Idempotent resolution of every reflection handle the inject chain
        /// needs. Returns <see cref="IsReady"/> after attempting.
        /// </summary>
        public static bool TryInit()
        {
            if (_initTried) return _ready;
            lock (_gate)
            {
                if (_initTried) return _ready;
                _ready = Resolve();
                _initTried = true;
                return _ready;
            }
        }

        /// <summary>
        /// Mark the left mouse button as held: adds it to the Held set and
        /// removes it from PrevHeld so the game's "just pressed this frame"
        /// check fires.
        /// </summary>
        public static bool PressLeft()
        {
            if (!TryInit()) return false;
            try
            {
                object? gl = _gameLogicSingletonField!.GetValue(null);
                if (gl == null) return false;
                object? inputPc = _inputStatePcField!.GetValue(gl);
                if (inputPc == null) return false;
                object? ms = _inputPcMouseStateField!.GetValue(inputPc);
                if (ms == null) return false;
                object? held = _mouseHeldField!.GetValue(ms);
                if (held == null) return false;

                _hashSetAdd!.Invoke(held, new[] { _mouseButtonLeft });
                if (_mousePrevHeldField != null)
                {
                    object? prevHeld = _mousePrevHeldField.GetValue(ms);
                    if (prevHeld != null)
                        _hashSetRemove!.Invoke(prevHeld, new[] { _mouseButtonLeft });
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Remove the left mouse button from the Held set (release).
        /// </summary>
        public static bool ReleaseLeft()
        {
            if (!TryInit()) return false;
            try
            {
                object? gl = _gameLogicSingletonField!.GetValue(null);
                if (gl == null) return false;
                object? inputPc = _inputStatePcField!.GetValue(gl);
                if (inputPc == null) return false;
                object? ms = _inputPcMouseStateField!.GetValue(inputPc);
                if (ms == null) return false;
                object? held = _mouseHeldField!.GetValue(ms);
                if (held == null) return false;

                _hashSetRemove!.Invoke(held, new[] { _mouseButtonLeft });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Diagnostic: read whether MouseButton.Left is currently in the Held
        /// set. Returns false if the chain isn't ready or on any failure.
        /// </summary>
        public static bool IsLeftHeld()
        {
            if (!TryInit() || _hashSetContains == null) return false;
            try
            {
                object? gl = _gameLogicSingletonField!.GetValue(null);
                if (gl == null) return false;
                object? inputPc = _inputStatePcField!.GetValue(gl);
                if (inputPc == null) return false;
                object? ms = _inputPcMouseStateField!.GetValue(inputPc);
                if (ms == null) return false;
                object? held = _mouseHeldField!.GetValue(ms);
                if (held == null) return false;
                return (bool)_hashSetContains.Invoke(held, new[] { _mouseButtonLeft })!;
            }
            catch { return false; }
        }

        // ── Resolution ─────────────────────────────────────────────────────

        private static bool Resolve()
        {
            Type? tGameLogic = Nagoya.FindType("GameLogic");
            if (tGameLogic == null) return false;

            _gameLogicSingletonField = tGameLogic.GetField(
                "#=qdf_jbonLPSi33E8HSVNUxQ==", BindingFlags.Public | BindingFlags.Static);
            // Fallback: a public static field on GameLogic whose type is GameLogic itself.
            if (_gameLogicSingletonField == null)
            {
                foreach (FieldInfo f in tGameLogic.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType == tGameLogic) { _gameLogicSingletonField = f; break; }
                }
            }
            if (_gameLogicSingletonField == null) return false;

            _inputStatePcField = tGameLogic.GetField(
                "#=qf6BrebAAA9YjisjqQc7fysXu7TMZu24B85quJVU6Ydw=",
                BindingFlags.Public | BindingFlags.Instance);
            if (_inputStatePcField == null) return false;

            Type tInputState = _inputStatePcField.FieldType;
            _inputPcMouseStateField = tInputState.GetField(
                "#=qME7OCa1eZpa3gEuQvVI$gg==", BindingFlags.Public | BindingFlags.Instance);
            if (_inputPcMouseStateField == null) return false;

            Type tMouseState = _inputPcMouseStateField.FieldType;
            _mouseHeldField = tMouseState.GetField(
                "#=qAATWroPPnIB8_GqCkK8a2Ihq7al0QWf$194bfUxpGjg=",
                BindingFlags.Public | BindingFlags.Instance);
            _mousePrevHeldField = tMouseState.GetField(
                "#=q$ip1cdZgxyG0zMf9dSrxDdKNlu559s9vQdNwupcruVk=",
                BindingFlags.Public | BindingFlags.Instance);
            if (_mouseHeldField == null || _mousePrevHeldField == null)
            {
                // Fallback: signature scan for HashSet<enum> instance fields on
                // mouseState. There are exactly two: Held and PrevHeld, in that
                // declared order in every observed build.
                var setFields = new List<FieldInfo>();
                foreach (FieldInfo f in tMouseState.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.IsGenericType &&
                        f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
                        f.FieldType.GetGenericArguments()[0].IsEnum)
                        setFields.Add(f);
                }
                if (setFields.Count >= 2)
                {
                    if (_mouseHeldField     == null) _mouseHeldField     = setFields[0];
                    if (_mousePrevHeldField == null) _mousePrevHeldField = setFields[1];
                }
            }
            if (_mouseHeldField == null) return false;

            Type tHeld = _mouseHeldField.FieldType;
            _hashSetAdd      = tHeld.GetMethod("Add",      BindingFlags.Public | BindingFlags.Instance);
            _hashSetRemove   = tHeld.GetMethod("Remove",   BindingFlags.Public | BindingFlags.Instance);
            _hashSetContains = tHeld.GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance);
            if (_hashSetAdd == null || _hashSetRemove == null) return false;

            if (!tHeld.IsGenericType) return false;
            Type tMouseButton = tHeld.GetGenericArguments()[0];
            _mouseButtonLeft = Enum.ToObject(tMouseButton, 1);
            return _mouseButtonLeft != null;
        }
    }
}
