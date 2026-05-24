using System;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Reads the SolitaireScreen game out loud. Custom 40-card variant
    /// (ranks 1–10, 4 of each, no suits in the visible model), 8 tableau
    /// piles + 4 "free cells" (the engine arrays call them foundations
    /// but the tutorial text calls them free cells — each holds one
    /// card; 4-of-a-kind in any cell or tableau pile collapses into a
    /// locked stack).
    ///
    /// SolitaireScreen pushes onto the screen stack on top of PhoneScreen
    /// when the player taps the solitaire tile on the home grid; pops
    /// back to PhoneScreen when the player exits.
    ///
    /// Behaviour:
    /// - On focus: re-orient with "Solitaire game." Re-orientation also
    ///   covers returning from the escape menu / a confirm dialog.
    /// - On entry while difficulty-select is still active: announce the
    ///   four localized difficulty labels.
    /// - On the difficulty→game transition: announce that the game is
    ///   starting.
    /// - Win/loss: announce on the game-state edge.
    /// - Cursor focus: as the controller cursor moves over a pile,
    ///   announce that pile's contents (Tableau/Foundation N, count,
    ///   top-card rank or face-down).
    ///
    /// All cards on the board ARE registered as hotspots via
    /// VirtualInput.smethod_7 (SolitaireScreen.cs:654, 675, 712), but
    /// they have no labels drawn near them, so HotspotLabelCapture has
    /// nothing to pair. Pile identity has to come from the game state
    /// itself: walk each pile to its tail, read its stored render
    /// position, and bounds-test the cursor against the card body.
    ///
    /// Game phase comes from <c>GameState.genum24_0</c> (an enum):
    ///   0 = difficulty-select modal showing (SELECT DIFFICULTY)
    ///   1 = transition / dealing
    ///   2 = playing
    ///   3 = won (win-animation phase)
    /// There is no loss state — the variant lets you retry forever via
    /// the NEW GAME side button. A previous V2 mapping treated a
    /// separate bool (SolitaireScreen.bool_0) as "difficulty mode";
    /// that bool is actually the HOW-TO-PLAY modal, unrelated.
    /// </summary>
    internal sealed class SolitaireView : ScreenView
    {
        public override string ScreenTypeName => "SolitaireScreen";

        // ── Obfuscated names ───────────────────────────────────────────
        private const string SolGameStateField       = "#=qomC9fXAO44ewTlpgcgsp3w==";
        private const string SolTableauArrayField    = "#=qpmUgHx13GLWdBV_PKwc4yg==";
        private const string SolFoundationArrayField = "#=qdkaJzFh9FtMj$w385J3jgA==";
        // GameState.genum24_0 — game phase (0=difficulty/1=dealing/2=playing/3=won).
        private const string SolStateEnumField       = "#=qbv8bImizajLuSwCqTVbwvA==";
        private const string SolScoreField           = "#=q43XiqMIj7Y6VnEbgN_9aWg==";
        // SaveData.method_13() — returns the highest difficulty index
        // the player has won at (default -1). Used as the engine's gate
        // `flag4 = j <= method_13() + 1` for which difficulty cards
        // register as hotspots. With -1, only EASY is navigable; harder
        // cards are drawn but inert until the player wins.
        private const string SaveDataUnlockedMethod  = "#=qVEb8UWTzvIw0eI4U5yx491BJARJPcOq$PVxcPDkfqtIOEJTcerAhwRDj5$AVaZl1";
        // SolitaireScreen.bool_0 — true while the HOW TO PLAY tutorial
        // modal is open. The engine binds button 9 (our keyboard 2) to
        // FAKE_KEY_2 which sets this true during gameplay (flag2 =
        // phase != 0 && no card dragged).
        private const string HowToPlayFlagField      = "#=q3pMpNnUvOmjkmB0Yp62luA==";

        // Static HOW TO PLAY text drawn by the engine (SolitaireScreen.cs
        // gclass23_0[0..3]). Hardcoded so we don't need to reflect the
        // static LocalizedText array; if the player plays in a non-
        // English locale we'd reflect via #=qAwVefAmijgEdhOcd_dMEmg==
        // and convert each LocalizedText to string.
        // Loc keys (not the text) so the paragraphs resolve in the
        // current game language at speak time rather than freezing to
        // English at static-init.
        private static readonly string[] HowToPlayParagraphKeys =
        {
            "solitaire.rule_1",
            "solitaire.rule_2",
            "solitaire.rule_3",
            "solitaire.rule_4",
        };
        private const string ItemRankField           = "#=qpxM7EauMEb3FM9sNlLtUXg==";
        // SolitaireItem.bool_3 — set true on collapsed/locked stacks.
        // NOT a face-up flag (V2 misname); every card always shows rank.
        private const string ItemLockedStackField    = "#=q2KLR$Zlo48S7ovCPnvTIZA==";
        private const string ItemChildMaybeField     = "#=qie4_qSKrDOP_kPglHh082Q==";
        private const string ItemPositionField       = "#=qjrFOeygNue3EQRMm4FN_5A==";
        private const string ItemGetTailMethod       = "#=qarU$Uic3qi0edQG2wiQ3qw==";
        private const string MaybeHasValueName       = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValueName       = "#=qE_kIMPOWNCxXDU2FQiQZEw==";
        // VirtualInput.SetCursor(Vector2) — same private static helper
        // ElizaAppSubView uses to drive the engine's cursor for the
        // profile-tab grid. We call it on game start to put the cursor
        // on tableau pile 1; otherwise it stays at its phone-screen-
        // entry position (off the play area entirely, e.g. (0, 2400))
        // and the engine's D-pad navigation has nowhere to start from.
        private const string SetCursorMethod         = "#=qT_SlnKGmQty0k3OLe9Ydng==";

        // Card body bounds match the engine's hotspot at
        // SolitaireScreen.cs:615 — Bounds2.WithSize(pos + (10, 10), (210, 366)).
        // Apply the same (10, 10) offset when bounds-testing the cursor;
        // otherwise our probe is off by 10px in each axis and misses
        // hits the engine considers inside.
        private const float CardOffsetX = 10f;
        private const float CardOffsetY = 10f;
        private const float CardW       = 210f;
        private const float CardH       = 366f;

        // ── Reflected handles ──────────────────────────────────────────
        private FieldInfo?  _solGameStateField;
        private FieldInfo?  _solHowToPlayField;
        private FieldInfo?  _solTableauArrayField;
        private FieldInfo?  _solFoundationArrayField;
        private FieldInfo?  _solStateEnumField;
        private FieldInfo?  _solScoreField;
        private FieldInfo?  _itemRankField;
        private FieldInfo?  _itemLockedStackField;
        private FieldInfo?  _itemChildMaybeField;
        private FieldInfo?  _itemPositionField;
        private MethodInfo? _itemGetTailMethod;
        private MethodInfo? _maybeItemHasValue;
        private MethodInfo? _maybeItemGetValue;
        private MethodInfo? _cursorPosMethod;
        private MethodInfo? _setCursorMethod;
        private Type?       _tVector2;
        private FieldInfo?  _vec2XField;
        private FieldInfo?  _vec2YField;

        // ── Per-activation state ───────────────────────────────────────
        private int  _lastStateEnum = -1;
        private bool _lastHowToPlay;

        // Engine's D-pad nav uses pure-axis bounds-translation in
        // Solitaire mode and won't bridge tableau ↔ free cells unless
        // X aligns exactly — which it doesn't from edge columns. So we
        // intercept arrow Up/Down ourselves and teleport the cursor
        // between rows. (Engine still handles Left/Right; those work
        // fine.)
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);

        protected override void OnAdded(object screen)
        {
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type tScreen = screen.GetType();

            _solGameStateField = tScreen.GetField(SolGameStateField, inst);
            _solHowToPlayField = tScreen.GetField(HowToPlayFlagField, inst);
            if (_solGameStateField != null)
            {
                Type tState = _solGameStateField.FieldType;
                _solTableauArrayField    = tState.GetField(SolTableauArrayField,    inst);
                _solFoundationArrayField = tState.GetField(SolFoundationArrayField, inst);
                _solStateEnumField       = tState.GetField(SolStateEnumField,       inst);
                _solScoreField           = tState.GetField(SolScoreField,           inst);
            }

            Type? tItem = Nagoya.FindType("SolitaireItem");
            if (tItem != null)
            {
                _itemRankField       = tItem.GetField(ItemRankField,       inst);
                _itemLockedStackField = tItem.GetField(ItemLockedStackField, inst);
                _itemChildMaybeField = tItem.GetField(ItemChildMaybeField, inst);
                _itemPositionField   = tItem.GetField(ItemPositionField,   inst);
                _itemGetTailMethod   = tItem.GetMethod(ItemGetTailMethod,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (_itemChildMaybeField != null)
                {
                    const BindingFlags m = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    _maybeItemHasValue = _itemChildMaybeField.FieldType.GetMethod(
                        MaybeHasValueName, m, null, Type.EmptyTypes, null);
                    _maybeItemGetValue = _itemChildMaybeField.FieldType.GetMethod(
                        MaybeGetValueName, m, null, Type.EmptyTypes, null);
                }
            }

            // Cursor position — same static getter CursorFocus uses
            // internally. We need raw coords (not a hotspot index) here
            // because the cards have no labels for HotspotLabelCapture to
            // pair, so we identify piles by walking game state and
            // matching their stored render positions.
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
            if (_cursorPosMethod != null)
            {
                Type tVec = _cursorPosMethod.ReturnType;
                _vec2XField = FindFloatField(tVec, 0);
                _vec2YField = FindFloatField(tVec, 1);
            }

            // SetCursor for bootstrapping initial focus when gameplay
            // starts — the engine doesn't auto-place the cursor on a
            // tableau hotspot, it stays at the phone-entry position.
            _tVector2 = Nagoya.FindType("Vector2");
            if (tVI != null && _tVector2 != null)
            {
                _setCursorMethod = tVI.GetMethod(SetCursorMethod,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { _tVector2 }, null);
            }

            Log($"[SolitaireView] added: gameState={(_solGameStateField != null)} " +
                $"tableau={(_solTableauArrayField != null)} foundation={(_solFoundationArrayField != null)} " +
                $"state={(_solStateEnumField != null)} score={(_solScoreField != null)} " +
                $"itemRank={(_itemRankField != null)} itemPos={(_itemPositionField != null)} " +
                $"getTail={(_itemGetTailMethod != null)} cursor={(_cursorPosMethod != null)} " +
                $"vec2.X={(_vec2XField != null)} vec2.Y={(_vec2YField != null)} " +
                $"setCursor={(_setCursorMethod != null)}");
        }

        private static FieldInfo? FindFloatField(Type tVec, int index)
        {
            int seen = 0;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo f in tVec.GetFields(any))
            {
                if (f.FieldType != typeof(float)) continue;
                if (seen == index) return f;
                seen++;
            }
            return null;
        }

        protected override void OnFocus()
        {
            int phase = ReadPhase();
            if (phase == 0)
            {
                // The engine only registers a hotspot for difficulty j
                // when `j <= saveData.method_13() + 1`. With method_13
                // defaulting to -1 (no wins), only EASY is navigable;
                // the other three cards are drawn but not selectable.
                // Read the unlock level so the announcement reflects
                // what the player can actually pick.
                int unlocked = UnlockedDifficultyCount();
                string[] names =
                {
                    Loc.Get("difficulty.easy"), Loc.Get("difficulty.medium"),
                    Loc.Get("difficulty.hard"), Loc.Get("difficulty.expert")
                };
                if (unlocked >= 4)
                {
                    Speech.Speak(Loc.Get("solitaire.diff_all"));
                }
                else if (unlocked >= 2)
                {
                    string list = string.Join(", ", names, 0, unlocked - 1)
                                + Loc.Get("solitaire.list_or") + names[unlocked - 1];
                    int locked = 4 - unlocked;
                    string lockedPart = Loc.Fmt(
                        locked == 1 ? "solitaire.locked_one" : "solitaire.locked_many", locked);
                    Speech.Speak(Loc.Fmt("solitaire.diff_some", list, lockedPart));
                }
                else
                {
                    Speech.Speak(Loc.Get("solitaire.diff_easy_only"));
                }
                Log($"[SolitaireView] focus (phase=0 difficulty, unlocked={unlocked}).");
            }
            else
            {
                Speech.Speak(Loc.Get("solitaire.game"));
                Log($"[SolitaireView] focus (phase={phase} game).");
            }
            _lastStateEnum = phase;
        }

        // saveData.method_13() returns the highest difficulty index the
        // player has won at, or -1 if none. Unlocked count is
        // clamp(method_13() + 2, 1, 4) (matches the engine's `flag4 =
        // j <= method_13() + 1` over j in 0..3).
        private int UnlockedDifficultyCount()
        {
            try
            {
                Type? tGameLogic = Nagoya.FindType("GameLogic");
                if (tGameLogic == null) return 1;
                FieldInfo? gameLogicInstanceField = null;
                foreach (FieldInfo f in tGameLogic.GetFields(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType == tGameLogic) { gameLogicInstanceField = f; break; }
                }
                if (gameLogicInstanceField == null) return 1;
                object? gameLogic = gameLogicInstanceField.GetValue(null);
                if (gameLogic == null) return 1;

                Type? tSaveData = Nagoya.FindType("SaveData");
                if (tSaveData == null) return 1;
                FieldInfo? saveDataField = null;
                foreach (FieldInfo f in tGameLogic.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType == tSaveData) { saveDataField = f; break; }
                }
                if (saveDataField == null) return 1;
                object? saveData = saveDataField.GetValue(gameLogic);
                if (saveData == null) return 1;

                MethodInfo? m = tSaveData.GetMethod(SaveDataUnlockedMethod,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (m == null) return 1;
                int highestWon = (int)m.Invoke(saveData, null)!;
                int unlocked = highestWon + 2;
                if (unlocked < 1) unlocked = 1;
                if (unlocked > 4) unlocked = 4;
                return unlocked;
            }
            catch { return 1; }
        }

        protected override void OnUpdate()
        {
            PollPhaseChange();
            PollHowToPlay();
            PollRowJump();
        }

        // Detect Up/Down and teleport the cursor between the tableau
        // row and the free-cell row. Engine's own nav handles
        // horizontal motion within a row.
        private void PollRowJump()
        {
            if (ReadPhase() != 2) return;
            if (ReadHowToPlay()) return;

            bool up   = _kbUp.Pressed();
            bool down = _kbDown.Pressed();
            if (!up && !down) return;

            if (!TryReadCursor(out float cx, out float cy)) return;

            // Tableau Y ~ 1015–1400 (head card to deepest mid-stack);
            // free cells Y ~ 1539–1905. We don't hard-cap on these
            // bounds — instead we decide row by which one is closer.
            const float tableauCenterY  = 1207f; // mid of typical card stack range
            const float freeCellCenterY = 1722f; // matches log
            bool onTableau = Math.Abs(cy - tableauCenterY) < Math.Abs(cy - freeCellCenterY);

            if (onTableau) JumpToNearestFreeCell(cx);
            else           JumpToNearestTableauPile(cx);
        }

        private bool TryReadCursor(out float cx, out float cy)
        {
            cx = 0; cy = 0;
            if (_cursorPosMethod == null || _vec2XField == null || _vec2YField == null) return false;
            try
            {
                object pos = _cursorPosMethod.Invoke(null, null)!;
                cx = (float)_vec2XField.GetValue(pos)!;
                cy = (float)_vec2YField.GetValue(pos)!;
                return true;
            }
            catch { return false; }
        }

        private void JumpToNearestFreeCell(float cx)
        {
            if (ScreenInstance == null || _solGameStateField == null
                || _solFoundationArrayField == null || _setCursorMethod == null
                || _tVector2 == null || _itemPositionField == null
                || _vec2XField == null || _vec2YField == null) return;
            try
            {
                object? gs = _solGameStateField.GetValue(ScreenInstance);
                if (gs == null) return;
                if (!(_solFoundationArrayField.GetValue(gs) is Array cells) || cells.Length == 0) return;

                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int k = 0; k < cells.Length; k++)
                {
                    object? cell = cells.GetValue(k);
                    if (cell == null) continue;
                    object tail = GetTail(cell);
                    object? posObj = _itemPositionField.GetValue(tail);
                    if (posObj == null) continue;
                    float px = (float)_vec2XField.GetValue(posObj)!;
                    float centerX = px + CardOffsetX + CardW * 0.5f;
                    float dist = Math.Abs(centerX - cx);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx  = k;
                    }
                }
                if (bestIdx < 0) return;
                JumpCursorToTail((Array)cells, bestIdx, "free cell");
            }
            catch (Exception ex)
            {
                Log($"[SolitaireView] JumpToFreeCell error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void JumpToNearestTableauPile(float cx)
        {
            if (ScreenInstance == null || _solGameStateField == null
                || _solTableauArrayField == null || _setCursorMethod == null
                || _tVector2 == null || _itemPositionField == null
                || _vec2XField == null || _vec2YField == null) return;
            try
            {
                object? gs = _solGameStateField.GetValue(ScreenInstance);
                if (gs == null) return;
                if (!(_solTableauArrayField.GetValue(gs) is Array piles) || piles.Length == 0) return;

                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int j = 0; j < piles.Length; j++)
                {
                    object? pile = piles.GetValue(j);
                    if (pile == null) continue;
                    object tail = GetTail(pile);
                    object? posObj = _itemPositionField.GetValue(tail);
                    if (posObj == null) continue;
                    float px = (float)_vec2XField.GetValue(posObj)!;
                    float centerX = px + CardOffsetX + CardW * 0.5f;
                    float dist = Math.Abs(centerX - cx);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx  = j;
                    }
                }
                if (bestIdx < 0) return;
                JumpCursorToTail(piles, bestIdx, "pile");
            }
            catch (Exception ex)
            {
                Log($"[SolitaireView] JumpToTableau error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void JumpCursorToTail(Array piles, int idx, string label)
        {
            object? pile = piles.GetValue(idx);
            if (pile == null) return;
            object tail = GetTail(pile);
            object? posObj = _itemPositionField!.GetValue(tail);
            if (posObj == null) return;
            float px = (float)_vec2XField!.GetValue(posObj)!;
            float py = (float)_vec2YField!.GetValue(posObj)!;
            float cx = px + CardOffsetX + CardW * 0.5f;
            float cy = py + CardOffsetY + CardH * 0.5f;
            object center = Activator.CreateInstance(_tVector2!, new object[] { cx, cy })!;
            _setCursorMethod!.Invoke(null, new[] { center });
            Log($"[SolitaireView] row-jump → {label} {idx + 1} at ({cx:F0},{cy:F0})");
        }

        private void PollPhaseChange()
        {
            int phase = ReadPhase();
            if (phase == _lastStateEnum || phase < 0) return;
            int prev = _lastStateEnum;
            _lastStateEnum = phase;

            // Difficulty → game transition (player picked a difficulty).
            if (prev == 0 && phase != 0)
            {
                Speech.Speak(Loc.Get("solitaire.difficulty_selected"));
                Log($"[SolitaireView] phase 0 → {phase} (game starting).");
            }
            // Phase 2 is the first interactive frame (phase 1 is the deal
            // animation, during which the engine repositions the cursor
            // off the play area). Bootstrap the cursor onto pile 1 only
            // once gameplay actually begins, otherwise the engine
            // immediately discards our placement.
            if (prev != 2 && phase == 2)
            {
                BootstrapCursorOnFirstPile();
                Log($"[SolitaireView] phase {prev} → 2 (playing).");
            }
            // Won: the only "end" state in this variant. State 1/2 are
            // intermediate gameplay phases; no loss state exists, so we
            // don't announce anything else.
            if (phase == 3)
            {
                int score = ReadSolScore();
                Speech.Speak(Loc.Fmt("solitaire.you_won", score));
                Log($"[SolitaireView] won. Score={score}");
            }
        }

        // Without this, the cursor stays wherever it was when the
        // player tapped Solitaire on the home grid — typically off the
        // play area entirely (e.g. (0, 2400)) — and the engine's D-pad
        // nav has no current focus to translate from, so arrow keys
        // do nothing. Snap to the center of tableau pile 0's tail and
        // the engine takes over from there.
        private void BootstrapCursorOnFirstPile()
        {
            if (_setCursorMethod == null || _tVector2 == null
                || _solGameStateField == null || _solTableauArrayField == null
                || _itemPositionField == null
                || _vec2XField == null || _vec2YField == null) return;
            try
            {
                object? gs = _solGameStateField.GetValue(ScreenInstance);
                if (gs == null) return;
                if (!(_solTableauArrayField.GetValue(gs) is Array tableau)
                    || tableau.Length == 0) return;
                object? head = tableau.GetValue(0);
                if (head == null) return;
                object tail = GetTail(head);
                object? posObj = _itemPositionField.GetValue(tail);
                if (posObj == null) return;
                float px = (float)_vec2XField.GetValue(posObj)!;
                float py = (float)_vec2YField.GetValue(posObj)!;
                float cx = px + CardOffsetX + CardW * 0.5f;
                float cy = py + CardOffsetY + CardH * 0.5f;
                object center = Activator.CreateInstance(_tVector2, new object[] { cx, cy })!;
                _setCursorMethod.Invoke(null, new[] { center });
                Log($"[SolitaireView] bootstrapped cursor → ({cx:F0},{cy:F0}) on pile 1 tail.");
            }
            catch (Exception ex)
            {
                Log($"[SolitaireView] BootstrapCursor error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Player presses keyboard 2 (button 9 → FAKE_KEY_2) during
        // gameplay to flip bool_0 true → render HOW TO PLAY modal. Read
        // out its four paragraphs on the rising edge.
        private void PollHowToPlay()
        {
            bool now = ReadHowToPlay();
            if (now == _lastHowToPlay) return;
            _lastHowToPlay = now;
            if (now)
            {
                Speech.Speak(Loc.Get("solitaire.how_to_play"));
                foreach (string key in HowToPlayParagraphKeys) Speech.Speak(Loc.Get(key));
                Log("[SolitaireView] how-to-play opened.");
            }
            else
            {
                Log("[SolitaireView] how-to-play closed.");
            }
        }

        private bool ReadHowToPlay()
        {
            if (ScreenInstance == null || _solHowToPlayField == null) return false;
            try { return (bool)(_solHowToPlayField.GetValue(ScreenInstance) ?? false); }
            catch { return false; }
        }

        private int ReadPhase()
        {
            if (ScreenInstance == null || _solGameStateField == null) return -1;
            try
            {
                object? gs = _solGameStateField.GetValue(ScreenInstance);
                if (gs == null) return -1;
                return ReadSolStateEnum(gs);
            }
            catch { return -1; }
        }

        private int ReadSolScore()
        {
            if (ScreenInstance == null || _solGameStateField == null) return 0;
            try
            {
                object? gs = _solGameStateField.GetValue(ScreenInstance);
                if (gs == null) return 0;
                return ReadSolScore(gs);
            }
            catch { return 0; }
        }

        // Focus model: while difficulty-select is open, defer to the
        // standard cursor-on-hotspot reader (the four difficulty cards
        // and the side buttons are smethod_6 / smethod_7-registered with
        // their labels drawn nearby). Once the game is running, probe
        // each pile's tail by render position; cards are registered but
        // not labeled (sprite-only), so HotspotLabelCapture can't
        // recover pile identity for us — we have to walk the game
        // state. If pile probing finds nothing (cursor on the NEW GAME
        // / HOW TO PLAY / WIN COUNT side buttons), fall through to the
        // generic hotspot reader so those side controls announce too.
        protected override (object? key, string? label) ReadCurrentFocus()
        {
            if (ScreenInstance == null) return (null, null);

            // Phase 0 = SELECT DIFFICULTY modal. The tableau cards
            // exist behind it with valid render positions, so my probe
            // would spuriously announce "Tableau N" on any cursor
            // overlap. Defer entirely to the hotspot reader, which
            // sees only what the player can actually click — the four
            // difficulty cards plus the side buttons. Same applies
            // when the HOW TO PLAY modal is up.
            if (ReadPhase() == 0 || ReadHowToPlay())
            {
                return ReadFocusFromCursor();
            }

            if (_solGameStateField == null || _itemPositionField == null
                || _cursorPosMethod == null || _vec2XField == null || _vec2YField == null)
                return ReadFocusFromCursor();
            if (_solTableauArrayField == null && _solFoundationArrayField == null)
                return ReadFocusFromCursor();

            object? gs;
            try { gs = _solGameStateField.GetValue(ScreenInstance); }
            catch { return ReadFocusFromCursor(); }
            if (gs == null) return ReadFocusFromCursor();

            float mx, my;
            try
            {
                object mouse = _cursorPosMethod.Invoke(null, null)!;
                mx = (float)_vec2XField.GetValue(mouse)!;
                my = (float)_vec2YField.GetValue(mouse)!;
            }
            catch { return ReadFocusFromCursor(); }

            int     kind = -1, idx = -1;
            object? tail = null;

            if (_solTableauArrayField != null
                && _solTableauArrayField.GetValue(gs) is Array tableau)
                ProbePiles(tableau, 0, mx, my, ref kind, ref idx, ref tail);
            if (kind < 0 && _solFoundationArrayField != null
                && _solFoundationArrayField.GetValue(gs) is Array foundation)
                ProbePiles(foundation, 1, mx, my, ref kind, ref idx, ref tail);

            // Cursor isn't over any pile — the side buttons (NEW GAME,
            // HOW TO PLAY, WIN COUNT) are labeled hotspots, so fall
            // through to the cursor-on-hotspot reader for them.
            if (kind < 0) return ReadFocusFromCursor();

            string kindLabel = kind == 0 ? "Tableau" : "Foundation";
            FieldInfo pileField = (kind == 0 ? _solTableauArrayField : _solFoundationArrayField)!;
            Array  pileArr   = (Array)pileField.GetValue(gs)!;
            int count = WalkPileCount(pileArr.GetValue(idx));
            // Format: "top R, N cards, pile P." — top rank first because
            // that's the playable card; pile id last because it's the
            // least-changing field. The "foundation" row in this
            // variant is actually free cells (per the HOW TO PLAY
            // text: each holds one card; collecting 4-of-a-kind in
            // one collapses it into a locked stack).
            string pileLabel = kind == 0 ? "pile" : "free cell";
            string label;
            if (count == 0)
            {
                label = $"empty, {pileLabel} {idx + 1}.";
            }
            else
            {
                int rank = ReadCardRank(tail);
                string topPart = IsLockedStack(tail)
                    ? $"locked rank {rank}"
                    : $"top {rank}";
                label = $"{topPart}, {count} cards, {pileLabel} {idx + 1}.";
            }
            // Include pile contents in the key so that dropping a card
            // on a pile (count or top rank changes while focus stays)
            // triggers a re-announcement. Otherwise the focus key
            // dedups on identity and the player can't tell whether
            // their drop actually landed.
            int rankOrLocked = 0;
            if (count > 0)
            {
                rankOrLocked = ReadCardRank(tail);
                if (IsLockedStack(tail)) rankOrLocked += 1000;
            }
            object key = ("pile", kind * 100 + idx, count, rankOrLocked);
            return (key, label);
        }

        // ── Helpers ────────────────────────────────────────────────────

        private void ProbePiles(Array piles, int kind, float mx, float my,
            ref int outKind, ref int outIdx, ref object? outTail)
        {
            // Check the cursor against every card in each pile chain,
            // not just the tail. Mid-stack cards have their own
            // H=54-ish hotspots (one per card), and the head card has
            // an H=663-ish extended hotspot covering the stack-target
            // area above it. The tail alone covers only one of those
            // bands — navigating UP within a column lands on the head's
            // extended region and the probe would miss it. Walking
            // every card's stored vector2_0 lets us match whichever
            // hotspot the cursor actually settled on.
            for (int i = 0; i < piles.Length; i++)
            {
                object? pileBase = piles.GetValue(i);
                if (pileBase == null) continue;
                object? hit = HitTestChain(pileBase, mx, my);
                if (hit != null)
                {
                    outKind = kind;
                    outIdx  = i;
                    // Always announce the TAIL's contents (top playable
                    // card / locked-stack indicator), even if the
                    // cursor is on a mid-stack card — that's the
                    // useful info regardless of which card hotspot got
                    // focused.
                    outTail = GetTail(pileBase);
                    return;
                }
            }
        }

        // Walk parent→child chain, hit-test each card's bounds against
        // the cursor, return the first match or null. Bounds match the
        // engine's hotspot at SolitaireScreen.cs:615.
        private object? HitTestChain(object pileBase, float mx, float my)
        {
            if (_itemPositionField == null || _vec2XField == null || _vec2YField == null
                || _itemChildMaybeField == null || _maybeItemHasValue == null
                || _maybeItemGetValue == null) return null;

            object current = pileBase;
            // Cap walk to prevent runaway on malformed cycles.
            for (int safety = 0; safety < 60; safety++)
            {
                if (HitTestCard(current, mx, my)) return current;
                object? cm = _itemChildMaybeField.GetValue(current);
                if (cm == null) break;
                bool has;
                try { has = (bool)_maybeItemHasValue.Invoke(cm, null)!; } catch { break; }
                if (!has) break;
                object? next = _maybeItemGetValue.Invoke(cm, null);
                if (next == null) break;
                current = next;
            }
            return null;
        }

        private bool HitTestCard(object card, float mx, float my)
        {
            object? posObj = _itemPositionField!.GetValue(card);
            if (posObj == null) return false;
            try
            {
                float px = (float)_vec2XField!.GetValue(posObj)!;
                float py = (float)_vec2YField!.GetValue(posObj)!;
                float bx = px + CardOffsetX;
                float by = py + CardOffsetY;
                return mx >= bx && mx < bx + CardW && my >= by && my < by + CardH;
            }
            catch { return false; }
        }

        private object GetTail(object pileBase)
        {
            if (_itemGetTailMethod != null)
            {
                try { return _itemGetTailMethod.Invoke(pileBase, null) ?? pileBase; }
                catch { }
            }
            // Fallback: walk children manually if the engine's GetTail
            // throws for any reason.
            object current = pileBase;
            if (_itemChildMaybeField == null || _maybeItemHasValue == null
                || _maybeItemGetValue == null) return current;
            while (true)
            {
                object? cm = _itemChildMaybeField.GetValue(current);
                if (cm == null) break;
                bool has;
                try { has = (bool)_maybeItemHasValue.Invoke(cm, null)!; } catch { break; }
                if (!has) break;
                object? next = _maybeItemGetValue.Invoke(cm, null);
                if (next == null) break;
                current = next;
            }
            return current;
        }

        private int WalkPileCount(object? pileBase)
        {
            if (pileBase == null || _itemChildMaybeField == null
                || _maybeItemHasValue == null || _maybeItemGetValue == null) return 0;
            int count = 0;
            object current = pileBase;
            while (true)
            {
                object? cm = _itemChildMaybeField.GetValue(current);
                if (cm == null) break;
                bool has;
                try { has = (bool)_maybeItemHasValue.Invoke(cm, null)!; } catch { break; }
                if (!has) break;
                object? next = _maybeItemGetValue.Invoke(cm, null);
                if (next == null) break;
                count++;
                current = next;
            }
            return count;
        }

        private int ReadCardRank(object? card)
        {
            if (card == null || _itemRankField == null) return 0;
            try { return (int)(_itemRankField.GetValue(card) ?? 0); }
            catch { return 0; }
        }

        // SolitaireItem.bool_3 — the engine sets this true when a pile
        // of 4 matching cards collapses into a locked stack (the win
        // condition for that rank, GClass36's win-check at line 54
        // gates on this flag). V2 misnamed it "face up" — there's no
        // face-up/down state in this variant; all cards always show
        // their rank.
        private bool IsLockedStack(object? card)
        {
            if (card == null || _itemLockedStackField == null) return false;
            try { return (bool)(_itemLockedStackField.GetValue(card) ?? false); }
            catch { return false; }
        }

        private int ReadSolStateEnum(object gameState)
        {
            if (_solStateEnumField == null) return -1;
            try
            {
                object? v = _solStateEnumField.GetValue(gameState);
                if (v is Enum e) return Convert.ToInt32(e);
                if (v is int i)  return i;
            }
            catch { }
            return -1;
        }

        private int ReadSolScore(object gameState)
        {
            if (_solScoreField == null) return 0;
            try { return (int)(_solScoreField.GetValue(gameState) ?? 0); }
            catch { return 0; }
        }
    }
}
