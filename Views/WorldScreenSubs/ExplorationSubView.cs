using System;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Announces in-story "look around" / exploration scenes (the monorail
    /// intro where Evelyn has thoughts about objects on the train, and any
    /// later equivalents).
    ///
    /// Script step: <c>GClass10.ShowExploration</c> (method_22 in the
    /// tagged-union discriminator). Data flow:
    /// <code>
    ///   currentStep.method_22()                       // is this a ShowExploration step?
    ///   currentStep.method_58().gclass12_0            // → GClass12 scene
    ///                          .scriptExplorationEntry_0[]
    /// </code>
    /// Each <c>ScriptExplorationEntry</c> has:
    /// - <c>gclass13_0 : GClass13</c> with <c>bounds2_0</c> — the icon's
    ///   on-screen hit box.
    /// - <c>gclass23_0 : GClass23[]</c> — localized strings. <c>[0]</c> is
    ///   the entry's name/label (the screen-reader announcement);
    ///   <c>[1+]</c> are Evelyn's thoughts spoken when the entry is
    ///   clicked (handled by V2AutoObserver's dialogue-line path).
    ///
    /// Each entry is registered as a real hotspot via
    /// <c>VirtualInput.smethod_7</c> at WorldScreen.cs:1891, but the game
    /// only draws an icon sprite at the entry's position — there's no
    /// text-draw, so <see cref="Patches.HotspotLabelCapture"/> finds
    /// nothing to pair. That's why the mode is silent today. This SubView
    /// closes the gap by matching the cursor's position against each
    /// entry's bounds directly and reading the label from gclass23_0[0]
    /// instead of going through HotspotLabelCapture.
    ///
    /// Activation: <c>IsActive ⇔ currentStep.method_22()</c>.
    /// Lifecycle:
    /// - <c>OnEnter</c>: enumerate entries, speak
    ///   "Look around. N things to interact with."
    /// - <c>OnUpdate</c>: find the entry whose bounds contains the cursor,
    ///   announce its label on change.
    /// - <c>OnLeave</c>: clear tracked focus.
    /// </summary>
    internal sealed class ExplorationSubView : SubView
    {
        // ── WorldScreen-level reflection (resolved in OnInit) ──────────
        // The clean decompile calls this "method_0" but that's a de4dot
        // rename — the runtime DLL has an obfuscated hash name, so we
        // resolve by signature: an instance no-arg method whose return
        // type has 30+ bool no-arg "method_X" discriminators (the
        // tagged-union GClass10 has 36).
        private MethodInfo? _currentStepMethod;

        // Path-A reflection for the session-label feed (see SessionLabel
        // region below). Independent of the path-B currentStep traversal
        // used for activation + entry enumeration.
        private const string ScriptStateMethodName        = "#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=";
        private const string GetExplorationDataMethodName = "#=qhhWPJ8fBX7pe_LsaBPuzWXxKbAdt7hQ53WXUyMRPlrU=";
        private const string SessionDataFieldName         = "#=q1V5SAz4ePyBSHfbO4e$BSw==";
        private const string SessionLabelFieldName        = "#=qKfr2g1SCaWvhyqxZtYgZag==";
        private MethodInfo? _scriptStateMethod;
        private MethodInfo? _getExplorationDataMethod;
        private FieldInfo?  _sessionDataField;
        private FieldInfo?  _sessionLabelField;
        private bool        _sessionChainReflectAttempted;

        // ── ScriptStep-level reflection (deferred to first live step) ──
        private bool _stepReflectDone;
        // The "ShowExploration" cast (clean name method_58). Found by
        // signature: returns a struct with a single field whose type has
        // a ScriptExplorationEntry[] field. Throws on wrong-type cast
        // — we use try/catch in IsActive as the discriminator instead of
        // hunting for the bool method_22.
        private MethodInfo? _getExplorationStructMethod;
        private FieldInfo?  _gStruct19SceneField;      // struct's single GClass12-typed field

        // ── GClass12 (scene) reflection ────────────────────────────────
        private bool _sceneReflectDone;
        private FieldInfo? _entriesField;              // GClass12.scriptExplorationEntry_0 → ScriptExplorationEntry[]

        // ── ScriptExplorationEntry reflection ──────────────────────────
        private bool _entryReflectDone;
        private FieldInfo? _entryVisualField;          // ScriptExplorationEntry.gclass13_0 → GClass13
        private FieldInfo? _entryTextsField;           // ScriptExplorationEntry.gclass23_0 → GClass23[]
        private FieldInfo? _visualBoundsField;         // GClass13.bounds2_0 → Bounds2
        private FieldInfo? _visualTextureField;        // GClass13.texture_0 → Texture (for name lookup)
        private FieldInfo? _textureNameMaybeField;     // Texture.maybe_0 → Maybe<string> (asset id/path)
        private MethodInfo? _maybeStringHasValue;
        private MethodInfo? _maybeStringGetValue;

        // ── Bounds2 / Vector2 field cache (for cursor-in-bounds tests) ──
        // Same approach HotspotLabelCapture uses: read fields off boxed
        // values rather than trusting de4dot's clean names.
        private bool _boundsFieldsCached;
        private FieldInfo? _boundsMinField, _boundsMaxField, _vec2XField, _vec2YField;

        // Cursor getter (same one CursorFocus uses)
        private MethodInfo? _cursorPosMethod;

        // ── Per-activation state ───────────────────────────────────────
        private int _lastFocusedIdx = -1;
        // Independent tracker for non-entry hotspots in the same scene
        // (notably the Continue button at WorldScreen.cs:1849). When the
        // cursor leaves the entries and lands on a labeled hotspot, we
        // pick its label up from HotspotLabelCapture and announce it.
        // Tracked separately so navigating entries→Continue→entry doesn't
        // re-announce the same label twice.
        private int _lastSideHotspotIdx = -1;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // Find the "current step" accessor by signature: an instance
            // no-arg method whose return type has 30+ bool no-arg
            // discriminator methods (GClass10's tagged-union shape, with
            // 36 type-check methods).
            foreach (MethodInfo m in tWS.GetMethods(inst))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (rt.IsValueType || rt == typeof(string)) continue;
                if (CountBoolNoArgMethods(rt) >= 30)
                {
                    _currentStepMethod = m;
                    break;
                }
            }

            _scriptStateMethod = tWS.GetMethod(ScriptStateMethodName, inst);

            // Cursor position getter — same lookup as CursorFocus
            // (NonPublic | Public; runtime modifier is private).
            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI != null)
            {
                foreach (MethodInfo m in tVI.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 0) continue;
                    if (m.ReturnType.Name != "Vector2") continue;
                    _cursorPosMethod = m;
                    break;
                }
            }

            Log($"[ExplorationSubView] init: currentStep={(_currentStepMethod != null)} " +
                $"cursorGet={(_cursorPosMethod != null)}");
        }

        private static int CountBoolNoArgMethods(Type t)
        {
            int n = 0;
            foreach (MethodInfo m in t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool)) n++;
            }
            return n;
        }

        public override bool IsActive
        {
            get
            {
                object? step = GetCurrentStep();
                if (step == null) return false;
                if (!_stepReflectDone) ReflectStep(step);
                if (_getExplorationStructMethod == null) return false;
                // GClass10.method_58 throws GException0("Invalid tagged union
                // cast") when the current step is NOT ShowExploration. We use
                // the throw vs success as the discriminator since the bool
                // method_22's runtime name is also obfuscated and not
                // straightforward to identify by signature.
                try
                {
                    object? result = _getExplorationStructMethod.Invoke(step, null);
                    return result != null;
                }
                catch { return false; }
            }
        }

        public override void OnEnter()
        {
            _lastFocusedIdx = -1;
            var entries = ReadEntries();
            int n = entries?.Count ?? 0;
            string ann = n switch
            {
                0 => "Look around.",
                1 => "Look around. One thing to interact with.",
                _ => $"Look around. {n} things to interact with."
            };
            Speech.Speak(ann);
            Log($"[ExplorationSubView] enter: {n} entries");
        }

        public override void OnPoll()
        {
            // Runs every tick regardless of IsActive — keeps the shared
            // ExplorationState.CurrentSessionLabel fresh throughout the
            // whole chapter (e.g., "light-rail01"), not just during the
            // brief ShowExploration look-around bursts. V2PhoneObserver
            // reads it from arbitrary screens (e.g., the phone home grid
            // opened mid-chapter) and would otherwise see null.
            RefreshSessionLabel();
        }

        public override void OnUpdate()
        {
            var entries = ReadEntries();
            if (entries == null || entries.Count == 0) return;

            int idx = FindEntryUnderCursor(entries);
            if (idx >= 0)
            {
                // Cursor is on an entry. Announce if it's a new one;
                // reset the side-hotspot tracker so re-entering Continue
                // later re-announces it.
                _lastSideHotspotIdx = -1;
                if (idx == _lastFocusedIdx) return;
                _lastFocusedIdx = idx;
                string? label = ReadEntryLabel(entries[idx]);
                if (!string.IsNullOrEmpty(label))
                {
                    Speech.Speak(label!);
                    Log($"[ExplorationSubView] focus → [{idx}] \"{label}\"");
                }
                return;
            }

            // Cursor isn't on any entry. Might be on Continue (or any
            // other labeled hotspot the game renders alongside the
            // exploration icons — Continue is the only one in practice).
            // Pick up its label from HotspotLabelCapture so the user can
            // tell when D-pad navigation has carried them off the
            // exploration list.
            int hotspotIdx = CursorFocus.FindHotspotIndex();
            if (hotspotIdx < 0) return;          // cursor between hotspots — preserve last
            if (hotspotIdx == _lastSideHotspotIdx) return;
            string? sideLabel = HotspotLabelCapture.GetLabel(hotspotIdx);
            if (string.IsNullOrEmpty(sideLabel)) return;  // unlabeled hotspot (another entry slot we didn't match by center); ignore
            _lastSideHotspotIdx = hotspotIdx;
            _lastFocusedIdx = -1;       // re-announce next entry on return
            Speech.Speak(sideLabel!);
            Log($"[ExplorationSubView] focus → side[{hotspotIdx}] \"{sideLabel}\"");
        }

        public override void OnLeave()
        {
            _lastFocusedIdx = -1;
            _lastSideHotspotIdx = -1;
            // Don't clear ExplorationState.CurrentSessionLabel here. The
            // label tracks the whole chapter, not the look-around step —
            // RefreshSessionLabel() in OnPoll updates / nulls it based on
            // whether the live scriptState still has exploration data.
        }

        // ── Session label (path A: scriptState → exploration data → label) ──
        //
        // Different traversal from the path-B currentStep walk used for
        // activation and entry rendering. Path A reaches the running-
        // session data (an ExplorationSessionData object with the scene
        // label and per-entry unlock state); path B reaches the script
        // step's static scene definition (GClass12 with the entry array).
        // Both routes are active at the same time during an exploration
        // scene, but only path A exposes the "light-rail01" style label
        // that V2PhoneObserver needs to detect the intro chapter.

        private void RefreshSessionLabel()
        {
            if (_scriptStateMethod == null) return;

            object? ss;
            try { ss = _scriptStateMethod.Invoke(Parent, null); }
            catch { return; }
            if (ss == null) return;

            if (!_sessionChainReflectAttempted)
                ReflectSessionChain(ss);

            if (_getExplorationDataMethod == null
                || _sessionDataField == null
                || _sessionLabelField == null) return;

            string? next = null;
            try
            {
                object? wrapper = _getExplorationDataMethod.Invoke(ss, null);
                if (wrapper != null)
                {
                    object? sessionData = _sessionDataField.GetValue(wrapper);
                    if (sessionData != null)
                        next = _sessionLabelField.GetValue(sessionData) as string;
                }
            }
            catch { /* fall through with next=null */ }

            if (ExplorationState.CurrentSessionLabel != next)
            {
                Log($"[ExplorationSubView] session label: " +
                    $"\"{ExplorationState.CurrentSessionLabel ?? "<null>"}\" → \"{next ?? "<null>"}\"");
                ExplorationState.CurrentSessionLabel = next;
            }
        }

        private void ReflectSessionChain(object ss)
        {
            _sessionChainReflectAttempted = true;
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.DeclaredOnly;

                _getExplorationDataMethod = ss.GetType().GetMethod(GetExplorationDataMethodName, bf);
                if (_getExplorationDataMethod == null) return;

                Type wrapperType = _getExplorationDataMethod.ReturnType;
                _sessionDataField = wrapperType.GetField(SessionDataFieldName, bf);
                if (_sessionDataField == null) return;

                _sessionLabelField = _sessionDataField.FieldType.GetField(SessionLabelFieldName, bf);

                Log($"[ExplorationSubView] session-chain: getData={_getExplorationDataMethod != null} " +
                    $"sessionData={_sessionDataField != null} label={_sessionLabelField != null}");
            }
            catch (Exception ex)
            {
                Log($"[ExplorationSubView] ReflectSessionChain error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Reflection helpers ─────────────────────────────────────────

        private object? GetCurrentStep()
        {
            try { return _currentStepMethod?.Invoke(Parent, null); }
            catch { return null; }
        }

        private void ReflectStep(object step)
        {
            _stepReflectDone = true;
            Type t = step.GetType();

            // Find the ShowExploration cast: a no-arg public instance
            // method returning a struct that contains a single field whose
            // type has a ScriptExplorationEntry[] field.
            Type? tEntry = Nagoya.FindType("ScriptExplorationEntry");
            if (tEntry == null)
            {
                Log("[ExplorationSubView] step-reflect: ScriptExplorationEntry type not found");
                return;
            }

            foreach (MethodInfo m in t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (!rt.IsValueType) continue;
                FieldInfo[] sfs = rt.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (sfs.Length != 1) continue;
                Type sceneType = sfs[0].FieldType;
                if (!HasEntriesArrayField(sceneType, tEntry)) continue;
                _getExplorationStructMethod = m;
                _gStruct19SceneField = sfs[0];
                break;
            }

            Log($"[ExplorationSubView] step-reflect ({t.Name}): " +
                $"castMethod={_getExplorationStructMethod?.Name ?? "<null>"} " +
                $"sceneField={_gStruct19SceneField?.Name ?? "<null>"}");
        }

        private static bool HasEntriesArrayField(Type sceneType, Type entryType)
        {
            foreach (FieldInfo f in sceneType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType.IsArray && f.FieldType.GetElementType() == entryType) return true;
            }
            return false;
        }

        private List<object>? ReadEntries()
        {
            object? step = GetCurrentStep();
            if (step == null || _getExplorationStructMethod == null) return null;

            object? gStruct19;
            try { gStruct19 = _getExplorationStructMethod.Invoke(step, null); }
            catch { return null; }
            if (gStruct19 == null) return null;

            // First time: find GStruct19.gclass12_0 — the only field, of type GClass12.
            if (_gStruct19SceneField == null)
            {
                foreach (FieldInfo f in gStruct19.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    _gStruct19SceneField = f;   // GStruct19 has exactly one field
                    break;
                }
            }
            if (_gStruct19SceneField == null) return null;

            object? scene;
            try { scene = _gStruct19SceneField.GetValue(gStruct19); }
            catch { return null; }
            if (scene == null) return null;

            // First time: find GClass12.scriptExplorationEntry_0 — the array
            // of entries. Match by element type having both a GClass13-like
            // and a GClass23[]-like field. Simpler: array field that's not
            // string-based.
            if (!_sceneReflectDone) ReflectScene(scene);
            if (_entriesField == null) return null;

            Array? arr;
            try { arr = _entriesField.GetValue(scene) as Array; }
            catch { return null; }
            if (arr == null) return null;

            var result = new List<object>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                object? e = arr.GetValue(i);
                if (e != null) result.Add(e);
            }
            return result;
        }

        private void ReflectScene(object scene)
        {
            _sceneReflectDone = true;
            Type t = scene.GetType();
            // GClass12 fields: string_0, scriptExplorationEntry_0[], bool_0.
            // Pick the array field.
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType.IsArray && f.FieldType.GetElementType()!.Name == "ScriptExplorationEntry")
                {
                    _entriesField = f;
                    break;
                }
            }
            Log($"[ExplorationSubView] scene-reflect ({t.Name}): entries={_entriesField?.Name ?? "<null>"}");
        }

        private string? ReadEntryLabel(object entry)
        {
            if (!_entryReflectDone) ReflectEntry(entry);

            // Prefer the texture's asset name (no spoiler — the dialogue
            // is what plays when you click). Fall back to the first
            // dialogue line if the texture has no name set.
            string? texName = ReadEntryTextureName(entry);
            Log($"[ExplorationSubView] label-source: texName=\"{texName ?? "<null>"}\"");
            if (!string.IsNullOrEmpty(texName))
            {
                return CleanTextureName(texName!);
            }

            if (_entryTextsField == null) return null;
            try
            {
                if (_entryTextsField.GetValue(entry) is Array texts && texts.Length > 0)
                {
                    object? first = texts.GetValue(0);
                    return first?.ToString();
                }
            }
            catch { }
            return null;
        }

        private string? ReadEntryTextureName(object entry)
        {
            if (_entryVisualField == null || _visualTextureField == null
                || _textureNameMaybeField == null
                || _maybeStringHasValue == null || _maybeStringGetValue == null)
                return null;
            try
            {
                object? visual = _entryVisualField.GetValue(entry);
                if (visual == null) return null;
                object? texture = _visualTextureField.GetValue(visual);
                if (texture == null) return null;
                object? maybe = _textureNameMaybeField.GetValue(texture);
                if (maybe == null) return null;
                if (!(bool)_maybeStringHasValue.Invoke(maybe, null)!) return null;
                return _maybeStringGetValue.Invoke(maybe, null) as string;
            }
            catch { return null; }
        }

        /// <summary>Turn an asset path like
        /// "textures/backgrounds/light_rail/front_seat_hover" into
        /// something speakable: strip directory + extension, drop the
        /// "_hover" / "_idle" / "_highlight" state suffix (the texture
        /// we resolve is always the hover-state variant by convention
        /// at GClass13.texture_1), replace separators with spaces,
        /// title-case the first letter.</summary>
        private static string CleanTextureName(string raw)
        {
            string s = raw.Trim();
            int slash = s.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            // Strip common state-variant suffixes the game uses for
            // texture naming. They describe the rendered state, not the
            // semantic identity of the item.
            string[] stateSuffixes = { "_hover", "_highlight", "_idle", "_pressed", "_active" };
            foreach (string suf in stateSuffixes)
            {
                if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(0, s.Length - suf.Length);
                    break;
                }
            }
            s = s.Replace('_', ' ').Replace('-', ' ');
            if (s.Length > 0 && char.IsLetter(s[0]) && char.IsLower(s[0]))
                s = char.ToUpper(s[0]) + s.Substring(1);
            return s;
        }

        private void ReflectEntry(object entry)
        {
            _entryReflectDone = true;
            Type t = entry.GetType();
            // ScriptExplorationEntry has exactly two public fields per
            // source: gclass23_0 (the localized text array) and gclass13_0
            // (the visual). Both class types are de4dot renames that
            // don't survive at runtime, so match by shape:
            //   - the array field is the texts
            //   - the non-array reference field is the visual
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType.IsArray)
                {
                    if (_entryTextsField == null) _entryTextsField = f;
                }
                else if (!f.FieldType.IsValueType && f.FieldType != typeof(string))
                {
                    if (_entryVisualField == null) _entryVisualField = f;
                }
            }
            // GClass13 (visual) fields: Texture×2, Bounds2.
            // Bounds2 is preserved at runtime by name. Texture's name is
            // also preserved (file Texture.cs exists in the decompile,
            // indicating de4dot recovered it). Find both for label
            // lookup.
            if (_entryVisualField != null)
            {
                Type tVisual = _entryVisualField.FieldType;
                foreach (FieldInfo f in tVisual.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.Name == "Bounds2")
                    {
                        if (_visualBoundsField == null) _visualBoundsField = f;
                    }
                    else if (f.FieldType.Name == "Texture")
                    {
                        // Use the first texture (primary icon, before the
                        // hover-state version) as the label source.
                        if (_visualTextureField == null) _visualTextureField = f;
                    }
                }
            }

            // Texture.maybe_0 (Maybe<string>) — the asset path/id used
            // as the texture's identity in Equals(). Reflect by shape:
            // first generic instance field whose type argument is string.
            if (_visualTextureField != null)
            {
                Type tTex = _visualTextureField.FieldType;
                foreach (FieldInfo f in tTex.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    Type[] args = f.FieldType.GetGenericArguments();
                    if (args.Length == 1 && args[0] == typeof(string))
                    {
                        _textureNameMaybeField = f;
                        // Maybe<string>.HasValue() / GetValue() — same
                        // shape we resolved in ChoiceSubView.
                        Type tMaybe = f.FieldType;
                        foreach (MethodInfo m in tMaybe.GetMethods(
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (m.GetParameters().Length != 0) continue;
                            if (m.ReturnType == typeof(bool)) _maybeStringHasValue = m;
                            else if (m.ReturnType == typeof(string) && m.Name != "ToString") _maybeStringGetValue = m;
                        }
                        break;
                    }
                }
            }

            Log($"[ExplorationSubView] entry-reflect ({t.Name}): " +
                $"texts={_entryTextsField?.Name ?? "<null>"} " +
                $"visual={_entryVisualField?.Name ?? "<null>"} " +
                $"bounds={_visualBoundsField?.Name ?? "<null>"} " +
                $"texture={_visualTextureField?.Name ?? "<null>"} " +
                $"texName={_textureNameMaybeField?.Name ?? "<null>"} " +
                $"maybeStr.Has={_maybeStringHasValue != null} Get={_maybeStringGetValue != null}");
        }

        private int FindEntryUnderCursor(List<object> entries)
        {
            if (_cursorPosMethod == null) return -1;
            if (entries.Count == 0) return -1;
            // First-time entry-shape reflection. We need to know the
            // entry's visual/bounds fields before we can test cursor
            // containment — without this call, we'd bail out below before
            // ReadEntryLabel ever runs the reflection itself.
            if (!_entryReflectDone) ReflectEntry(entries[0]);
            if (_entryVisualField == null || _visualBoundsField == null) return -1;

            object? cursor;
            try { cursor = _cursorPosMethod.Invoke(null, null); }
            catch { return -1; }
            if (cursor == null) return -1;

            if (!TryReadVector2(cursor, out float cx, out float cy)) return -1;

            // Each entry's stored bounds (gclass13_0.bounds2_0) is a large
            // "interaction zone" — much wider than the icon itself, and
            // wide enough to overlap with sibling UI (notably the
            // Continue button below the icons). The game compensates in
            // controller mode by overriding the hotspot to a 20x20 region
            // at the bounds' center (WorldScreen.cs:1890), so the cursor
            // only ever snaps to that center point. We match the same
            // logic — cursor must be within ±10 units of the entry's
            // bounds center — so D-pad navigation onto Continue doesn't
            // falsely register as one of the entries.
            const float Tolerance = 10f;
            for (int i = 0; i < entries.Count; i++)
            {
                object? visual;
                try { visual = _entryVisualField.GetValue(entries[i]); }
                catch { continue; }
                if (visual == null) continue;

                object? bounds;
                try { bounds = _visualBoundsField.GetValue(visual); }
                catch { continue; }
                if (bounds == null) continue;

                if (TryReadBounds(bounds, out float minX, out float minY, out float maxX, out float maxY))
                {
                    float centerX = (minX + maxX) * 0.5f;
                    float centerY = (minY + maxY) * 0.5f;
                    if (Math.Abs(cx - centerX) <= Tolerance &&
                        Math.Abs(cy - centerY) <= Tolerance)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        // ── Bounds2 / Vector2 reflection (same trick HotspotLabelCapture uses) ──

        private bool TryReadVector2(object vec, out float x, out float y)
        {
            x = y = 0f;
            EnsureBoundsFieldsCached(vec);
            if (_vec2XField == null || _vec2YField == null) return false;
            try
            {
                x = (float)_vec2XField.GetValue(vec)!;
                y = (float)_vec2YField.GetValue(vec)!;
                return true;
            }
            catch { return false; }
        }

        private bool TryReadBounds(object bounds,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = minY = maxX = maxY = 0f;
            EnsureBoundsFieldsCached(bounds);
            if (_boundsMinField == null || _boundsMaxField == null
                || _vec2XField == null || _vec2YField == null) return false;
            try
            {
                object? mn = _boundsMinField.GetValue(bounds);
                object? mx = _boundsMaxField.GetValue(bounds);
                if (mn == null || mx == null) return false;
                minX = (float)_vec2XField.GetValue(mn)!;
                minY = (float)_vec2YField.GetValue(mn)!;
                maxX = (float)_vec2XField.GetValue(mx)!;
                maxY = (float)_vec2YField.GetValue(mx)!;
                return true;
            }
            catch { return false; }
        }

        private void EnsureBoundsFieldsCached(object sample)
        {
            if (_boundsFieldsCached) return;
            // sample could be either a Bounds2 or a Vector2. We need both
            // Bounds2.Min/Max and Vector2.{x,y} — derive from whichever
            // shows up first by walking the type.
            Type t = sample.GetType();
            if (t.Name == "Bounds2")
            {
                _boundsMinField = t.GetField("Min", BindingFlags.Public | BindingFlags.Instance);
                _boundsMaxField = t.GetField("Max", BindingFlags.Public | BindingFlags.Instance);
                if (_boundsMinField != null)
                {
                    Type tV = _boundsMinField.FieldType;
                    CacheVec2Fields(tV);
                }
                _boundsFieldsCached = (_boundsMinField != null && _vec2XField != null);
            }
            else if (t.Name == "Vector2")
            {
                CacheVec2Fields(t);
                // Bounds2 fields come on the next bounds-shaped sample.
                _boundsFieldsCached = _vec2XField != null && _boundsMinField != null;
            }
        }

        private void CacheVec2Fields(Type vec2Type)
        {
            int floatsFound = 0;
            foreach (FieldInfo f in vec2Type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(float)) continue;
                if (floatsFound == 0) _vec2XField = f;
                else if (floatsFound == 1) { _vec2YField = f; break; }
                floatsFound++;
            }
        }
    }
}
