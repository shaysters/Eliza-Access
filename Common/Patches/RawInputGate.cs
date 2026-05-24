using System;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Blocks the engine's raw key / click / action input layer while a
    /// modal mod overlay (the F7 settings menu) is open.
    ///
    /// Every <c>GClass5</c> input query — <c>smethod_11/12</c> (key
    /// held / pressed), <c>smethod_27/28/29</c> (GEnum19 action), and
    /// the <c>smethod_31</c> dialogue-advance helper — short-circuits
    /// to false when <c>GClass5.smethod_1()</c> returns true.
    /// <c>smethod_1</c> normally returns <c>GameLogic.bool_6</c>, the
    /// engine's global input-filter flag.
    ///
    /// We can't just set <c>bool_6</c> from the mod thread: GameLogic's
    /// per-frame update calls <c>GClass5.smethod_6()</c> (GameLogic.cs:879)
    /// which resets it to false immediately before the frame's input
    /// reads, so any async write loses the race. Patching
    /// <c>smethod_1</c> to force-return true while
    /// <see cref="Blocking"/> is set is timing-immune — the per-frame
    /// reset no longer matters because the getter never reflects it.
    ///
    /// This is the layer below <see cref="InputSuppressor"/> (which only
    /// covers the VirtualInput controller surface — buttons + axes).
    /// Dialogue advance reads <c>Key.SPACE</c> / <c>Key.ANY_ENTER</c>
    /// straight through <c>smethod_12</c>, bypassing the controller
    /// layer entirely, which is why suppressing button 4 alone didn't
    /// stop Enter from advancing dialogue under the open menu.
    ///
    /// Target resolution is structural (clean type/field names don't
    /// survive obfuscation at runtime): <c>GClass5</c> is the static
    /// type carrying readonly float fields equal to the 4K screen
    /// dimensions (3840 / 2160 / 2400); <c>smethod_1</c> is its only
    /// private, static, parameterless bool method.
    /// </summary>
    [HarmonyPatch]
    internal static class RawInputGate
    {
        /// <summary>Set true by the overlay while it owns input. Read
        /// on the engine thread inside the patched getter.</summary>
        public static volatile bool Blocking;

        public static Action<string>? Log;

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            Type? tG5 = FindInputHelperType();
            if (tG5 == null) { Log?.Invoke("[RawInputGate] GClass5 (input helper) not found"); return null; }

            MethodInfo? found = null;
            foreach (MethodInfo m in tG5.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType != typeof(bool)) continue;
                if (m.GetParameters().Length != 0) continue;
                if (found != null)
                {
                    // Ambiguous — more than one private static bool() —
                    // bail rather than patch the wrong one.
                    Log?.Invoke($"[RawInputGate] ambiguous: multiple private static bool() on {tG5.Name}");
                    return null;
                }
                found = m;
            }
            Log?.Invoke($"[RawInputGate] target = {tG5.Name}.{found?.Name ?? "<none>"}");
            return found;
        }

        // GClass5: a static type (abstract + sealed in IL) with readonly
        // static float fields holding the engine's reference resolution
        // — 3840 (width), 2160 (height), 2400 (letterbox height). Those
        // three values together are a unique fingerprint.
        private static Type? FindInputHelperType()
        {
            foreach (Type t in Nagoya.Types)
            {
                if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue; // static class
                bool has3840 = false, has2160 = false, has2400 = false;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(float) || !f.IsInitOnly) continue;
                    float v;
                    try { v = (float)f.GetValue(null)!; }
                    catch { continue; }
                    if      (v == 3840f) has3840 = true;
                    else if (v == 2160f) has2160 = true;
                    else if (v == 2400f) has2400 = true;
                }
                if (has3840 && has2160 && has2400) return t;
            }
            return null;
        }

        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (Blocking) __result = true;
        }
    }
}
