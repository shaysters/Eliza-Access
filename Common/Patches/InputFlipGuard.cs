using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ElizaAccess.Common.Patches
{
    /// <summary>
    /// Stop <c>VirtualInput.smethod_9</c> from flipping the input mode to
    /// "mouse" on every keypress.
    ///
    /// At line 254 of the per-frame update, the game does:
    /// <code>
    /// if (smethod_0() &amp;&amp; (mouseMoved || gclass110.hashSet_2.Count &gt; 0))
    ///     smethod_1(false);   // flip IsControllerActive to false
    /// </code>
    /// <c>gclass110.hashSet_2</c> is populated by the SDL event pump
    /// whenever a key is pressed or auto-repeated (GameLogic line 1827).
    /// For a sighted player switching from controller to keyboard, this
    /// flip is the intended UX. For our blind, keyboard-only player, the
    /// flip is what produces the cursor jump: mode flips → the next branch
    /// (line 426) snaps cursor to <c>TopLeft</c> → next frame's snap-to-
    /// first-hotspot lands on Continue.
    ///
    /// <see cref="ControllerModeLock.Hold"/> (poll-loop reflection rewrite
    /// of <c>bool_0</c>) is too slow — the cursor-snap to TopLeft already
    /// happened inside <c>smethod_9</c> before our restore runs, and the
    /// next frame snaps to <c>list.First()</c> = Continue regardless of
    /// our restored mode.
    ///
    /// Patch strategy: prefix on <c>smethod_9</c> that clears
    /// <c>gclass110.hashSet_2</c> before the original body reads it. The
    /// flip-trigger condition collapses to false, the flip never happens,
    /// the cursor stays put.
    ///
    /// <c>smethod_9</c> is 200+ lines and won't be inlined, so this prefix
    /// actually fires (unlike <see cref="ControllerModeLock"/>'s prefix on
    /// the one-line setter).
    ///
    /// Side effect: screens that use <c>GClass5.smethod_17</c> (rising-edge
    /// key check) will see hashSet_2 as empty during smethod_9. The screen
    /// render runs AFTER smethod_9 but BEFORE GameLogic's end-of-frame clear,
    /// so those screens still see the original hashSet_2 contents — only
    /// smethod_9 itself is fooled. Verified that the accessibility-critical
    /// screens (Title, Settings, Escape) don't rely on smethod_17 for
    /// anything we care about; Escape uses smethod_12 which reads hashSet_0.
    /// </summary>
    [HarmonyPatch]
    internal static class InputFlipGuard
    {
        // smethod_9(float) — public per-frame input update.
        // Obfuscated name from eliza_decompiled/VirtualInput.cs line 250.
        public const string UpdateMethodName = "#=q0uUQRvV_8kdh$RIvH0qOkQ==";

        [HarmonyTargetMethod]
        internal static MethodBase? Target()
        {
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI == null) return null;
            foreach (MethodInfo m in tVI.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != UpdateMethodName) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(float)) return m;
            }
            return null;
        }

        public static string? LogPath;
        private static int _fireCount;
        private static int _clearCount;

        // Reflection cache for gclass110.hashSet_2 access. Resolved lazily
        // on first prefix call (when GameLogic.gameLogic_0 is guaranteed
        // non-null because we're inside its update path).
        private static bool   _cached;
        private static object? _gclass110;
        private static FieldInfo? _hashSet2Field;

        private static void EnsureCached()
        {
            if (_cached) return;
            _cached = true;
            try
            {
                Type? tGL = Nagoya.FindType("GameLogic");
                if (tGL == null) return;
                FieldInfo? singleton = tGL.GetField("gameLogic_0",
                    BindingFlags.Public | BindingFlags.Static);
                object? gl = singleton?.GetValue(null);
                if (gl == null) return;

                // GameLogic.gclass110_0 - the keyboard/mouse state holder.
                FieldInfo? gclass110Field = null;
                foreach (FieldInfo f in tGL.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.Name.StartsWith("GClass110"))
                    {
                        gclass110Field = f;
                        break;
                    }
                }
                if (gclass110Field == null) return;
                _gclass110 = gclass110Field.GetValue(gl);
                if (_gclass110 == null) return;

                // gclass110.hashSet_2 = HashSet<Key> of keys pressed this frame.
                // Other hashSets: hashSet_0 (held), hashSet_1 (last frame held),
                // hashSet_3/4 (mouse buttons). hashSet_2 is the one smethod_9
                // line 254 reads.
                foreach (FieldInfo f in _gclass110.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.HashSet<>))
                        continue;
                    Type itemType = f.FieldType.GetGenericArguments()[0];
                    if (itemType.Name != "Key") continue;
                    // Three Key-typed HashSet fields exist (hashSet_0/1/2);
                    // hashSet_2 is named identically in obfuscated form.
                    // Identify it by name suffix.
                    if (f.Name.EndsWith("hashSet_2", StringComparison.Ordinal))
                    {
                        _hashSet2Field = f;
                        break;
                    }
                }
                // Name-suffix match may fail under obfuscation. Fall back:
                // hashSet_2 is the THIRD HashSet<Key> field by declaration
                // order in GClass110.cs (after hashSet_0 and hashSet_1).
                if (_hashSet2Field == null)
                {
                    int seen = 0;
                    foreach (FieldInfo f in _gclass110.GetType().GetFields(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!f.FieldType.IsGenericType) continue;
                        if (f.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.HashSet<>))
                            continue;
                        Type itemType = f.FieldType.GetGenericArguments()[0];
                        if (itemType.Name != "Key") continue;
                        if (seen == 2) { _hashSet2Field = f; break; }
                        seen++;
                    }
                }

                if (LogPath != null)
                {
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} [InputFlipGuard] cached: gclass110={_gclass110 != null} hashSet_2Field={_hashSet2Field?.Name ?? "<null>"}\r\n");
                }
            }
            catch (Exception ex)
            {
                if (LogPath != null)
                {
                    try
                    {
                        File.AppendAllText(LogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} [InputFlipGuard] cache FAILED: {ex.GetType().Name}: {ex.Message}\r\n");
                    }
                    catch { }
                }
            }
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            EnsureCached();

            int fired = System.Threading.Interlocked.Increment(ref _fireCount);
            if (fired == 1 && LogPath != null)
            {
                try
                {
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} [InputFlipGuard] prefix first fire — smethod_9 hook is live\r\n");
                }
                catch { }
            }

            if (_hashSet2Field == null || _gclass110 == null) return;
            try
            {
                if (_hashSet2Field.GetValue(_gclass110) is ICollection set && set.Count > 0)
                {
                    // Reflection-call Clear() on the HashSet<Key>.
                    MethodInfo? clear = set.GetType().GetMethod("Clear");
                    clear?.Invoke(set, null);
                    int n = System.Threading.Interlocked.Increment(ref _clearCount);
                    if (n <= 5 || n % 100 == 0)
                    {
                        try
                        {
                            File.AppendAllText(LogPath ?? "",
                                $"{DateTime.Now:HH:mm:ss.fff} [InputFlipGuard] cleared hashSet_2 ({n} clears total)\r\n");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
