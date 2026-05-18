using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Diagnostic-only: logs every call to VirtualInput.smethod_10(Key) —
    /// the key injector that the per-frame binding loop fires for each
    /// rising-edge D-pad direction with a registered binding.
    ///
    /// Used to diagnose why Settings sliders don't respond to keyboard
    /// LEFT/RIGHT via the controller-emulation path. If MINUS/PLUS appears
    /// here when the user is on a slider, the binding loop is firing and
    /// the problem is downstream (slider's GClass5.smethod_12 check). If
    /// MINUS/PLUS does NOT appear, the problem is upstream (AxisInjection,
    /// rising-edge detection, or dictionary_0 binding).
    ///
    /// REMOVE once the bug is understood.
    /// </summary>
    [HarmonyPatch]
    internal static class KeyInjectDiag
    {
        // smethod_10(Key) — runtime name from the obfuscated decompile.
        // Adds key to gclass110.hashSet_0 (game state) + local hashSet_0
        // (mod buffer that gets removed next frame via ExceptWith).
        private const string KeyInjectorName = "#=qFUYVuIZ0M2$Wp19bOV5LwQ==";

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return null;
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name == KeyInjectorName) return m;
            }
            return null;
        }

        public static string? LogPath;

        [HarmonyPostfix]
        private static void Postfix(object __0)
        {
            if (LogPath == null) return;
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [KeyInjectDiag] smethod_10({__0})\r\n");
            }
            catch { }
        }
    }
}
