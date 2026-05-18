using System;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Force the game's <c>IsControllerActive</c> flag to <c>true</c> permanently.
    ///
    /// Without this, <c>VirtualInput.smethod_9</c> (per-frame update) flips the
    /// flag to false whenever a key is pressed or the mouse moves. That makes
    /// the game's default-focus snap, D-pad navigation, and other controller-
    /// mode behaviours unavailable to a keyboard user — there's no input the
    /// game would interpret as a "controller is active" signal.
    ///
    /// Patch strategy: prefix on the flag's setter rewrites the argument to
    /// always be <c>true</c>. Every call site — including the per-frame mode-
    /// switch logic that would write <c>false</c> — ends up writing <c>true</c>.
    /// The field is also seeded to <c>true</c> at bootstrap time
    /// (see <see cref="HarmonyBootstrap"/>) because the static initializer ran
    /// before our patches were registered.
    ///
    /// Target: <c>VirtualInput.smethod_1(bool)</c> — obfuscated name
    /// <c>#=qSG15X8Id1viyrV3biM_yQA==</c>. Body in the cleaned decompile is
    /// <c>bool_0 = bool_1;</c>.
    /// </summary>
    [HarmonyPatch]
    internal static class ControllerModeLock
    {
        public const string SetterMethodName     = "#=qSG15X8Id1viyrV3biM_yQA==";
        public const string IsActiveFieldName    = "#=qzm799gsV0Db7dGLcGmUPe$qFxrFW40n4IZ3jukcmtn0=";

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            // AccessTools.Method silently returns null for obfuscated names
            // like #=qSG15X8Id1viyrV3biM_yQA== on this runtime — use the same
            // explicit GetMethods iteration that AxisInjection / ButtonInjection
            // use successfully. Without this, the patch registers without error
            // but never hooks, so the game's mouse-motion-detected mode-switch
            // flips the input mode back to mouse the instant the user stops
            // pressing arrow keys.
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return null;
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != SetterMethodName) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(bool)) return m;
            }
            return null;
        }

        /// <summary>
        /// Rewrites the setter's argument to <c>true</c>. NOTE: in practice
        /// this prefix never fires because <c>smethod_1</c> is a one-line
        /// setter (<c>bool_0 = bool_1;</c>) that the JIT inlines into
        /// <c>smethod_9</c>'s callsites — Harmony patches the method entry,
        /// inlined copies bypass it. The real mode-lock is <see cref="Hold"/>
        /// called from the poll loop. This prefix is kept as belt-and-
        /// suspenders for non-inlined callsites (e.g., reflection-invoked).
        /// </summary>
        [HarmonyPrefix]
        private static void Prefix(ref bool __0)
        {
            __0 = true;
        }

        private static FieldInfo? _cachedField;
        private static bool _cacheAttempted;
        private static int _holdFixCount;

        /// <summary>
        /// Per-poll backstop: read <c>IsControllerActive</c> and rewrite to
        /// <c>true</c> if the game flipped it. The Harmony prefix on the
        /// setter doesn't catch flips because the setter is inlined; this
        /// reflection-based rewrite catches the field state regardless of
        /// how it was written. Called from Main's ~200 Hz poll loop, so
        /// any flip is reverted within ~5 ms — too fast for the cursor
        /// read path (smethod_23) to act on the false value visibly.
        /// </summary>
        public static void Hold(string logPath)
        {
            if (!_cacheAttempted)
            {
                _cacheAttempted = true;
                try
                {
                    Type? tVI = Nagoya.FindType("VirtualInput");
                    if (tVI != null)
                        _cachedField = tVI.GetField(IsActiveFieldName,
                            BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch { }
            }
            if (_cachedField == null) return;
            try
            {
                if (_cachedField.GetValue(null) is bool b && !b)
                {
                    _cachedField.SetValue(null, true);
                    int n = System.Threading.Interlocked.Increment(ref _holdFixCount);
                    if (n <= 5 || n % 100 == 0)
                    {
                        try
                        {
                            System.IO.File.AppendAllText(logPath,
                                $"{DateTime.Now:HH:mm:ss.fff} [CtrlModeLock] Hold: detected flip-to-false, restored (fix#{n})\r\n");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Seed the underlying static field to <c>true</c>. Called from
        /// <see cref="HarmonyBootstrap"/> right after PatchAll, because the
        /// game's static initializer for VirtualInput sets the field to
        /// false before Harmony loads, and there's no guarantee the setter
        /// will be called again before the user expects controller-mode
        /// behaviour.
        /// </summary>
        public static bool SeedTrue(Action<string> log)
        {
            try
            {
                Type? tVI = Nagoya.FindType("VirtualInput");
                if (tVI == null)
                {
                    log("[ControllerModeLock] SeedTrue: VirtualInput type not found");
                    return false;
                }
                FieldInfo? field = tVI.GetField(IsActiveFieldName,
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    log("[ControllerModeLock] SeedTrue: IsControllerActive field not found by name");
                    return false;
                }
                field.SetValue(null, true);
                log("[ControllerModeLock] SeedTrue: IsControllerActive set to true");
                return true;
            }
            catch (Exception ex)
            {
                log($"[ControllerModeLock] SeedTrue FAILED: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
