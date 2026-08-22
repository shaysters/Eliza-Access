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

        // Third one-shot: dumps every location background and every
        // character sprite (base + per-location variants + the two
        // phone-portrait textures) from the game's two static script
        // registries. Output goes under {OutputDir}/Locations/ and
        // {OutputDir}/Characters/, with an index file mapping
        // name/variant → file.
        public static volatile bool DumpLocationsCharactersRequested;

        // Set by the render thread when the loc/char dump finishes;
        // Main's poll loop speaks and clears it (the dumper has no
        // speech access of its own, and speech belongs on the poll
        // thread anyway).
        public static volatile string? DoneAnnouncement;

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
            if (DumpLocationsCharactersRequested)
            {
                DumpLocationsCharactersRequested = false;
                BeginLocationsCharactersDump();
            }
            // The loc/char dump is incremental: ~80 full-screen PNG
            // encodes in one blocking call froze the game for 10+
            // seconds, so instead one texture is processed per throttled
            // render-thread visit and the game keeps pumping frames.
            if (_lcQueue != null) ProcessLocCharQueueItem();
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
            string? asset = TryGetAssetPath(texture);
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
            EnsureTextureAssetChain();
            if (_textureMaybeField == null || _maybeStringHasValue == null || _maybeStringGetValue == null)
            {
                Log("[TextureDump/Exploration] texture asset-name chain not resolved");
                return;
            }
            Log("[TextureDump/Exploration] reflection ready");
        }

        // ── Texture → asset-path chain (shared by all dumps) ──────────
        // Texture carries its load path in a Maybe<string> field
        // ("textures/backgrounds/cafe" etc.). Used both for naming
        // output files and for deduplicating shared textures. Also
        // resolves the GL handle (the single IntPtr field) and the
        // pixel-dimensions struct (the single value-type field with
        // exactly two public int fields — Index2) so dumps can tell a
        // loaded texture from a lazy stub: the engine's readback does
        // no such check and wedges the GL driver on handle 0 / 0×0.
        private static bool _assetChainTried;
        private static FieldInfo? _texHandleField;   // IntPtr — GL handle, Zero until loaded
        private static FieldInfo? _texSizeField;     // Index2 — storage dims, 0×0 until loaded
        private static FieldInfo? _texSizeXField;
        private static FieldInfo? _texSizeYField;

        private static void EnsureTextureAssetChain()
        {
            if (_assetChainTried) return;
            _assetChainTried = true;
            Type? tTexture = Nagoya.FindType("Texture");
            if (tTexture == null) return;
            foreach (FieldInfo f in tTexture.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType == typeof(IntPtr))
                {
                    if (_texHandleField == null) _texHandleField = f;
                    continue;
                }
                if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum
                    && !f.FieldType.IsGenericType && _texSizeField == null)
                {
                    FieldInfo[] inner = f.FieldType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    if (inner.Length == 2 && inner[0].FieldType == typeof(int)
                                          && inner[1].FieldType == typeof(int))
                    {
                        _texSizeField  = f;
                        _texSizeXField = inner[0];
                        _texSizeYField = inner[1];
                    }
                    continue;
                }
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length == 1 && args[0] == typeof(string) && _textureMaybeField == null)
                {
                    _textureMaybeField = f;
                    foreach (MethodInfo m in f.FieldType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.ReturnType == typeof(bool)) _maybeStringHasValue = m;
                        else if (m.ReturnType == typeof(string) && m.Name != "ToString") _maybeStringGetValue = m;
                    }
                }
            }
        }

        // GL handle + pixel dims of a texture; (Zero, 0, 0) when the
        // state fields couldn't be resolved (callers then skip the
        // guard rather than skip the texture — matches pre-guard
        // behavior for the other dumps).
        private static void ReadTextureState(object texture, out IntPtr handle, out int w, out int h)
        {
            handle = IntPtr.Zero; w = 0; h = 0;
            try
            {
                if (_texHandleField != null)
                    handle = (IntPtr)_texHandleField.GetValue(texture)!;
                if (_texSizeField != null && _texSizeXField != null && _texSizeYField != null)
                {
                    object? size = _texSizeField.GetValue(texture);
                    if (size != null)
                    {
                        w = (int)_texSizeXField.GetValue(size)!;
                        h = (int)_texSizeYField.GetValue(size)!;
                    }
                }
            }
            catch { }
        }

        private static string? TryGetAssetPath(object texture)
        {
            if (_textureMaybeField == null || _maybeStringHasValue == null
                || _maybeStringGetValue == null) return null;
            try
            {
                object? maybe = _textureMaybeField.GetValue(texture);
                if (maybe == null) return null;
                if (!(bool)_maybeStringHasValue.Invoke(maybe, null)!) return null;
                return _maybeStringGetValue.Invoke(maybe, null) as string;
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────
        // Locations + characters dump (F11)
        // ─────────────────────────────────────────────────────────────

        // Walks the game's two static script registries — the all-
        // locations ScriptLocation[] ("black", "counseling-office",
        // "cafe-a", … 27 entries) and the all-characters
        // ScriptCharacter[] ("Evelyn", "Rae", … 25 entries) — and
        // dumps every texture they reference:
        //  - location backgrounds: the static Maybe<Texture>, or for
        //    animated locations the animation's base frame plus its
        //    patch atlas (the base frame is the representative image;
        //    the atlas holds the small animated deltas),
        //  - character sprites: the base visual's Texture[] plus every
        //    entry in the per-location variant dictionary
        //    ("nightclub", "cafe-a", …),
        //  - the two phone-portrait textures per character.
        // Files get semantic names (Locations/cafe-a.png,
        // Characters/Evelyn__nightclub.png); textures shared between
        // entries are written once, with index lines pointing at the
        // first file. locations_characters_index.txt maps every
        // name/variant to its file (or notes "no image").
        //
        // Type names ScriptLocation / ScriptCharacter survive
        // obfuscation (verified in the raw decompile); their FIELD
        // names don't, so fields are matched by type shape:
        //  - ScriptLocation: single string field = name;
        //    Maybe<Texture> = static background; the Maybe<T> whose T
        //    itself has ≥2 Texture fields = the animation (base +
        //    atlas). The other Maybes (two Maybe<int> frame indices,
        //    the flash-export class with no direct Texture fields)
        //    can't match either pattern.
        //  - ScriptCharacter: single string field = name; the class-
        //    typed field exposing a Texture[] = the base visual; the
        //    Dictionary<string, thatType> = variants; the two Texture
        //    fields in declaration order = the phone portraits.
        private static bool        _locCharReflected;
        private static FieldInfo?  _locationsArrayField;    // static ScriptLocation[]
        private static FieldInfo?  _charactersArrayField;   // static ScriptCharacter[]
        private static FieldInfo?  _locNameField;
        private static FieldInfo?  _locMaybeTextureField;
        private static MethodInfo? _locMaybeTexHasValue;
        private static MethodInfo? _locMaybeTexGetValue;
        private static FieldInfo?  _locMaybeAnimField;
        private static MethodInfo? _locMaybeAnimHasValue;
        private static MethodInfo? _locMaybeAnimGetValue;
        private static FieldInfo?  _animBaseTextureField;
        private static FieldInfo?  _animAtlasTextureField;
        private static FieldInfo?  _charNameField;
        private static FieldInfo?  _charVisualField;
        private static FieldInfo?  _charVariantsField;
        private static FieldInfo?  _charPortraitAField;
        private static FieldInfo?  _charPortraitBField;
        private static FieldInfo?  _visualTexturesField;    // Texture[] on the visual class

        // One queued dump item: a texture to save (or, with Texture
        // null, a pass-through index line — section headers and
        // "no image" notes keep their place in the index this way).
        private sealed class PendingTexture
        {
            public object? Texture;
            public string  RelPath = "";
            public string  Label   = "";
            public string? Note;
        }

        private static Queue<PendingTexture>?      _lcQueue;
        private static Dictionary<string, string>? _lcByAsset;
        private static Dictionary<object, string>? _lcByRef;
        private static List<string>?               _lcIndex;
        private static string?                     _lcBaseDir;
        private static int _lcOk, _lcFail, _lcSkip, _lcTotal;
        private static int _lcLastItemTick;

        // Walk both registries and build the work queue. No GL calls
        // here — the actual readbacks happen one at a time in
        // ProcessLocCharQueueItem so the game keeps rendering.
        private static void BeginLocationsCharactersDump()
        {
            try
            {
                if (_lcQueue != null)
                {
                    Log("[TextureDump/LocChar] dump already in progress — ignoring");
                    return;
                }
                EnsureReflected();
                EnsureTextureAssetChain();
                EnsureLocCharReflected();
                if (_getImageData == null || _saveAsPng == null)
                {
                    Log("[TextureDump/LocChar] save helpers unresolved — aborting");
                    return;
                }
                if (_locationsArrayField == null && _charactersArrayField == null)
                {
                    Log("[TextureDump/LocChar] neither registry resolved — aborting");
                    return;
                }

                _lcBaseDir = OutputDir ?? Path.Combine(
                    Path.GetDirectoryName(LogPath ?? ".") ?? ".", "TextureDumps");
                Directory.CreateDirectory(_lcBaseDir);

                var queue = new Queue<PendingTexture>();
                queue.Enqueue(new PendingTexture { Note = "[Locations]" });
                if (_locationsArrayField?.GetValue(null) is Array locs)
                {
                    for (int i = 0; i < locs.Length; i++)
                    {
                        object? loc = locs.GetValue(i);
                        if (loc != null) EnqueueLocation(queue, loc, i);
                    }
                }
                else queue.Enqueue(new PendingTexture { Note = "(location registry not resolved or not yet populated)" });

                queue.Enqueue(new PendingTexture { Note = "" });
                queue.Enqueue(new PendingTexture { Note = "[Characters]" });
                if (_charactersArrayField?.GetValue(null) is Array chars)
                {
                    for (int i = 0; i < chars.Length; i++)
                    {
                        object? ch = chars.GetValue(i);
                        if (ch != null) EnqueueCharacter(queue, ch, i);
                    }
                }
                else queue.Enqueue(new PendingTexture { Note = "(character registry not resolved or not yet populated)" });

                _lcQueue   = queue;
                _lcByAsset = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _lcByRef   = new Dictionary<object, string>();
                _lcIndex   = new List<string>();
                _lcOk = 0; _lcFail = 0; _lcSkip = 0;
                _lcTotal = queue.Count;
                _lcLastItemTick = 0;
                Log($"[TextureDump/LocChar] queued {queue.Count} item(s) → {_lcBaseDir}");
            }
            catch (Exception ex)
            {
                _lcQueue = null;
                Log($"[TextureDump/LocChar] begin error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnqueueLocation(Queue<PendingTexture> queue, object loc, int i)
        {
            string name = (_locNameField?.GetValue(loc) as string) is string s && s.Length > 0
                ? s : $"location-{i}";

            object? tex  = ReadMaybe(loc, _locMaybeTextureField, _locMaybeTexHasValue, _locMaybeTexGetValue);
            object? anim = ReadMaybe(loc, _locMaybeAnimField,    _locMaybeAnimHasValue, _locMaybeAnimGetValue);

            if (tex != null)
                queue.Enqueue(new PendingTexture
                    { Texture = tex, RelPath = $"Locations/{name}.png", Label = name });
            if (anim != null)
            {
                object? baseTex  = null; object? atlasTex = null;
                try { baseTex  = _animBaseTextureField?.GetValue(anim); }  catch { }
                try { atlasTex = _animAtlasTextureField?.GetValue(anim); } catch { }
                if (baseTex != null)
                    queue.Enqueue(new PendingTexture
                        { Texture = baseTex, RelPath = $"Locations/{name}.png", Label = $"{name} (animated: base frame)" });
                if (atlasTex != null)
                    queue.Enqueue(new PendingTexture
                        { Texture = atlasTex, RelPath = $"Locations/{name}__anim_patches.png", Label = $"{name} (animation patch atlas)" });
            }
            if (tex == null && anim == null)
                queue.Enqueue(new PendingTexture { Note = $"{name}: no image (blank location)" });
        }

        private static void EnqueueCharacter(Queue<PendingTexture> queue, object ch, int i)
        {
            string name = (_charNameField?.GetValue(ch) as string) is string s && s.Length > 0
                ? s : $"character-{i}";

            object? visual = null;
            try { visual = _charVisualField?.GetValue(ch); } catch { }
            EnqueueVisual(queue, visual, $"Characters/{name}", name);

            object? variants = null;
            try { variants = _charVariantsField?.GetValue(ch); } catch { }
            if (variants is IDictionary dict)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    string variant = kv.Key as string ?? "variant";
                    EnqueueVisual(queue, kv.Value, $"Characters/{name}__{variant}", $"{name} variant '{variant}'");
                }
            }

            object? portraitA = null; object? portraitB = null;
            try { portraitA = _charPortraitAField?.GetValue(ch); } catch { }
            try { portraitB = _charPortraitBField?.GetValue(ch); } catch { }
            if (portraitA != null)
                queue.Enqueue(new PendingTexture
                    { Texture = portraitA, RelPath = $"Characters/{name}__portrait_a.png", Label = $"{name} portrait A" });
            if (portraitB != null)
                queue.Enqueue(new PendingTexture
                    { Texture = portraitB, RelPath = $"Characters/{name}__portrait_b.png", Label = $"{name} portrait B" });
        }

        // A visual is the GClass35 shape: a Texture[] (usually one
        // frame; NoraExplaining has two) plus flags. One file per
        // frame; the single-frame common case gets no suffix.
        private static void EnqueueVisual(Queue<PendingTexture> queue, object? visual, string relBase, string label)
        {
            if (visual == null || _visualTexturesField == null) return;
            Array? texArr = null;
            try { texArr = _visualTexturesField.GetValue(visual) as Array; } catch { }
            if (texArr == null || texArr.Length == 0) return;
            for (int i = 0; i < texArr.Length; i++)
            {
                object? t = texArr.GetValue(i);
                if (t == null) continue;
                queue.Enqueue(new PendingTexture
                {
                    Texture = t,
                    RelPath = texArr.Length == 1 ? relBase + ".png" : $"{relBase}__frame{i}.png",
                    Label   = texArr.Length == 1 ? label            : $"{label} frame {i}",
                });
            }
        }

        // Process one queue item per throttled render-thread visit.
        // The postfix hook fires many times per frame, so without the
        // tick throttle a whole frame's visits would drain the queue
        // in one burst and freeze the game exactly like the
        // monolithic version did.
        private static void ProcessLocCharQueueItem()
        {
            Queue<PendingTexture>? queue = _lcQueue;
            if (queue == null) return;
            if (queue.Count == 0) { FinishLocCharDump(); return; }

            int now = Environment.TickCount;
            if (now - _lcLastItemTick < 30) return;
            _lcLastItemTick = now;

            PendingTexture item = queue.Dequeue();
            try   { ProcessOneLocChar(item); }
            catch (Exception ex)
            {
                _lcFail++;
                _lcIndex?.Add($"{item.Label}: ERROR {ex.GetType().Name}: {ex.Message}");
                Log($"[TextureDump/LocChar] {item.Label}: {ex.GetType().Name}: {ex.Message}");
            }
            if (queue.Count == 0) FinishLocCharDump();
        }

        private static void ProcessOneLocChar(PendingTexture item)
        {
            if (item.Texture == null)
            {
                if (item.Note != null) _lcIndex?.Add(item.Note);
                return;
            }
            if (_lcIndex == null || _lcByAsset == null || _lcByRef == null || _lcBaseDir == null) return;

            string? asset = TryGetAssetPath(item.Texture);
            string? existing = null;
            if (asset != null && _lcByAsset.TryGetValue(asset, out string? e1)) existing = e1;
            else if (_lcByRef.TryGetValue(item.Texture, out string? e2))        existing = e2;
            if (existing != null)
            {
                _lcIndex.Add($"{item.Label}: same image as {existing}");
                return;
            }

            // Loaded-texture guard: the engine's readback passes the
            // GL handle and a dims-sized buffer to the driver with no
            // validation, and a lazy stub (handle 0, 0×0) wedges it —
            // that was the F11 hang, on the first texture the boot
            // preload hadn't populated. Skip and say how to get it.
            ReadTextureState(item.Texture, out IntPtr handle, out int w, out int h);
            bool stateKnown = _texHandleField != null && _texSizeField != null;
            if (stateKnown && (handle == IntPtr.Zero || w <= 0 || h <= 0))
            {
                _lcSkip++;
                _lcIndex.Add($"{item.Label}: SKIPPED — texture not loaded (handle={handle.ToInt64():X}, {w}x{h}). " +
                             "Play to a scene that shows it, then press F11 again.");
                Log($"[TextureDump/LocChar] skip (not loaded): {item.Label} (asset: {asset ?? "<none>"})");
                return;
            }

            // Progress line BEFORE the readback: if a texture still
            // manages to wedge the driver, the log names it.
            int done = _lcTotal - (_lcQueue?.Count ?? 0);
            Log($"[TextureDump/LocChar] {done}/{_lcTotal} {item.Label} ({w}x{h})");

            string full = Path.Combine(_lcBaseDir, item.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (TrySaveTexture(item.Label, full, item.Texture))
            {
                _lcOk++;
                _lcIndex.Add(asset != null
                    ? $"{item.Label}: {item.RelPath} (asset: {asset})"
                    : $"{item.Label}: {item.RelPath}");
                if (asset != null) _lcByAsset[asset] = item.RelPath;
                _lcByRef[item.Texture] = item.RelPath;
            }
            else
            {
                _lcFail++;
                _lcIndex.Add($"{item.Label}: SAVE FAILED");
            }
        }

        private static void FinishLocCharDump()
        {
            try
            {
                if (_lcIndex != null && _lcBaseDir != null)
                {
                    string indexPath = Path.Combine(_lcBaseDir, "locations_characters_index.txt");
                    File.WriteAllLines(indexPath, _lcIndex);
                    Log($"[TextureDump/LocChar] done: {_lcOk} saved / {_lcSkip} skipped / {_lcFail} failed " +
                        $"→ {_lcBaseDir} (index: {indexPath})");
                    string extras = "";
                    if (_lcSkip > 0) extras += $", {_lcSkip} skipped as not loaded";
                    if (_lcFail > 0) extras += $", {_lcFail} failed";
                    DoneAnnouncement = $"Image dump complete. {_lcOk} images saved{extras}.";
                }
            }
            catch (Exception ex)
            {
                Log($"[TextureDump/LocChar] finish error: {ex.GetType().Name}: {ex.Message}");
            }
            _lcQueue = null; _lcByAsset = null; _lcByRef = null;
            _lcIndex = null; _lcBaseDir = null;
        }

        private static object? ReadMaybe(object owner, FieldInfo? field,
            MethodInfo? hasValue, MethodInfo? getValue)
        {
            if (field == null || hasValue == null || getValue == null) return null;
            try
            {
                object? maybe = field.GetValue(owner);
                if (maybe == null) return null;
                if (!(bool)hasValue.Invoke(maybe, null)!) return null;
                return getValue.Invoke(maybe, null);
            }
            catch { return null; }
        }

        private static void EnsureLocCharReflected()
        {
            if (_locCharReflected) return;
            _locCharReflected = true;

            Type? tTexture = Nagoya.FindType("Texture");
            Type? tLoc     = Nagoya.FindType("ScriptLocation");
            Type? tChar    = Nagoya.FindType("ScriptCharacter");
            if (tTexture == null || tLoc == null || tChar == null)
            {
                Log($"[TextureDump/LocChar] types: Texture={tTexture != null} " +
                    $"ScriptLocation={tLoc != null} ScriptCharacter={tChar != null} — aborting");
                return;
            }

            _locationsArrayField  = FindStaticArrayField(tLoc);
            _charactersArrayField = FindStaticArrayField(tChar);

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;

            // ── ScriptLocation fields ─────────────────────────────────
            foreach (FieldInfo f in tLoc.GetFields(pubInst))
            {
                if (f.FieldType == typeof(string))
                {
                    if (_locNameField == null) _locNameField = f;
                    continue;
                }
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length != 1) continue;
                if (args[0] == tTexture && _locMaybeTextureField == null)
                {
                    _locMaybeTextureField = f;
                    ResolveMaybeAccessors(f.FieldType, args[0],
                        out _locMaybeTexHasValue, out _locMaybeTexGetValue);
                }
                else if (_locMaybeAnimField == null
                         && CountFieldsOfType(args[0], tTexture) >= 2)
                {
                    _locMaybeAnimField = f;
                    ResolveMaybeAccessors(f.FieldType, args[0],
                        out _locMaybeAnimHasValue, out _locMaybeAnimGetValue);
                    // Animation class: first Texture field is the base
                    // frame, second is the patch atlas (declaration
                    // order — matches the ctor argument order).
                    foreach (FieldInfo af in args[0].GetFields(pubInst))
                    {
                        if (af.FieldType != tTexture) continue;
                        if      (_animBaseTextureField == null)  _animBaseTextureField  = af;
                        else if (_animAtlasTextureField == null) _animAtlasTextureField = af;
                    }
                }
            }

            // ── ScriptCharacter fields ────────────────────────────────
            Type? tVisual = null;
            foreach (FieldInfo f in tChar.GetFields(pubInst))
            {
                Type ft = f.FieldType;
                if (ft == typeof(string))
                {
                    if (_charNameField == null) _charNameField = f;
                }
                else if (ft == tTexture)
                {
                    if      (_charPortraitAField == null) _charPortraitAField = f;
                    else if (_charPortraitBField == null) _charPortraitBField = f;
                }
                else if (_charVisualField == null && ft.IsClass && !ft.IsGenericType
                         && !ft.IsArray && HasFieldOfArrayType(ft, tTexture))
                {
                    _charVisualField = f;
                    tVisual = ft;
                }
                else if (_charVariantsField == null && ft.IsGenericType
                         && ft.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    Type[] dargs = ft.GetGenericArguments();
                    if (dargs[0] == typeof(string)) _charVariantsField = f;
                }
            }
            if (tVisual != null)
            {
                foreach (FieldInfo f in tVisual.GetFields(pubInst))
                {
                    if (f.FieldType.IsArray && f.FieldType.GetElementType() == tTexture)
                    { _visualTexturesField = f; break; }
                }
            }

            Log($"[TextureDump/LocChar] resolved: locArray={_locationsArrayField != null} " +
                $"charArray={_charactersArrayField != null} " +
                $"locName={_locNameField != null} locTex={_locMaybeTextureField != null} " +
                $"locAnim={_locMaybeAnimField != null} animBase={_animBaseTextureField != null} " +
                $"animAtlas={_animAtlasTextureField != null} " +
                $"charName={_charNameField != null} visual={_charVisualField != null} " +
                $"variants={_charVariantsField != null} " +
                $"portraits={_charPortraitAField != null}/{_charPortraitBField != null} " +
                $"visualTexArr={_visualTexturesField != null}");
        }

        private static FieldInfo? FindStaticArrayField(Type elemType)
        {
            foreach (Type t in Nagoya.Types)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (FieldInfo f in fields)
                {
                    if (f.FieldType.IsArray && f.FieldType.GetElementType() == elemType)
                    {
                        Log($"[TextureDump/LocChar] registry array: {t.Name}.{f.Name} ({elemType.Name}[])");
                        return f;
                    }
                }
            }
            Log($"[TextureDump/LocChar] no static {elemType.Name}[] field found");
            return null;
        }

        private static void ResolveMaybeAccessors(Type maybeType, Type valueType,
            out MethodInfo? hasValue, out MethodInfo? getValue)
        {
            hasValue = null; getValue = null;
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool))
                { if (hasValue == null) hasValue = m; }
                else if (m.ReturnType == valueType && m.Name != "ToString")
                { if (getValue == null) getValue = m; }
            }
        }

        private static int CountFieldsOfType(Type owner, Type fieldType)
        {
            int n = 0;
            try
            {
                foreach (FieldInfo f in owner.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.FieldType == fieldType) n++;
            }
            catch { }
            return n;
        }

        private static bool HasFieldOfArrayType(Type owner, Type elemType)
        {
            try
            {
                foreach (FieldInfo f in owner.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.FieldType.IsArray && f.FieldType.GetElementType() == elemType) return true;
            }
            catch { }
            return false;
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
