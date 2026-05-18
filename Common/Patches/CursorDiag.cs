using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Diagnostic-only: logs every call to VirtualInput.smethod_12 (the public
    /// cursor setter that writes both vector2_4 and dictionary_1[currentSource]).
    /// Captures the new cursor position + a short call-site stack frame so we
    /// can correlate cursor moves with the smethod_9 logic that triggered them.
    ///
    /// REMOVE this file once the cursor-drift bug is understood.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorDiag
    {
        // smethod_12(Vector2) — runtime name from the obfuscated decompile.
        // Writes vector2_4 then calls smethod_5 to update dictionary_1.
        // This is the canonical "snap the cursor to here" entry point.
        private const string CursorSetterName = "#=qT_SlnKGmQty0k3OLe9Ydng==";

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            // AccessTools.Method silently returns null for obfuscated names
            // like #=qT_SlnKGmQty0k3OLe9Ydng== on this runtime — explicit
            // GetMethods iteration is what the working patches use.
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return null;
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name == CursorSetterName) return m;
            }
            return null;
        }

        public static string? LogPath;
        private static int _fireCount;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (LogPath == null) return;
            int n = System.Threading.Interlocked.Increment(ref _fireCount);
            // Log only every 30th call to avoid drowning the log, but every
            // 30th proves the postfix is firing in steady state.
            if (n == 1 || n % 30 == 0)
            {
                try
                {
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} [CursorDiag] fire#{n}\r\n");
                }
                catch { }
            }
        }
    }
}
