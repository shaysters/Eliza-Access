using System;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Injects keyboard arrow keys + Enter as if they were controller D-pad
    /// directions + A button.
    ///
    /// The game's <c>VirtualInput.smethod_18</c> ("is this controller button
    /// held?") and <c>smethod_19</c> ("what is this axis value?") read SDL
    /// hardware state directly via <c>SdlController.GetAllConnectedControllers</c>.
    /// With no controller plugged in, both return zero/false and the game's
    /// controller-mode focus logic (D-pad nav, A-to-confirm) never fires.
    ///
    /// Patches:
    ///   smethod_19 postfix — axis-value getter. We add keyboard arrow
    ///                        contributions to the controller-derived result:
    ///                          horizontal axis: LEFT contributes -1, RIGHT +1
    ///                          vertical axis:   UP contributes -1, DOWN  +1
    ///                        (sign convention matches smethod_18's switch.)
    ///                        Result is clamped to [-1, +1].
    ///
    ///   smethod_18 postfix — button-held check. For button index 4 (the
    ///                        "confirm / A" button — used by FakeAppScreen,
    ///                        phone apps, etc. as the advance/select action),
    ///                        OR keyboard Enter state into the result.
    ///
    /// Together these make a keyboard-only player look indistinguishable from
    /// a controller player to the game's input pipeline. The game's existing
    /// rising-edge detection (hashSet_2 + smethod_14) handles repeats and
    /// hold-vs-tap semantics for us.
    ///
    /// GEnum22 mapping (inferred from how the game's own code binds it):
    ///   0 = D-pad Up,    1 = D-pad Down
    ///   2 = D-pad Left,  3 = D-pad Right
    ///   4 = Confirm / A  (mapped to Key.SPACE in FakeAppScreen)
    ///   7 = Back / B     (mapped to Key.FAKE_KEY_4 in WorldScreen — NOT
    ///                    Key.ESCAPE. The game checks Key.ESCAPE only when
    ///                    smethod_0 is false (mouse mode); since we force
    ///                    controller mode permanently, the keyboard ESCAPE
    ///                    branch is unreachable and we have to drive
    ///                    button 7 ourselves to open the escape menu.)
    /// GEnum5 mapping:
    ///   0 = horizontal axis,  1 = vertical axis
    /// </summary>
    internal static class KeyboardToController
    {
        // smethod_19(GEnum5) → float, axis value clamped [-1, +1]
        private const string AxisMethodName   = "#=qyP0scakPlG2zN5$Ew4jiyQ==";
        // smethod_18(GEnum22) → bool, is this controller button currently held?
        private const string ButtonMethodName = "#=qm4y1QIFADtUI4bz_xFjAQA==";

        [HarmonyPatch]
        internal static class AxisInjection
        {
            [HarmonyTargetMethod]
            private static MethodBase? Target()
            {
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI == null) return null;
                // Match by name; signature is (enum) -> float, which we trust.
                foreach (MethodInfo m in tVI.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == AxisMethodName) return m;
                }
                return null;
            }

            /// <summary>
            /// __0 is the axis enum (GEnum5). 0 = horizontal, 1 = vertical.
            /// __result is the controller-derived value; we add keyboard
            /// contribution and re-clamp.
            /// </summary>
            [HarmonyPostfix]
            private static void Postfix(object __0, ref float __result)
            {
                int axis;
                try { axis = Convert.ToInt32(__0); }
                catch { return; }

                float kb = 0f;
                if (axis == 0)
                {
                    if (Keyboard.IsHeld(Keyboard.VK_LEFT))  kb -= 1f;
                    if (Keyboard.IsHeld(Keyboard.VK_RIGHT)) kb += 1f;
                }
                else if (axis == 1)
                {
                    if (Keyboard.IsHeld(Keyboard.VK_UP))    kb -= 1f;
                    if (Keyboard.IsHeld(Keyboard.VK_DOWN))  kb += 1f;
                }

                if (kb == 0f) return;
                float combined = __result + kb;
                if      (combined < -1f) combined = -1f;
                else if (combined > +1f) combined = +1f;
                __result = combined;
            }
        }

        [HarmonyPatch]
        internal static class ButtonInjection
        {
            [HarmonyTargetMethod]
            private static MethodBase? Target()
            {
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI == null) return null;
                foreach (MethodInfo m in tVI.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == ButtonMethodName) return m;
                }
                return null;
            }

            /// <summary>
            /// __0 is the button enum (GEnum22).
            ///   btn == 4 (A / confirm) → driven by keyboard Enter.
            ///   btn == 7 (B / back)    → driven by keyboard Escape. The
            ///                            in-game escape-menu trigger in
            ///                            WorldScreen.method_12 only checks
            ///                            Key.ESCAPE when smethod_0 is false
            ///                            (mouse mode); in our forced-
            ///                            controller-mode world it reads
            ///                            Key.FAKE_KEY_4 instead, which the
            ///                            binding loop synthesizes from
            ///                            GEnum22.7 → we have to drive 7.
            ///   btn == 8/9/10 → driven by keyboard 1/2/3. ElizaApp binds
            ///                   these to FAKE_KEY_4/5/6 for the three-tab
            ///                   switch (Profile / Inbox / Sessions —
            ///                   ElizaApp.cs:318-320). The buttons map to
            ///                   non-standard controller positions
            ///                   (probably X/Y/LB or similar) that a
            ///                   keyboard player can't otherwise trigger.
            /// Buttons 0–3 are reachable here too, but the original method
            /// handles those via the axis check (smethod_19) which our
            /// AxisInjection patch already drives — leave them alone to
            /// avoid double-injecting.
            /// </summary>
            [HarmonyPostfix]
            private static void Postfix(object __0, ref bool __result)
            {
                if (__result) return;   // already held from controller
                int btn;
                try { btn = Convert.ToInt32(__0); }
                catch { return; }

                switch (btn)
                {
                    case 4:
                        if (Keyboard.IsHeld(Keyboard.VK_RETURN)) __result = true;
                        break;
                    case 7:
                        if (Keyboard.IsHeld(Keyboard.VK_ESCAPE))
                        {
                            // Skip the injection if the inline phone
                            // (Transparency-Mode path) currently has an
                            // app open. The phone already consumes
                            // Key.ESCAPE directly via PhoneScreen.smethod_1
                            // to close the app — driving button 7 here
                            // would let WorldScreen.method_12 ALSO open
                            // the escape menu on the same keypress.
                            if (!PhoneAppOpenState.IsInlineAppOpen) __result = true;
                        }
                        break;
                    case 8:
                        if (Keyboard.IsHeld(Keyboard.VK_1)) __result = true;
                        break;
                    case 9:
                        if (Keyboard.IsHeld(Keyboard.VK_2)) __result = true;
                        break;
                    case 10:
                        if (Keyboard.IsHeld(Keyboard.VK_3)) __result = true;
                        break;
                }
            }
        }
    }
}
