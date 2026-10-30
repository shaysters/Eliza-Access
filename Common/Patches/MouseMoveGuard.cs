using System;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Stops mouse movement from clearing the focused dialogue choice.
    ///
    /// The input helper's <c>KOIsul</c> method (GClass5, the type
    /// <see cref="RawInputGate"/> also patches) is the engine's "pointer
    /// moved this frame" check. The dialogue-choice panel runs
    /// <c>if (pointerMoved) focus = None; else if (Up/Down) …</c> every
    /// frame (WorldScreen.cs:1658, 1688, 2558), so a bumped mouse or
    /// touchpad silently drops the focused option, and a key press on
    /// that same frame is ignored. The next Down then lands on the first
    /// option instead of the next one, and Enter does nothing until the
    /// player presses an arrow again.
    ///
    /// The only other caller (GameLogic.cs:848) uses it to leave
    /// keyboard-navigation mode for the mouse. The player drives the
    /// game with the keyboard or a controller, so we report the pointer
    /// as never moving. Hover and clicks are separate checks and are
    /// untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class MouseMoveGuard
    {
        private const string PointerMovedName = "#=qKOIsulEdBaXB7CuZ2BGRjw==";

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            Type? tG5 = RawInputGate.FindInputHelperType();
            return tG5?.GetMethod(PointerMovedName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
        }

        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            __result = false;
        }
    }
}
