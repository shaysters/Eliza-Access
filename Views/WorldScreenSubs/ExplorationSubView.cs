using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;
using ElizaAccess.UI.Elements;

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
    ///
    /// Navigation: the engine's controller-cursor snap-to-nearest-hotspot
    /// nav is too erratic on this screen to be useful (the hotspot
    /// layout is sparse and visually-arranged, not arranged by the
    /// engine's bounds-translation heuristic). We take over: suppress
    /// both axes via <see cref="InputSuppressor"/> so neither real
    /// controller input nor our keyboard injection reaches the engine,
    /// then drive an ordered nav list ourselves with the Up/Down arrow
    /// keys. The list contains every interactable entry plus the
    /// side-rendered Continue button, sorted top-to-bottom then
    /// left-to-right so the reading order matches what a sighted
    /// player sees.
    ///
    /// Lifecycle:
    /// - <c>OnEnter</c>: suppress axes, build nav list, snap cursor to
    ///   the first item, speak entry-count + first-item label.
    /// - <c>OnUpdate</c>: poll Up/Down rising edges; on press, move
    ///   focus index (clamped), snap engine cursor to that item, speak
    ///   the new item's label.
    /// - <c>OnLeave</c>: release axis suppression, clear nav state.
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

        // Cursor setter + Vector2 ctor (for snapping the engine cursor
        // to the focused nav item). Same shape as SolitaireView's
        // bootstrap-cursor plumbing.
        private const string SetCursorMethodName = "#=qT_SlnKGmQty0k3OLe9Ydng==";
        private MethodInfo? _setCursorMethod;
        private Type?       _tVector2;

        // VirtualInput.list_0 + hotspot bounds field, for enumerating
        // side hotspots (Continue button) that aren't part of the
        // entry data model.
        private FieldInfo?  _hotspotListField;
        private FieldInfo?  _hotspotBoundsField;

        // ── Per-activation state ───────────────────────────────────────
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);
        // Ordered navigation list, rebuilt each tick from current entries
        // plus current side hotspots. Sort order is (Y desc, X asc) so
        // reading goes top-to-bottom then left-to-right.
        private readonly List<NavItem> _navList = new List<NavItem>();
        private int _navIdx = -1;
        // Label of the currently-focused item. The nav list can change
        // shape between polls (the Continue button's hotspot drops in
        // and out of list_0, and its HotspotLabelCapture pairing fails
        // intermittently), so tracking focus by index alone causes a
        // silent reclamp that the user can't perceive — they press DOWN
        // again and hear the same item, or press UP and skip an entry.
        // Tracking by label lets us re-anchor _navIdx to the same item
        // even when its position in the list moved.
        private string? _focusedLabel;
        // True between OnEnter and OnLeave, used to decide whether to
        // release axis suppression on a re-entry without OnLeave (safety
        // belt for the lifecycle).
        private bool _axesSuppressed;
        // Scene-ready gate. Entries register their hotspots in list_0
        // only after their fade-in finishes (WorldScreen.cs:1879 — the
        // `num4 == texture_50.Length` guard around smethod_7). Letting
        // the user navigate before all entries are interactable is
        // misleading: they hear an item announced, press Enter, and
        // nothing happens because there's no hotspot at the cursor
        // yet. _ready flips true once we see N entry-matched hotspots
        // in list_0 (or the safety deadline elapses).
        private bool     _ready;
        private DateTime _readyDeadline;
        // OnEnter timestamp + one-shot "Loading." announce gate. The
        // gap between OnEnter (count announces) and _ready (first item
        // announces) is several seconds on the train scenes while the
        // entry sprites fade in. Without intermediate feedback the
        // player thinks the mod has hung. If the wait exceeds the
        // threshold we say "Loading." once — fast scenes stay quiet.
        private DateTime _enteredAtUtc;
        private bool     _loadingAnnounced;
        private static readonly TimeSpan LoadingAnnounceAfter = TimeSpan.FromMilliseconds(500);

        private struct NavItem
        {
            public float  CenterX;
            public float  CenterY;
            public string Label;
        }

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

            // SetCursor + Vector2 type for snapping the engine cursor to
            // the focused nav item.
            _tVector2 = Nagoya.FindType("Vector2");
            if (tVI != null && _tVector2 != null)
            {
                _setCursorMethod = tVI.GetMethod(SetCursorMethodName,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { _tVector2 }, null);
            }

            // list_0 (hotspot list) for enumerating side hotspots
            // (the Continue button). Same signature-based lookup as
            // CursorFocus.
            if (tVI != null)
            {
                foreach (FieldInfo f in tVI.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(List<>)) continue;
                    Type elem = f.FieldType.GetGenericArguments()[0];
                    if (elem.Name == "VirtualInputHotspot") { _hotspotListField = f; break; }
                }
            }
            Type? tHotspot = Nagoya.FindType("VirtualInputHotspot");
            if (tHotspot != null)
            {
                foreach (FieldInfo f in tHotspot.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType.Name == "Bounds2") { _hotspotBoundsField = f; break; }
                }
            }

            Log($"[ExplorationSubView] init: currentStep={(_currentStepMethod != null)} " +
                $"cursorGet={(_cursorPosMethod != null)} " +
                $"setCursor={(_setCursorMethod != null)} " +
                $"hotspotList={(_hotspotListField != null)} " +
                $"hotspotBounds={(_hotspotBoundsField != null)}");
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
            // Take exclusive control of the cursor for the duration of
            // this look-around step. Engine's own controller-cursor
            // nav doesn't reach reliably between sparse hotspots; we
            // drive it ourselves via the ordered nav list.
            SuppressAxes();
            _kbUp.Reset();
            _kbDown.Reset();
            _ready             = false;
            _readyDeadline     = DateTime.UtcNow.AddSeconds(5);
            _enteredAtUtc      = DateTime.UtcNow;
            _loadingAnnounced  = false;

            var entries = ReadEntries();
            int n = entries?.Count ?? 0;
            string countPart = n switch
            {
                0 => "Look around. Nothing to interact with.",
                1 => "Look around. One thing to interact with.",
                _ => $"Look around. {n} things to interact with."
            };

            // Announce the count up front so the user gets feedback that
            // the screen has loaded — but DON'T announce the focused
            // item or process input until the hotspots have registered.
            Speech.Speak(countPart);

            if (n == 0)
            {
                // Nothing to wait for.
                _ready = true;
                _navIdx = -1;
                _focusedLabel = null;
                Log("[ExplorationSubView] enter: no entries, ready immediately");
            }
            else
            {
                Log($"[ExplorationSubView] enter: {n} entries, waiting for hotspots to register");
            }
        }

        public override void OnPoll()
        {
            // Runs every tick regardless of IsActive — keeps the shared
            // ExplorationState.CurrentSessionLabel fresh throughout the
            // whole chapter (e.g., "light-rail01"), not just during the
            // brief ShowExploration look-around bursts. Other observers
            // read it from arbitrary screens (e.g., the phone home grid
            // opened mid-chapter) and would otherwise see null.
            RefreshSessionLabel();
        }

        public override void OnUpdate()
        {
            if (!_ready)
            {
                if (!_loadingAnnounced
                    && DateTime.UtcNow - _enteredAtUtc >= LoadingAnnounceAfter)
                {
                    _loadingAnnounced = true;
                    Speech.Speak("Loading.");
                    Log("[ExplorationSubView] loading announce (ready gate still open)");
                }
                if (!TryAdvanceReady()) return;
                _ready = true;
                // Just became ready — build the nav list, snap cursor
                // to the first item, and speak it. From here on,
                // OnUpdate processes input normally.
                RebuildNavList();
                if (_navList.Count > 0)
                {
                    _navIdx = 0;
                    _focusedLabel = _navList[0].Label;
                    SnapCursorToFocused();
                    SpeakFocused();
                    Log($"[ExplorationSubView] ready, focus → [0] \"{_focusedLabel}\"");
                }
                return;
            }

            // Side hotspots (Continue) appear partway through and may
            // shift between polls; entries don't change once IsActive
            // is true. Cheap to rebuild, keeps the list current.
            RebuildNavList();
            if (_navList.Count == 0) return;

            // Re-anchor _navIdx to the previously-focused label. The
            // list can change shape between polls — Continue's hotspot
            // pairing is fragile (the button text is drawn with
            // EnableMeasuring=false so HotspotLabelCapture sees zero-
            // size bounds and the pair-by-containment match misses).
            // If we naïvely clamped to the new list size, the user
            // would silently slide to a lower index between key
            // presses, then press a key and hear the wrong item.
            if (_focusedLabel != null)
            {
                int found = _navList.FindIndex(n => n.Label == _focusedLabel);
                if (found >= 0)
                {
                    _navIdx = found;
                }
                else
                {
                    // Focused item really is gone (not just temporarily
                    // missing this poll — could be a scene-state change).
                    // Fall back to clamping by old idx and re-anchor on
                    // whatever's at that position now.
                    if (_navIdx >= _navList.Count) _navIdx = _navList.Count - 1;
                    if (_navIdx < 0)               _navIdx = 0;
                    _focusedLabel = _navList[_navIdx].Label;
                }
            }
            else
            {
                if (_navIdx >= _navList.Count) _navIdx = _navList.Count - 1;
                if (_navIdx < 0)               _navIdx = 0;
                _focusedLabel = _navList[_navIdx].Label;
            }

            bool up   = _kbUp.Pressed();
            bool down = _kbDown.Pressed();
            if (!up && !down) return;

            // Apply each direction independently rather than picking one,
            // so a poll that sees BOTH edges (a fast down-then-up tap
            // pair landing in the same 5 ms window) doesn't silently
            // drop one of them. Speech is interruptible, so back-to-back
            // Speak calls just truncate the first announcement.
            if (down) Move(+1, "down");
            if (up)   Move(-1, "up");
        }

        /// <summary>
        /// Returns true once the scene is interactable. The engine
        /// only registers an entry's hotspot in <c>list_0</c> after
        /// the entry's fade-in completes (WorldScreen.cs:1879 — the
        /// <c>num4 == texture_50.Length</c> guard). When all N data-
        /// model entries have a matching hotspot, the scene is ready.
        ///
        /// Bails out (returns true) after <see cref="_readyDeadline"/>
        /// regardless of hotspot count — if the count-match check is
        /// broken (entry positions don't quite line up with hotspot
        /// centers under some scene variant), the user should still
        /// be able to navigate after a few seconds rather than stay
        /// frozen forever.
        /// </summary>
        private bool TryAdvanceReady()
        {
            if (DateTime.UtcNow >= _readyDeadline)
            {
                Log("[ExplorationSubView] ready deadline elapsed; advancing without hotspot match");
                return true;
            }
            var entries = ReadEntries();
            int expected = entries?.Count ?? 0;
            if (expected == 0) return true;
            int matched = CountEntryHotspotsInList0(entries!);
            return matched >= expected;
        }

        private int CountEntryHotspotsInList0(List<object> entries)
        {
            if (_hotspotListField == null || _hotspotBoundsField == null) return 0;
            IList? list;
            try { list = _hotspotListField.GetValue(null) as IList; }
            catch { return 0; }
            if (list == null || list.Count == 0) return 0;

            if (!_entryReflectDone && entries.Count > 0) ReflectEntry(entries[0]);
            const float Match = 4f;
            int matched = 0;
            foreach (object entry in entries)
            {
                if (!TryGetEntryBoundsCenter(entry, out float ex, out float ey)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    object? h;
                    try { h = list[i]; } catch { break; }
                    if (h == null) continue;
                    object? b;
                    try { b = _hotspotBoundsField.GetValue(h); } catch { continue; }
                    if (b == null) continue;
                    if (!TryReadBounds(b, out float mnX, out float mnY, out float mxX, out float mxY)) continue;
                    float cx = (mnX + mxX) * 0.5f;
                    float cy = (mnY + mxY) * 0.5f;
                    if (Math.Abs(cx - ex) <= Match && Math.Abs(cy - ey) <= Match)
                    {
                        matched++;
                        break;
                    }
                }
            }
            return matched;
        }

        private void Move(int delta, string direction)
        {
            int newIdx = _navIdx + delta;
            if (newIdx < 0)               newIdx = 0;
            if (newIdx >= _navList.Count) newIdx = _navList.Count - 1;
            if (newIdx == _navIdx) return;
            _navIdx = newIdx;
            _focusedLabel = _navList[_navIdx].Label;
            SnapCursorToFocused();
            SpeakFocused();
            Log($"[ExplorationSubView] {direction} → [{_navIdx}] \"{_focusedLabel}\"");
        }

        // Speak the currently-focused nav item via ButtonElement so
        // entries and the Continue button announce as "label, button"
        // — matches every other clickable surface in the mod.
        private void SpeakFocused()
        {
            if (_focusedLabel == null) return;
            Speech.Speak(new ButtonElement(_focusedLabel).ResolveFocusText());
        }

        public override void OnLeave()
        {
            ReleaseAxes();
            InputSuppressor.ClearCursorLock();
            _navList.Clear();
            _stableNavList.Clear();
            _stableNavListTime = DateTime.MinValue;
            _navIdx = -1;
            _focusedLabel = null;
            _lastListShape = "";
            _ready = false;
            // Don't clear ExplorationState.CurrentSessionLabel here. The
            // label tracks the whole chapter, not the look-around step —
            // RefreshSessionLabel() in OnPoll updates / nulls it based on
            // whether the live scriptState still has exploration data.
        }

        // ── Axis suppression lifecycle ─────────────────────────────────

        private void SuppressAxes()
        {
            if (_axesSuppressed) return;
            InputSuppressor.SuppressAxis(InputSuppressor.AxisHorizontal);
            InputSuppressor.SuppressAxis(InputSuppressor.AxisVertical);
            _axesSuppressed = true;
        }

        private void ReleaseAxes()
        {
            if (!_axesSuppressed) return;
            InputSuppressor.ReleaseAxis(InputSuppressor.AxisHorizontal);
            InputSuppressor.ReleaseAxis(InputSuppressor.AxisVertical);
            _axesSuppressed = false;
        }

        // ── Nav list build + cursor snap ───────────────────────────────

        private string _lastListShape = "";
        // Stable nav list — kept around so transient drops in the
        // candidate list (most commonly: the Continue button's hotspot
        // is mid-frame and not yet re-registered, or its label-pair
        // failed for this poll) don't churn _navIdx underneath the user.
        private readonly List<NavItem> _stableNavList = new List<NavItem>();
        private DateTime _stableNavListTime = DateTime.MinValue;

        private void RebuildNavList()
        {
            // Build the candidate from current entries + side hotspots
            // into _navList, then decide whether to keep it or roll
            // back to the stable list.
            BuildCandidateNavList();

            // If the candidate is a STRICT SUBSET of the stable list
            // (every item in the candidate exists in the stable list,
            // but the stable list has more items) and the stable list
            // is recent, keep the stable list. This swallows the
            // single-poll flicker where Continue drops out without
            // anything else changing. If items are added, reordered,
            // or differ from the stable list, accept the candidate.
            const double StaleAfterMs = 500.0;
            bool stableIsFresh = (DateTime.UtcNow - _stableNavListTime).TotalMilliseconds < StaleAfterMs;
            bool candidateIsStrictSubset = stableIsFresh
                && _navList.Count < _stableNavList.Count
                && _navList.TrueForAll(c => _stableNavList.Exists(s => s.Label == c.Label));

            if (candidateIsStrictSubset)
            {
                _navList.Clear();
                _navList.AddRange(_stableNavList);
            }
            else
            {
                _stableNavList.Clear();
                _stableNavList.AddRange(_navList);
                _stableNavListTime = DateTime.UtcNow;
            }

            // Log only when the list shape changes — useful for
            // verifying the stabilizer is doing its job.
            string shape = string.Join(",", _navList.ConvertAll(n => n.Label));
            if (shape != _lastListShape)
            {
                Log($"[ExplorationSubView] navlist shape: [{shape}]");
                _lastListShape = shape;
            }
        }

        private void BuildCandidateNavList()
        {
            _navList.Clear();

            // Entries (icons in the scene). Read label + bounds-center
            // from the data model so we don't depend on HotspotLabel-
            // Capture for the icon labels (icons are sprite-only — no
            // text drawn alongside).
            var entries = ReadEntries();
            var entryCenters = new List<(float X, float Y)>();
            if (entries != null && entries.Count > 0)
            {
                if (!_entryReflectDone) ReflectEntry(entries[0]);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!TryGetEntryBoundsCenter(entries[i], out float cx, out float cy)) continue;
                    string? label = ReadEntryLabel(entries[i]);
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    entryCenters.Add((cx, cy));
                    _navList.Add(new NavItem { CenterX = cx, CenterY = cy, Label = label! });
                }
            }

            // Side hotspots — anything in list_0 whose center doesn't
            // line up with an entry's bounds center. In practice this
            // is the Continue button at the bottom of the scene.
            // We pull its label from HotspotLabelCapture since the
            // engine draws "Continue" text near it.
            AppendSideHotspots(entryCenters);

            // Top-to-bottom, then left-to-right. Engine Y grows up
            // (Bounds2.Bottom == Min.Y per the clean decompile), so
            // "top" = higher Y. Sort Y descending, X ascending.
            _navList.Sort((a, b) =>
            {
                int dy = b.CenterY.CompareTo(a.CenterY);
                if (dy != 0) return dy;
                return a.CenterX.CompareTo(b.CenterX);
            });
        }

        private bool TryGetEntryBoundsCenter(object entry, out float cx, out float cy)
        {
            cx = cy = 0f;
            if (_entryVisualField == null || _visualBoundsField == null) return false;
            object? visual;
            try { visual = _entryVisualField.GetValue(entry); }
            catch { return false; }
            if (visual == null) return false;
            object? bounds;
            try { bounds = _visualBoundsField.GetValue(visual); }
            catch { return false; }
            if (bounds == null) return false;
            if (!TryReadBounds(bounds, out float minX, out float minY, out float maxX, out float maxY))
                return false;
            cx = (minX + maxX) * 0.5f;
            cy = (minY + maxY) * 0.5f;
            return true;
        }

        private void AppendSideHotspots(List<(float X, float Y)> entryCenters)
        {
            if (_hotspotListField == null || _hotspotBoundsField == null) return;
            IList? list;
            try { list = _hotspotListField.GetValue(null) as IList; }
            catch { return; }
            if (list == null) return;

            const float Match = 4f; // px tolerance for "is this hotspot one of our entries"
            for (int i = 0; i < list.Count; i++)
            {
                object? hotspot;
                try { hotspot = list[i]; }
                catch { break; }
                if (hotspot == null) continue;
                object? bounds;
                try { bounds = _hotspotBoundsField.GetValue(hotspot); }
                catch { continue; }
                if (bounds == null) continue;
                if (!TryReadBounds(bounds, out float minX, out float minY, out float maxX, out float maxY))
                    continue;
                float cx = (minX + maxX) * 0.5f;
                float cy = (minY + maxY) * 0.5f;

                bool matchesEntry = false;
                foreach (var (ex, ey) in entryCenters)
                {
                    if (Math.Abs(cx - ex) <= Match && Math.Abs(cy - ey) <= Match)
                    {
                        matchesEntry = true;
                        break;
                    }
                }
                if (matchesEntry) continue;

                string? label = HotspotLabelCapture.GetLabel(i);
                if (string.IsNullOrWhiteSpace(label)) continue;
                _navList.Add(new NavItem { CenterX = cx, CenterY = cy, Label = label! });
            }
        }

        private void SnapCursorToFocused()
        {
            if (_navIdx < 0 || _navIdx >= _navList.Count) return;
            var item = _navList[_navIdx];

            // Set the per-frame lock first so the CursorSnapLock postfix
            // on VirtualInput.smethod_9 re-asserts this position every
            // frame from here on. Without the lock, the engine's per-
            // frame cursor-snap (smethod_9 lines 329-347) would relocate
            // the cursor on the next frame whenever it drifts off our
            // 20×20 hotspot — caused by transient mouse-mode flips or
            // any other source of cursor jitter.
            InputSuppressor.SetCursorLock(item.CenterX, item.CenterY);

            // Also call SetCursor directly now so the cursor is correct
            // on this same frame (the postfix only fires on the NEXT
            // smethod_9 invocation, which may be a frame away).
            if (_setCursorMethod == null || _tVector2 == null) return;
            try
            {
                object pos = Activator.CreateInstance(_tVector2, new object[] { item.CenterX, item.CenterY })!;
                _setCursorMethod.Invoke(null, new[] { pos });
            }
            catch (Exception ex)
            {
                Log($"[ExplorationSubView] SetCursor error: {ex.GetType().Name}: {ex.Message}");
            }
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

        // ── Bounds2 / Vector2 reflection (same trick HotspotLabelCapture uses) ──

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
