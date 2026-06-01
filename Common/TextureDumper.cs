using System;
using System.Collections;
using System.Collections.Generic;
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

        // Second one-shot: dumps every exploration-entry icon across
        // every ShowExploration script step (walks Script's full step
        // array). Output goes under {OutputDir}/ExplorationIcons/.
        public static volatile bool DumpExplorationRequested;

        public static string? OutputDir;
        public static string? LogPath;

        // ── Cached reflection ─────────────────────────────────────────
        private static bool        _reflected;
        private static FieldInfo?  _dictField;       // GClass127.dictionary_0 (static)
        private static MethodInfo? _getImageData;    // GClass106.smethod_10(Texture) → GClass22
        private static MethodInfo? _saveAsPng;       // GClass22.method_5(string)
        private static MethodInfo? _flipY;           // GClass22.method_2() — in-place vertical flip

        /// <summary>Render-thread entry point. Cheap fast-path when no
        /// dump is pending: just checks the two volatile flags.</summary>
        public static void DumpFromRenderThread()
        {
            if (DumpRequested)            { DumpRequested = false;            DumpEmailDictionary(); }
            if (DumpExplorationRequested) { DumpExplorationRequested = false; DumpExplorationIcons(); }
        }

        private static void DumpEmailDictionary()
        {
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
                    if (TrySaveTexture(key, Path.Combine(dir, key + ".png"), kv.Value)) ok++;
                    else                                                                fail++;
                }
                Log($"[TextureDump] {ok} saved / {fail} failed → {dir}");
            }
            catch (Exception ex)
            {
                Log($"[TextureDump] error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TrySaveTexture(string logKey, string path, object texture)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
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
                Log($"[TextureDump] {logKey}: {inner?.GetType().Name ?? "unknown"}: {inner?.Message ?? "no message"}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[TextureDump] {logKey}: {ex.GetType().Name}: {ex.Message}");
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

        // ─────────────────────────────────────────────────────────────
        // Exploration-icon dump
        // ─────────────────────────────────────────────────────────────

        // Walks Script.<steps>[], finds every ShowExploration step,
        // and dumps each entry's icon texture to a PNG. File path
        // mirrors the texture's asset path (e.g.
        // "textures/backgrounds/light_rail/front_seat_hover" →
        // ExplorationIcons/backgrounds/light_rail/front_seat_hover.png),
        // so the directory tree groups icons by chapter / scene.
        // Deduplicated by asset path — the same icon used across
        // entries is written once.
        private static bool        _exploreReflected;
        private static MethodInfo? _scriptSingletonMethod;
        private static FieldInfo?  _scriptStepsField;
        private static MethodInfo? _exploreCastMethod;
        private static FieldInfo?  _sceneField;
        private static FieldInfo?  _entriesArrayField;
        private static FieldInfo?  _entryVisualField;
        private static FieldInfo?  _visualTextureField;
        private static FieldInfo?  _textureMaybeField;
        private static MethodInfo? _maybeStringHasValue;
        private static MethodInfo? _maybeStringGetValue;

        private static void DumpExplorationIcons()
        {
            try
            {
                EnsureReflected();
                EnsureExplorationReflected();
                if (_getImageData == null || _saveAsPng == null
                    || _scriptSingletonMethod == null || _scriptStepsField == null
                    || _exploreCastMethod == null || _sceneField == null
                    || _entriesArrayField == null
                    || _entryVisualField == null || _visualTextureField == null
                    || _textureMaybeField == null
                    || _maybeStringHasValue == null || _maybeStringGetValue == null)
                {
                    Log("[TextureDump/Exploration] reflection incomplete — aborting");
                    return;
                }

                object? script = _scriptSingletonMethod.Invoke(null, null);
                if (script == null) { Log("[TextureDump/Exploration] script singleton null"); return; }
                if (!(_scriptStepsField.GetValue(script) is Array steps))
                {
                    Log("[TextureDump/Exploration] script steps field returned non-array");
                    return;
                }

                string baseDir = Path.Combine(
                    OutputDir ?? Path.Combine(Path.GetDirectoryName(LogPath ?? ".") ?? ".", "TextureDumps"),
                    "ExplorationIcons");
                Directory.CreateDirectory(baseDir);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int ok = 0, fail = 0, scenes = 0, entries = 0;

                for (int i = 0; i < steps.Length; i++)
                {
                    object? step = steps.GetValue(i);
                    if (step == null) continue;
                    object? castResult;
                    // The cast throws GException0 when the step isn't
                    // ShowExploration — that's our discriminator.
                    try { castResult = _exploreCastMethod.Invoke(step, null); }
                    catch { continue; }
                    if (castResult == null) continue;

                    object? scene; try { scene = _sceneField.GetValue(castResult); } catch { continue; }
                    if (scene == null) continue;
                    Array? entryArr; try { entryArr = _entriesArrayField.GetValue(scene) as Array; } catch { continue; }
                    if (entryArr == null) continue;
                    scenes++;

                    for (int j = 0; j < entryArr.Length; j++)
                    {
                        object? entry = entryArr.GetValue(j);
                        if (entry == null) continue;
                        entries++;
                        if (TryDumpEntryIcon(entry, baseDir, seen)) ok++;
                        else                                       fail++;
                    }
                }
                Log($"[TextureDump/Exploration] {scenes} scenes, {entries} entries, " +
                    $"{ok} icons saved / {fail} failed / {seen.Count} unique → {baseDir}");
            }
            catch (Exception ex)
            {
                Log($"[TextureDump/Exploration] error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryDumpEntryIcon(object entry, string baseDir, HashSet<string> seen)
        {
            object? visual; try { visual = _entryVisualField!.GetValue(entry); } catch { return false; }
            if (visual == null) return false;
            object? texture; try { texture = _visualTextureField!.GetValue(visual); } catch { return false; }
            if (texture == null) return false;
            object? maybe; try { maybe = _textureMaybeField!.GetValue(texture); } catch { return false; }
            if (maybe == null) return false;

            string? asset;
            try
            {
                if (!(bool)_maybeStringHasValue!.Invoke(maybe, null)!) return false;
                asset = _maybeStringGetValue!.Invoke(maybe, null) as string;
            }
            catch { return false; }
            if (string.IsNullOrEmpty(asset)) return false;
            if (!seen.Add(asset!)) return true;  // already dumped — count as success, no duplicate write

            string rel = asset!;
            if (rel.StartsWith("textures/", StringComparison.Ordinal))
                rel = rel.Substring("textures/".Length);
            rel = rel.Replace('\\', '/');
            string path = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)) + ".png";
            return TrySaveTexture(asset!, path, texture);
        }

        private static void EnsureExplorationReflected()
        {
            if (_exploreReflected) return;
            _exploreReflected = true;

            Type? tScript = Nagoya.FindType("Script");
            if (tScript == null) { Log("[TextureDump/Exploration] Script type not found"); return; }

            // Script's singleton accessor: static no-arg method
            // returning a Script instance (smethod_0 in the clean
            // decompile).
            foreach (MethodInfo m in tScript.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType != tScript) continue;
                _scriptSingletonMethod = m;
                Log($"[TextureDump/Exploration] script singleton: {tScript.Name}.{m.Name}");
                break;
            }
            if (_scriptSingletonMethod == null) { Log("[TextureDump/Exploration] script singleton not found"); return; }

            // Script.gclass10_0 — array of script steps. Element type
            // (GClass10) is a tagged union with 30+ bool no-arg
            // discriminators. Same signature ExplorationSubView uses
            // to find the current-step accessor on WorldScreen.
            Type? tStep = null;
            const BindingFlags inst =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (FieldInfo f in tScript.GetFields(inst))
            {
                Type ft = f.FieldType;
                if (!ft.IsArray) continue;
                Type elem = ft.GetElementType()!;
                if (elem.IsValueType) continue;
                int bools = 0;
                foreach (MethodInfo m in elem.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool)) bools++;
                }
                if (bools < 30) continue;
                _scriptStepsField = f;
                tStep = elem;
                Log($"[TextureDump/Exploration] script steps: {f.Name} of {elem.Name}[] (discriminators={bools})");
                break;
            }
            if (_scriptStepsField == null || tStep == null) { Log("[TextureDump/Exploration] steps field not found"); return; }

            // ShowExploration cast on GClass10: instance no-arg method
            // returning a struct with one field whose type has a
            // ScriptExplorationEntry[] field. Throws on the wrong
            // tagged-union case (used as the discriminator at dump time).
            Type? tEntry = Nagoya.FindType("ScriptExplorationEntry");
            if (tEntry == null) { Log("[TextureDump/Exploration] ScriptExplorationEntry not found"); return; }

            foreach (MethodInfo m in tStep.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (!rt.IsValueType) continue;
                FieldInfo[] sfs = rt.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (sfs.Length != 1) continue;
                Type sceneType = sfs[0].FieldType;
                FieldInfo? entriesField = null;
                foreach (FieldInfo sf in sceneType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (sf.FieldType.IsArray && sf.FieldType.GetElementType() == tEntry)
                    { entriesField = sf; break; }
                }
                if (entriesField == null) continue;
                _exploreCastMethod = m;
                _sceneField        = sfs[0];
                _entriesArrayField = entriesField;
                Log($"[TextureDump/Exploration] cast: {tStep.Name}.{m.Name} → {rt.Name} → {sceneType.Name}.{entriesField.Name}");
                break;
            }
            if (_exploreCastMethod == null || _sceneField == null || _entriesArrayField == null)
            {
                Log("[TextureDump/Exploration] ShowExploration cast / scene / entries not resolved");
                return;
            }

            // Entry → visual (first non-array, non-string, non-value
            // reference field — same shape ExplorationSubView matches).
            Type? tVisual = null;
            foreach (FieldInfo f in tEntry.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Type ft = f.FieldType;
                if (ft.IsArray || ft.IsValueType || ft == typeof(string)) continue;
                _entryVisualField = f;
                tVisual = ft;
                break;
            }
            if (_entryVisualField == null || tVisual == null) { Log("[TextureDump/Exploration] visual not found"); return; }

            // Visual → first Texture-typed field (the icon).
            Type? tTexture = Nagoya.FindType("Texture");
            if (tTexture == null) { Log("[TextureDump/Exploration] Texture type not found"); return; }
            foreach (FieldInfo f in tVisual.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType == tTexture) { _visualTextureField = f; break; }
            }
            if (_visualTextureField == null) { Log("[TextureDump/Exploration] visual texture not found"); return; }

            // Texture → Maybe<string> asset name + accessors.
            foreach (FieldInfo f in tTexture.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length == 1 && args[0] == typeof(string))
                {
                    _textureMaybeField = f;
                    foreach (MethodInfo m in f.FieldType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.ReturnType == typeof(bool)) _maybeStringHasValue = m;
                        else if (m.ReturnType == typeof(string) && m.Name != "ToString") _maybeStringGetValue = m;
                    }
                    break;
                }
            }
            if (_textureMaybeField == null || _maybeStringHasValue == null || _maybeStringGetValue == null)
            {
                Log("[TextureDump/Exploration] texture asset-name chain not resolved");
                return;
            }
            Log("[TextureDump/Exploration] reflection ready");
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
