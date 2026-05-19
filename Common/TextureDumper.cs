using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// One-off debug utility: dumps every image-token texture from
    /// <c>GClass127.dictionary_0</c> (the email body image map —
    /// "hedonic", "avalon", "pairup", etc.) to PNG files so the
    /// images can be inspected outside the game.
    ///
    /// Triggered by F9 (handled in <see cref="Main"/>). The actual
    /// readback runs on the render thread (via a piggyback on
    /// <see cref="Patches.HotspotLabelCapture.TextDrawPatch"/>'s
    /// postfix) because the engine's pixel-readback path bottoms
    /// out in GL calls that need the GL context current.
    ///
    /// The dumper uses the engine's own helpers instead of raw GL:
    ///   GClass106.smethod_10(Texture) → GClass22 (pixel buffer)
    ///   GClass22.method_5(string)     → write as PNG via SDL
    /// Both resolved by signature; GClass106 is identified by its
    /// "takes Texture, returns reference type with a byte[] field"
    /// signature, and method_5 by "instance, (string) → void".
    ///
    /// Output: <c>{LogDir}/TextureDumps/{key}.png</c>. The dump
    /// directory is created on demand.
    /// </summary>
    internal static class TextureDumper
    {
        // Set from the keyboard-poll thread; consumed on the render
        // thread. Volatile because the read/write happen on
        // different threads without a lock.
        public static volatile bool DumpRequested;

        public static string? OutputDir;
        public static string? LogPath;

        // ── Cached reflection ─────────────────────────────────────────
        private static bool        _reflected;
        private static FieldInfo?  _dictField;       // GClass127.dictionary_0 (static)
        private static MethodInfo? _getImageData;    // GClass106.smethod_10(Texture) → GClass22
        private static MethodInfo? _saveAsPng;       // GClass22.method_5(string)
        private static MethodInfo? _flipY;           // GClass22.method_2() — in-place vertical flip

        /// <summary>Render-thread entry point. Cheap fast-path when
        /// no dump is pending: just checks the volatile flag.</summary>
        public static void DumpFromRenderThread()
        {
            if (!DumpRequested) return;
            DumpRequested = false;

            try
            {
                EnsureReflected();
                if (_dictField == null || _getImageData == null || _saveAsPng == null)
                {
                    Log("[TextureDump] reflection incomplete — aborting");
                    return;
                }

                string dir = OutputDir ?? Path.Combine(
                    Path.GetDirectoryName(LogPath ?? ".") ?? ".", "TextureDumps");
                Directory.CreateDirectory(dir);

                object? dictObj = _dictField.GetValue(null);
                if (!(dictObj is IDictionary dict))
                {
                    Log("[TextureDump] dictionary field returned non-IDictionary");
                    return;
                }

                int ok = 0, fail = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    string key = (kv.Key as string) ?? "unknown";
                    if (kv.Value == null) { fail++; continue; }
                    if (TrySaveTexture(key, kv.Value, dir)) ok++;
                    else                                    fail++;
                }
                Log($"[TextureDump] {ok} saved / {fail} failed → {dir}");
            }
            catch (Exception ex)
            {
                Log($"[TextureDump] error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TrySaveTexture(string key, object texture, string outDir)
        {
            string path = Path.Combine(outDir, $"{key}.png");
            try
            {
                object imageData = _getImageData!.Invoke(null, new[] { texture })!;
                // GL textures are stored bottom-up in memory; the
                // engine's PNG save writes the raw byte order, so
                // without an explicit flip the output is upside-
                // down. method_2 does an in-place vertical flip.
                _flipY?.Invoke(imageData, null);
                _saveAsPng!.Invoke(imageData, new object[] { path });
                return true;
            }
            catch (TargetInvocationException tie)
            {
                Exception? inner = tie.InnerException;
                Log($"[TextureDump] {key}: {inner?.GetType().Name ?? "unknown"}: {inner?.Message ?? "no message"}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[TextureDump] {key}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void EnsureReflected()
        {
            if (_reflected) return;
            _reflected = true;

            Type? tTexture = Nagoya.FindType("Texture");
            if (tTexture == null) { Log("[TextureDump] Texture type not found"); return; }

            // ── GClass106.smethod_10(Texture) → GClass22 ──────────────
            // Signature: a public static method taking exactly one
            // Texture parameter and returning a reference type that
            // exposes a public byte[] field (the pixel buffer).
            // GClass106 has several other Texture-taking methods but
            // only smethod_10 returns a buffer-bearing class.
            Type? tImageData = null;
            foreach (Type t in Nagoya.Types)
            {
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (MethodInfo m in methods)
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType != tTexture) continue;
                    Type rt = m.ReturnType;
                    if (rt == typeof(void) || rt.IsValueType || rt.IsPrimitive) continue;
                    bool hasByteArray = false;
                    foreach (FieldInfo f in rt.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (f.FieldType == typeof(byte[])) { hasByteArray = true; break; }
                    }
                    if (!hasByteArray) continue;
                    _getImageData = m;
                    tImageData    = rt;
                    Log($"[TextureDump] resolved image-data fn: {t.Name}.{m.Name} → {rt.Name}");
                    break;
                }
                if (_getImageData != null) break;
            }
            if (_getImageData == null || tImageData == null)
            {
                Log("[TextureDump] no static (Texture) → buffer-class method found");
                return;
            }

            // ── GClass22.method_5(string) — save as PNG ───────────────
            // The image-data class has both a PNG save (method_5,
            // takes string) and a JPG save (method_6, takes string +
            // int). Distinguish by parameter count.
            //
            // Also pick up method_2 — the no-arg void instance
            // method that flips the image vertically in place
            // (needed because GL stores rows bottom-up, so the
            // straight pixel-buffer save comes out upside-down).
            foreach (MethodInfo m in tImageData.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType != typeof(void)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string) && _saveAsPng == null)
                {
                    _saveAsPng = m;
                    Log($"[TextureDump] resolved PNG-save fn: {tImageData.Name}.{m.Name}");
                }
                else if (ps.Length == 0 && _flipY == null)
                {
                    _flipY = m;
                    Log($"[TextureDump] resolved flip-Y fn: {tImageData.Name}.{m.Name}");
                }
            }
            if (_saveAsPng == null)
            {
                Log($"[TextureDump] no PNG-save (string)→void method on {tImageData.Name}");
                return;
            }

            // ── GClass127.dictionary_0 — the image map ────────────────
            // Static class with a static Dictionary<string, Texture>
            // field. Only one such field exists in the engine.
            foreach (Type t in Nagoya.Types)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (FieldInfo f in fields)
                {
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.Dictionary<,>)) continue;
                    Type[] args = f.FieldType.GetGenericArguments();
                    if (args.Length != 2) continue;
                    if (args[0] != typeof(string)) continue;
                    if (args[1] != tTexture) continue;
                    _dictField = f;
                    Log($"[TextureDump] resolved dictionary field: {t.Name}.{f.Name}");
                    return;
                }
            }
            Log("[TextureDump] image-token dictionary field not found");
        }

        private static void Log(string msg)
        {
            string? path = LogPath;
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path!, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); }
            catch { }
        }
    }
}
