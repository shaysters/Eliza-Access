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
    /// The "Skandha Wellness" intake / outro screen that runs at the
    /// start and end of every therapy session. Rendered directly by
    /// <c>WorldScreen.method_8</c> (NOT a phone app) when the current
    /// script event is <c>ShowElizaApp</c>.
    ///
    /// Two flows share the renderer:
    ///   Intake (IntroNotOutro = true): multi-step modal driven by the
    ///     <c>int_2</c> step counter.
    ///       0  Important Notice — medical disclaimer        [OK]
    ///       1  Important Notice — privacy / T&amp;Cs            [OK]
    ///       2  Personal info confirmation (name + age form)  [NEXT]
    ///       3  "Skandha Wellness account not found"          [NEXT]
    ///       4–10  PHQ-7 questions (one per step)             [YES] [NO]
    ///       11 "All done!" / thanks                          [DONE]
    ///       12 leaves the screen
    ///   Outro (IntroNotOutro = false): single-page rating + tip flow.
    ///
    /// All buttons are registered as hotspots via
    /// <c>VirtualInput.smethod_6</c> (which delegates to
    /// <c>smethod_7</c> — what HotspotLabelCapture patches), so the
    /// label-capture pair-up works. But WorldScreenView's
    /// ReadCurrentFocus returns (null, null) by design — focus tracking
    /// lives inside the subs that need it. So this sub polls the cursor
    /// itself and speaks the focused-button label on change, mirroring
    /// SessionResultsSubView's side-hotspot tracking pattern.
    /// </summary>
    internal sealed class WellnessIntakeSubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        // WorldScreen.int_2 — pre-session step counter (0..12).
        private const string StepCounterField        = "#=qLiYh_JyVLMUMVyHdkPr$qw==";
        // WorldScreen.method_0() → current ScriptEvent (GClass10).
        private const string ScriptEventMethod       = "#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=";
        // GClass10.method_25() → bool, is this event a ShowElizaApp.
        private const string IsShowElizaAppMethod    = "#=q66wGW8bX1eJblqmtQvZv6Q==";
        // GClass10.method_61() → GStruct8 carrying IntroNotOutro.
        private const string ScriptEventStructMethod = "#=qzvMZ1g6Zmj65Hinxd$3FdA==";
        // GStruct8.bool_0 — true = intake (pre-session), false = outro.
        private const string IntroNotOutroField      = "#=qCOwOWSGvwQXGhYcFuLvWTQ==";

        // GClass146 (static script-globals) — the player character lives
        // here, used by step 2's name-confirmation form.
        private const string ScriptGlobalsType       = "#=qUorh3bf3QXO$XxEBRj6$yIR8KAwd6emPaiKysEv1CLs=";
        private const string PlayerCharField         = "#=qgz5Q1RTXWFwDloYkUt1$4w==";

        // ScriptCharacter name fields + LocalizedText.ToString.
        private const string CharFirstNameField      = "#=qVcZOuQei28m8v7C2MtsdYA==";
        private const string CharLastNameField       = "#=q0XfuGfoz9CcWkzQ03CuAxw==";
        private const string LocalizedToStringMethod = "#=qTivbHO_rGrDAcTGXa1E4rg==";

        // ── Reflected handles ──────────────────────────────────────────
        private FieldInfo?  _stepField;
        private MethodInfo? _scriptEventMethod;
        private MethodInfo? _isShowElizaAppMethod;
        private MethodInfo? _scriptEventStructMethod;
        private FieldInfo?  _introNotOutroField;
        private FieldInfo?  _playerCharField;
        private FieldInfo?  _charFirstField;
        private FieldInfo?  _charLastField;
        private MethodInfo? _localizedToString;

        // ── Per-activation state ───────────────────────────────────────
        private int     _lastStep              = -1;
        private bool    _phqSurveyContextSpoken;
        private bool    _outroSpoken;
        private string? _lastFocusLabel;

        public override void OnInit()
        {
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            const BindingFlags stat =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

            Type tWS = Parent.GetType();

            _stepField = tWS.GetField(StepCounterField, inst);

            // method_0 returns GClass10 (script event). Match by name —
            // signature is parameterless so it's unambiguous.
            foreach (MethodInfo m in tWS.GetMethods(inst))
            {
                if (m.Name == ScriptEventMethod && m.GetParameters().Length == 0)
                {
                    _scriptEventMethod = m;
                    break;
                }
            }

            if (_scriptEventMethod != null)
            {
                Type tEvent = _scriptEventMethod.ReturnType;
                _isShowElizaAppMethod   = tEvent.GetMethod(IsShowElizaAppMethod, inst,
                    null, Type.EmptyTypes, null);
                _scriptEventStructMethod = tEvent.GetMethod(ScriptEventStructMethod, inst,
                    null, Type.EmptyTypes, null);
                if (_scriptEventStructMethod != null)
                {
                    Type tStruct = _scriptEventStructMethod.ReturnType;
                    _introNotOutroField = tStruct.GetField(IntroNotOutroField, inst);
                }
            }

            Type? tGlobals = Nagoya.FindType(ScriptGlobalsType);
            if (tGlobals != null)
            {
                _playerCharField = tGlobals.GetField(PlayerCharField, stat);
            }

            Type? tChar = Nagoya.FindType("ScriptCharacter");
            if (tChar != null)
            {
                _charFirstField = tChar.GetField(CharFirstNameField, inst);
                _charLastField  = tChar.GetField(CharLastNameField,  inst);
                if (_charFirstField != null)
                {
                    _localizedToString = _charFirstField.FieldType.GetMethod(
                        LocalizedToStringMethod,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                }
            }

            Log($"[WellnessSub] init: step={(_stepField != null)} " +
                $"scriptEvent={(_scriptEventMethod != null)} " +
                $"isShowElizaApp={(_isShowElizaAppMethod != null)} " +
                $"introNotOutro={(_introNotOutroField != null)} " +
                $"playerChar={(_playerCharField != null)} " +
                $"first={(_charFirstField != null)} last={(_charLastField != null)} " +
                $"toString={(_localizedToString != null)}");
        }

        public override bool IsActive => IsShowElizaAppEvent();

        private bool IsShowElizaAppEvent()
        {
            if (_scriptEventMethod == null || _isShowElizaAppMethod == null) return false;
            try
            {
                object? ev = _scriptEventMethod.Invoke(Parent, null);
                if (ev == null) return false;
                return (bool)_isShowElizaAppMethod.Invoke(ev, null)!;
            }
            catch { return false; }
        }

        private bool? IsIntake()
        {
            if (_scriptEventMethod == null || _scriptEventStructMethod == null
                || _introNotOutroField == null) return null;
            try
            {
                object? ev = _scriptEventMethod.Invoke(Parent, null);
                if (ev == null) return null;
                object? str = _scriptEventStructMethod.Invoke(ev, null);
                if (str == null) return null;
                return (bool)_introNotOutroField.GetValue(str)!;
            }
            catch { return null; }
        }

        public override void OnEnter()
        {
            _lastStep = -1;
            _phqSurveyContextSpoken = false;
            _outroSpoken = false;
            _lastFocusLabel = null;
            bool? intake = IsIntake();
            Log($"[WellnessSub] entered. intake={(intake?.ToString() ?? "<unknown>")}");
        }

        public override void OnLeave()
        {
            Log("[WellnessSub] left.");
        }

        public override void OnUpdate()
        {
            bool? intake = IsIntake();
            if (intake == true)
            {
                PollIntake();
            }
            else if (intake == false)
            {
                PollOutro();
            }
            PollFocus();
        }

        // The screen registers each button as a hotspot via
        // VirtualInput.smethod_6 (which delegates to smethod_7 — what
        // HotspotLabelCapture patches), so by the time the player has
        // moved focus we can read the cursor's hotspot index and look
        // up the label that was paired with it this frame.
        //
        // Dedup by label, not by index: VirtualInput.list_0 is rebuilt
        // every frame, so a hotspot's index isn't stable across frames
        // (each screen-render registers the buttons in whatever order
        // the layout code emits — and on this screen with multiple
        // buttons that order can shift). Index-based dedup re-speaks
        // every poll. Label-based dedup is what we actually care about:
        // the player only needs to know when the focused control's
        // identity changes, not when the engine reshuffles its list.
        private void PollFocus()
        {
            // Preserve last-spoken state when the cursor is between
            // hotspots OR the hotspot has no captured label (yet).
            // Engines tend to rebuild list_0 each frame; on any frame
            // where the rebuild lands after our poll OR the text-pair
            // hasn't been seen yet (smethod_41 runs after smethod_7 in
            // some draw orders), we'd otherwise reset the tracker and
            // re-announce on the next valid poll. Same pattern
            // ExplorationSubView uses for its side-hotspot tracker.
            int idx = CursorFocus.FindHotspotIndex();
            if (idx < 0) return;
            string? label = HotspotLabelCapture.GetLabel(idx);
            if (string.IsNullOrEmpty(label))
            {
                // Outro star buttons (5 sprite-only hotspots at the
                // same Y) have no text drawn near them, so capture
                // never pairs a label. Synthesize one from the
                // hotspot's row position.
                label = SynthesizeUnlabeledHotspot(idx);
            }
            if (string.IsNullOrEmpty(label)) return;
            if (string.Equals(label, _lastFocusLabel, StringComparison.Ordinal)) return;
            _lastFocusLabel = label;
            // Every interactive hotspot on this screen is a plain
            // click-to-advance button (Yes / No, the star ratings,
            // tip amounts, DONE). Format via ButtonElement so they
            // all announce as "{label}, button" — matches every
            // other clickable surface in the mod.
            Speech.Speak(new ButtonElement(label!).ResolveFocusText());
            Log($"[WellnessSub] focus → [{idx}] \"{label}\"");
        }

        // ── Hotspot-geometry reflection (for star buttons) ─────────────

        private bool        _hotspotGeomResolved;
        private FieldInfo?  _hotspotList0Field;
        private FieldInfo?  _hotspotBoundsField;
        private FieldInfo?  _boundsMinField;
        private FieldInfo?  _boundsMaxField;
        private FieldInfo?  _vec2XField;
        private FieldInfo?  _vec2YField;

        private void EnsureHotspotGeomResolved()
        {
            if (_hotspotGeomResolved) return;
            _hotspotGeomResolved = true;

            Type? tVI = Nagoya.FindType("VirtualInput");
            if (tVI != null)
            {
                foreach (FieldInfo f in tVI.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(List<>)) continue;
                    Type elem = f.FieldType.GetGenericArguments()[0];
                    if (elem.Name == "VirtualInputHotspot") { _hotspotList0Field = f; break; }
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
            Type? tBounds = Nagoya.FindType("Bounds2");
            if (tBounds != null)
            {
                _boundsMinField = tBounds.GetField("Min", BindingFlags.Public | BindingFlags.Instance);
                _boundsMaxField = tBounds.GetField("Max", BindingFlags.Public | BindingFlags.Instance);
            }
            Type? tVec = Nagoya.FindType("Vector2");
            if (tVec != null)
            {
                // Same trick CursorFocus uses for the cursor accessor:
                // de4dot's clean decompile widens visibility, but the
                // live DLL keeps these private. Try both.
                foreach (string vis in new[] { "public", "any" })
                {
                    BindingFlags flags = (vis == "public")
                        ? (BindingFlags.Public | BindingFlags.Instance)
                        : (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (FieldInfo f in tVec.GetFields(flags))
                    {
                        if (f.FieldType != typeof(float)) continue;
                        if (_vec2XField == null) _vec2XField = f;
                        else if (_vec2YField == null) { _vec2YField = f; break; }
                    }
                    if (_vec2XField != null && _vec2YField != null) break;
                    // reset between attempts
                    if (_vec2YField == null) _vec2XField = null;
                }
            }
        }

        // If the focused hotspot at index `idx` is one of 5 same-Y
        // unlabeled hotspots (the rating-stars row in the outro), return
        // "X of 5 stars" with the appropriate position. Else null.
        private string? SynthesizeUnlabeledHotspot(int idx)
        {
            EnsureHotspotGeomResolved();
            if (_hotspotList0Field == null || _hotspotBoundsField == null
                || _boundsMinField == null || _boundsMaxField == null
                || _vec2XField == null || _vec2YField == null)
                return null;

            try
            {
                IList? list = _hotspotList0Field.GetValue(null) as IList;
                if (list == null || idx < 0 || idx >= list.Count) return null;

                if (!TryGetHotspotCenter(list[idx], out float focusedX, out float focusedY))
                    return null;

                // Collect other unlabeled hotspots at the same Y. 4 px
                // tolerance covers float jitter without bleeding into
                // adjacent UI rows.
                var sameRow = new List<(int Idx, float X)>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (!TryGetHotspotCenter(list[i], out float x, out float y)) continue;
                    if (Math.Abs(y - focusedY) > 4f) continue;
                    string? lab = HotspotLabelCapture.GetLabel(i);
                    if (!string.IsNullOrEmpty(lab)) continue; // labeled = not a star
                    sameRow.Add((i, x));
                }
                if (sameRow.Count != 5) return null;
                sameRow.Sort((a, b) => a.X.CompareTo(b.X));
                int pos = sameRow.FindIndex(t => t.Idx == idx);
                if (pos < 0) return null;
                // Rating reads right-to-left: the rightmost star in the
                // row is "1 of 5", leftmost is "5 of 5". Sort gives us
                // pos=0 for leftmost; flip so the announced number
                // matches the rating direction the player expects.
                int n = 5 - pos;
                return $"{n} of 5 stars";
            }
            catch { return null; }
        }

        private bool TryGetHotspotCenter(object? hotspot, out float cx, out float cy)
        {
            cx = cy = 0f;
            if (hotspot == null) return false;
            object? bounds = _hotspotBoundsField!.GetValue(hotspot);
            if (bounds == null) return false;
            object? min = _boundsMinField!.GetValue(bounds);
            object? max = _boundsMaxField!.GetValue(bounds);
            if (min == null || max == null) return false;
            float minX = (float)_vec2XField!.GetValue(min)!;
            float minY = (float)_vec2YField!.GetValue(min)!;
            float maxX = (float)_vec2XField!.GetValue(max)!;
            float maxY = (float)_vec2YField!.GetValue(max)!;
            cx = 0.5f * (minX + maxX);
            cy = 0.5f * (minY + maxY);
            return true;
        }

        // ── Intake (multi-step modal) ──────────────────────────────────

        private void PollIntake()
        {
            if (_stepField == null) return;
            int step;
            try { step = (int)_stepField.GetValue(Parent)!; }
            catch { return; }

            // int_2 reaches 12 when the player presses DONE on step 11
            // and the screen is tearing down — we'll exit via IsActive
            // going false on the next poll, no need to read anything.
            if (step < 0 || step > 11) return;
            if (step == _lastStep) return;
            _lastStep = step;
            SpeakIntakeStep(step);
        }

        private void SpeakIntakeStep(int step)
        {
            string text;
            switch (step)
            {
                case 0:
                    text = "Important Notice. " +
                           "Eliza is not intended to be a substitute for professional medical advice, " +
                           "diagnosis, or treatment. If you believe you are experiencing serious " +
                           "mental health issues, seek care from a licensed mental health provider.";
                    break;
                case 1:
                    text = "Important Notice. " +
                           "Protecting the privacy and security of your information is a priority for " +
                           "everyone at Skandha. You have agreed to the Skandha Wellness Terms and " +
                           "Conditions, which governs how we use and share your data.";
                    break;
                case 2:
                    text = $"Please confirm your personal information. " +
                           $"First name: {ReadPlayerFirstName()}. " +
                           $"Last name: {ReadPlayerLastName()}. " +
                           $"Age: 34.";
                    break;
                case 3:
                    text = "Skandha Wellness account not found. " +
                           "A linked Skandha Wellness account was not found. You can link your Skandha " +
                           "Wellness account later at the Skandha Support Portal.";
                    break;
                case 4: text = PhqIntro() + "Question 1 of 7. I have been feeling down, depressed, or hopeless."; break;
                case 5: text = "Question 2 of 7. I have been feeling tired or having little energy.";       break;
                case 6: text = "Question 3 of 7. I have experienced trouble concentrating on things.";     break;
                case 7: text = "Question 4 of 7. I have been feeling nervous, anxious, or on edge.";       break;
                case 8: text = "Question 5 of 7. I have been having trouble relaxing.";                    break;
                case 9: text = "Question 6 of 7. I have been easily annoyed or irritable.";                break;
                case 10: text = "Question 7 of 7. I have been afraid, as if something terrible might happen at any moment."; break;
                case 11:
                    text = "All done! " +
                           "Thank you for registering with the Eliza, your counseling partner. " +
                           "We hope you enjoy your Eliza experience.";
                    break;
                default: return;
            }
            Speech.Speak(text);
            Log($"[WellnessSub] step {step}: \"{text}\"");
        }

        // The seven-question survey context is the small disclaimer
        // panel the engine draws under the question card. It's redundant
        // by question 2+, so speak it only once (on the first PHQ step).
        private string PhqIntro()
        {
            if (_phqSurveyContextSpoken) return "";
            _phqSurveyContextSpoken = true;
            return "In order for us to get started, please help Eliza understand how you've been " +
                   "feeling over the last week with this brief seven-question survey. ";
        }

        private string ReadPlayerFirstName() => ReadPlayerNameField(_charFirstField);
        private string ReadPlayerLastName()  => ReadPlayerNameField(_charLastField);

        private string ReadPlayerNameField(FieldInfo? nameField)
        {
            if (_playerCharField == null || nameField == null || _localizedToString == null)
                return "";
            try
            {
                object? player = _playerCharField.GetValue(null);
                if (player == null) return "";
                object? loc = nameField.GetValue(player);
                if (loc == null) return "";
                return (string)_localizedToString.Invoke(loc, null)! ?? "";
            }
            catch { return ""; }
        }

        // ── Outro (single-page rating + tip) ───────────────────────────

        private void PollOutro()
        {
            // Speak the framing once on entry. The five star buttons
            // and the four tip buttons are all hotspot-registered, so
            // focus changes ("1 star", "2 stars", "$5", "$10", "DONE")
            // are announced by HotspotLabelCapture without our help.
            if (_outroSpoken) return;
            _outroSpoken = true;
            string text =
                "Session complete. We hope you enjoyed your Eliza experience. " +
                "Please rate your experience with Eliza today. Total fee: forty-five dollars. Then add a tip.";
            Speech.Speak(text);
            Log($"[WellnessSub] outro: \"{text}\"");
        }
    }
}
