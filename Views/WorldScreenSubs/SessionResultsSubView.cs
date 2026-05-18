using System;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Reads the post-session "Session Results" overlay (header, client
    /// rating, client tip, Continue button) drawn over WorldScreen after a
    /// therapy session ends. The overlay is gated by the engine's
    /// EndTherapy script step (<c>GClass10.Enum0.EndTherapy</c>, index 17
    /// in the tagged union), so we use the cast method for that case as
    /// the activation signal.
    ///
    /// Detection — visitor-pattern lookup:
    /// <c>GClass10.method_72</c> is a void method that takes 36
    /// <c>Action&lt;TStructN&gt;</c> parameters, one per tagged-union case
    /// in declaration order. Parameter 17 is <c>Action&lt;TEndTherapy&gt;</c>,
    /// where <c>TEndTherapy</c> is the EndTherapy payload struct
    /// (<c>GStruct9</c> in the clean decompile). We find the visitor by
    /// signature (36 generic Action params), read its parameter-17 generic
    /// argument to learn the EndTherapy payload type, then locate the
    /// instance no-arg method on GClass10 that returns that type — that's
    /// the EndTherapy cast (<c>method_53</c>). Try-invoking the cast
    /// succeeds only while the current step is EndTherapy.
    ///
    /// Content — <c>maybe_21</c> (tip, Maybe&lt;float&gt;) and
    /// <c>maybe_22</c> (rating, Maybe&lt;int&gt;) on WorldScreen are set by
    /// the AwardTip / AwardRating script steps that fire just before
    /// EndTherapy, so they hold the displayed values for the duration of
    /// the overlay. Their obfuscated field names aren't known offline, so
    /// we identify them at runtime on first activation: scan Maybe&lt;int&gt;
    /// fields for one whose value is in [0..5] (the rating range), and
    /// Maybe&lt;float&gt; fields for the one whose value stays constant
    /// across polls (animation timer <c>maybe_27</c> increments; tip
    /// doesn't). Once identified, both fields are cached for the session.
    ///
    /// The "Continue" button is registered as a real hotspot during the
    /// overlay (WorldScreen.cs:2257 <c>VirtualInput.smethod_6</c>), so the
    /// same side-hotspot tracker pattern ExplorationSubView uses for its
    /// Continue button works here too.
    /// </summary>
    internal sealed class SessionResultsSubView : SubView
    {
        // ── WorldScreen-level reflection (OnInit) ──────────────────────
        private MethodInfo? _currentStepMethod;

        // ── GClass10-level reflection (OnInit) ─────────────────────────
        private MethodInfo? _endTherapyCastMethod;

        // ── WorldScreen Maybe<int>/Maybe<float> field catalogs ─────────
        // Built in OnInit; specific tip/rating fields identified on first
        // ── Per-activation state ───────────────────────────────────────
        private int _lastSideHotspotIdx = -1;

        // Animation-stability tracking via the panel's own timers.
        // WorldScreen has many Maybe<float> fields (cursor smoothing,
        // ambient world timers, etc.) that change continuously and would
        // never let us detect stability. But the EndTherapy panel's
        // timer chain (maybe_24 → maybe_25 → maybe_26 → maybe_27 →
        // maybe_23) is special: every one is no-value before EndTherapy
        // starts, then gets set to 0 and increments during the panel
        // display, then sits at its final value or resets to no-value
        // on cleanup. We identify them dynamically by watching for
        // Maybe<float> fields that transition from no-value → HasValue
        // after our IsActive begins. Those, and only those, are the
        // panel timers — and when NONE of them have an increment across
        // ~300ms, every phase (slide-in, stars, tip, achievements) is
        // complete.
        private readonly List<FieldInfo> _maybeFloatFields = new List<FieldInfo>();
        private MethodInfo? _maybeFloatHasValue;
        private MethodInfo? _maybeFloatGetValue;

        // Maybe<int> catalog for the rating-value read. maybe_22 (the
        // rating) is set by the AwardRating script step that runs just
        // before EndTherapy, so on a normal session-end it has value
        // 0-5 throughout the post-session screen; we find it by that
        // range. On save resume with no rating given, no Maybe<int>
        // has a value in 0-5 and we fall back to whatever "No Rating
        // Given" text the engine renders.
        private readonly List<FieldInfo> _maybeIntFields = new List<FieldInfo>();
        private MethodInfo? _maybeIntHasValue;
        private MethodInfo? _maybeIntGetValue;

        // Fields that were no-value at OnEnter time and have since
        // transitioned to HasValue — those are the panel timers.
        private readonly HashSet<FieldInfo> _initiallyNoValue = new HashSet<FieldInfo>();
        private readonly HashSet<FieldInfo> _panelTimerFields = new HashSet<FieldInfo>();
        // Last observed value per panel timer, used to detect increments.
        private readonly Dictionary<FieldInfo, float> _lastTimerValues = new Dictionary<FieldInfo, float>();
        // Polls since the last timer increment. When this reaches a
        // threshold (~60 polls ≈ 300ms), no animation is active.
        private int _pollsSinceLastIncrement;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // currentStep accessor — instance no-arg method whose return
            // type has 30+ bool no-arg discriminators (GClass10's 36-case
            // tagged union shape). Same approach as ExplorationSubView.
            Type? tGClass10 = null;
            foreach (MethodInfo m in tWS.GetMethods(inst))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (rt.IsValueType || rt == typeof(string)) continue;
                if (CountBoolNoArgMethods(rt) < 30) continue;
                _currentStepMethod = m;
                tGClass10 = rt;
                break;
            }

            if (tGClass10 != null)
                ResolveEndTherapyCast(tGClass10);

            // Catalog Maybe<float> for animation-stability and Maybe<int>
            // for the rating-value read.
            foreach (FieldInfo f in tWS.GetFields(inst))
            {
                Type ft = f.FieldType;
                if (!ft.IsGenericType) continue;
                Type[] args = ft.GetGenericArguments();
                if (args.Length != 1) continue;
                if (args[0] == typeof(float))
                {
                    _maybeFloatFields.Add(f);
                    if (_maybeFloatHasValue == null) ResolveMaybeFloatMethods(ft);
                }
                else if (args[0] == typeof(int))
                {
                    _maybeIntFields.Add(f);
                    if (_maybeIntHasValue == null) ResolveMaybeIntMethods(ft);
                }
            }

            Log($"[SessionResultsSub] init: currentStep={(_currentStepMethod != null)} " +
                $"endTherapyCast={(_endTherapyCastMethod != null)} " +
                $"maybeFloatFields={_maybeFloatFields.Count} " +
                $"maybeIntFields={_maybeIntFields.Count}");
        }

        private void ResolveMaybeIntMethods(Type maybeType)
        {
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool))     _maybeIntHasValue = m;
                else if (m.ReturnType == typeof(int)) _maybeIntGetValue = m;
            }
        }

        private void ResolveMaybeFloatMethods(Type maybeType)
        {
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool))   _maybeFloatHasValue = m;
                else if (m.ReturnType == typeof(float)) _maybeFloatGetValue = m;
            }
        }

        // Visitor-pattern lookup for the EndTherapy cast.
        // GClass10.method_72 takes 36 Action<T> params in declaration order
        // matching Enum0. Param 17 → Action<EndTherapy payload type>.
        // Find the cast method on GClass10 that returns that payload type
        // AND throws on the wrong tagged-union case (validating method_53,
        // not a compiler-generated default-value helper that happens to
        // return the same struct shape).
        private void ResolveEndTherapyCast(Type tGClass10)
        {
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            MethodInfo? visitor = null;
            foreach (MethodInfo m in tGClass10.GetMethods(inst))
            {
                if (m.ReturnType != typeof(void)) continue;
                if (m.IsSpecialName) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 36) continue;
                bool allActions = true;
                foreach (ParameterInfo p in ps)
                {
                    Type pt = p.ParameterType;
                    if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Action<>))
                    { allActions = false; break; }
                }
                if (!allActions) continue;
                visitor = m;
                break;
            }
            if (visitor == null)
            {
                Log("[SessionResultsSub] visitor method_72 not found");
                return;
            }

            // Diagnostic: log all 36 visitor param generic types, so we
            // can verify that the runtime parameter order matches the
            // source-declaration order (Eazfuscator shouldn't reorder
            // method parameters, but log it to confirm).
            ParameterInfo[] vps = visitor.GetParameters();
            for (int i = 0; i < vps.Length; i++)
            {
                Type tA = vps[i].ParameterType;
                Type tT = tA.IsGenericType ? tA.GetGenericArguments()[0] : tA;
                Log($"[SessionResultsSub]   visitor[{i}]: Action<{tT.Name}>");
            }

            Type tEndTherapyPayload = vps[17].ParameterType.GetGenericArguments()[0];

            // The visitor's parameter at index 17 is Action<EndTherapy
            // payload>, so the no-arg instance method on GClass10 that
            // returns that payload type is method_53 (the EndTherapy cast).
            // Per source there is exactly one such method; IL inspection
            // confirms it validates enum0_0 against the EndTherapy enum
            // value and throws GException0 on the wrong case. We trust the
            // visitor lookup directly without probe validation — probing
            // at init can't distinguish "wrong method" from "current step
            // happens to be EndTherapy right now" (which is exactly what
            // happens when the player resumed from a save parked on the
            // post-session screen).
            foreach (MethodInfo m in tGClass10.GetMethods(inst))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.IsSpecialName) continue;          // exclude get_/set_ helpers
                if (m.ReturnType != tEndTherapyPayload) continue;
                _endTherapyCastMethod = m;
                break;
            }

            Log($"[SessionResultsSub] visitor={visitor.Name} endTherapyPayload={tEndTherapyPayload.Name} " +
                $"endTherapyCast={_endTherapyCastMethod?.Name ?? "<null>"}");
        }

        public override bool IsActive
        {
            get
            {
                if (_currentStepMethod == null || _endTherapyCastMethod == null) return false;
                object? step;
                try { step = _currentStepMethod.Invoke(Parent, null); }
                catch { return false; }
                if (step == null) return false;
                try { return _endTherapyCastMethod.Invoke(step, null) != null; }
                catch { return false; }
            }
        }

        public override void OnEnter()
        {
            // Cross-observer gate: DialogueSubView reads this and skips
            // its dialogue-line speech path while we own the screen.
            // Without this, the in-session dialogue field doesn't clear
            // on EndTherapy and DialogueSub speaks a stray Eliza line
            // right after "Session Results."
            SessionResultsState.IsActive = true;

            _lastSideHotspotIdx = -1;
            _panelDumpDone = false;
            _initiallyNoValue.Clear();
            _panelTimerFields.Clear();
            _lastTimerValues.Clear();
            _pollsSinceLastIncrement = 0;

            // Snapshot which Maybe<float> fields are no-value right now.
            // Any of those that later transition to HasValue is a panel
            // timer we should watch for the animation-done signal.
            if (_maybeFloatHasValue != null)
            {
                foreach (FieldInfo f in _maybeFloatFields)
                {
                    try
                    {
                        object? boxed = f.GetValue(Parent);
                        if (boxed == null) continue;
                        if (!(bool)_maybeFloatHasValue.Invoke(boxed, null)!)
                            _initiallyNoValue.Add(f);
                    }
                    catch { }
                }
            }
            Log($"[SessionResultsSub] enter: announced \"Session Results.\" ({_initiallyNoValue.Count} Maybe<float> fields baseline no-value)");

            // Interrupt anything in flight. On a save-resume parked on
            // the post-session screen, there may be queued narration or
            // a stale Eliza dialogue line from the load handler that
            // would otherwise play right after "Session Results." The
            // results overlay owns the screen at this point, so cutting
            // off whatever was speaking matches the visual state.
            Speech.Speak("Session Results.", interrupt: true);
            Log("[SessionResultsSub] enter: announced \"Session Results.\"");
        }

        // Once the panel finishes animating in (signalled by the Continue
        // button text being drawn — it's the last thing the engine draws
        // and it only appears when bool_14 = true), dump every text the
        // engine is currently drawing. Read it through verbatim so the
        // player hears the panel content the same way a sighted player
        // sees it, without us having to know which Maybe field holds
        // what or how to format each piece.
        private bool _panelDumpDone;

        public override void OnUpdate()
        {
            // Identify panel timers as they activate: any Maybe<float>
            // that was no-value at OnEnter but is now HasValue is part
            // of the EndTherapy animation chain. Watch ONLY those for
            // the "animation done" signal — other WorldScreen Maybe<float>
            // fields change continuously for unrelated reasons.
            if (!_panelDumpDone && _maybeFloatHasValue != null && _maybeFloatGetValue != null)
            {
                bool anyIncrementedThisPoll = false;

                foreach (FieldInfo f in _initiallyNoValue)
                {
                    if (!TryReadMaybeFloat(f, out float v)) continue;

                    // First time we've seen this field HasValue — record
                    // as a panel timer and seed its last-value baseline.
                    if (_panelTimerFields.Add(f))
                    {
                        _lastTimerValues[f] = v;
                        anyIncrementedThisPoll = true;   // transition itself is activity
                        continue;
                    }

                    // Subsequent polls — was this timer incremented?
                    if (_lastTimerValues.TryGetValue(f, out float prev) && v > prev + 0.0001f)
                    {
                        anyIncrementedThisPoll = true;
                        _lastTimerValues[f] = v;
                    }
                }

                if (anyIncrementedThisPoll) _pollsSinceLastIncrement = 0;
                else                        _pollsSinceLastIncrement++;

                // ~60 polls at the main-loop's ~5ms cadence ≈ 300ms.
                // Long enough that we're past any inter-phase gap in the
                // timer chain (slide-in → stars → tip → achievements).
                // Also require at least one panel timer to have been
                // observed — otherwise we'd dump at OnEnter+0 before any
                // panel state has shown up.
                if (_panelTimerFields.Count > 0 && _pollsSinceLastIncrement >= 60)
                {
                    _panelDumpDone = true;
                    var headerPos = HotspotLabelCapture.LastDrawnPosition("Session Results", 0.5);
                    if (headerPos != null)
                    {
                        DumpPanelTexts(headerPos.Value.X, headerPos.Value.Y);
                    }
                    else
                    {
                        Log("[SessionResultsSub] animation settled but no Session Results header drawn — skipping dump");
                    }

                    // Pre-seed _lastSideHotspotIdx with whatever the
                    // cursor is currently on so the focus tracker
                    // below doesn't immediately speak a stale label.
                    // The in-session AR-console session log keeps
                    // registering hotspots (with labels like
                    // "Proxy Response: Sentiment analyzer: …") behind
                    // the results panel; the cursor sits wherever the
                    // user left it during the session, often on one of
                    // those. Only deliberate cursor movement (e.g. to
                    // the Continue button) should speak.
                    _lastSideHotspotIdx = CursorFocus.FindHotspotIndex();

                    Log($"[SessionResultsSub] dump triggered: {_panelTimerFields.Count} panel timers observed, last increment was {_pollsSinceLastIncrement} polls ago, cursor seeded to hotspot [{_lastSideHotspotIdx}]");
                }
            }

            // Continue button is the only labeled hotspot the overlay
            // registers. Same side-hotspot tracking pattern ExplorationSub
            // uses — pick up the label off HotspotLabelCapture when the
            // cursor moves onto it.
            //
            // Gated on _panelDumpDone: during the panel slide-in the
            // hotspot table is still in flux from the previous (in-
            // session) frame, and HotspotLabelCapture pairs whatever
            // text was last drawn with whatever hotspot is at each
            // index. Right after "Session Results" we'd otherwise
            // speak something like "Proxy Response: Sentiment analyzer:
            // terrible [neg]" — a stale session label paired with the
            // first hotspot at that instant. Once the dump fires the
            // panel is settled and the labels are valid.
            if (!_panelDumpDone) return;

            int hotspotIdx = CursorFocus.FindHotspotIndex();
            if (hotspotIdx < 0) return;
            if (hotspotIdx == _lastSideHotspotIdx) return;
            string? label = HotspotLabelCapture.GetLabel(hotspotIdx);
            if (string.IsNullOrEmpty(label)) return;
            _lastSideHotspotIdx = hotspotIdx;
            Speech.Speak(label!);
            Log($"[SessionResultsSub] focus → [{hotspotIdx}] \"{label}\"");
        }

        private void DumpPanelTexts(float headerX, float headerY)
        {
            // The post-session panel renders its content as a cluster
            // around the "Session Results" header. The header sits at
            // the top of the panel; everything else is within ~1000 X
            // and ~900 Y below it. We scope the snapshot to that box so
            // we ignore the AR-console session log on the far left
            // (X≈10), the in-session vitals panel that animates off the
            // right edge (X>3500), and any unrelated UI elsewhere.
            const float xPad      = 1000f;
            const float yPadAbove =  100f;
            const float yPadBelow =  900f;

            var items = HotspotLabelCapture.SnapshotRecentTextsInBoundsWithPosition(
                headerX - xPad, headerX + xPad,
                headerY - yPadBelow, headerY + yPadAbove,
                0.25);

            // Sort top-to-bottom (Y descending — higher Y is higher on
            // screen in this coordinate system, header at Y≈1710 is the
            // top of the panel and Continue at Y≈854 is the bottom),
            // then left-to-right within a row (X ascending).
            Array.Sort(items, (a, b) =>
            {
                int cy = b.Y.CompareTo(a.Y);
                if (cy != 0) return cy;
                return a.X.CompareTo(b.X);
            });

            // Group adjacent items on the same Y row (±20px tolerance)
            // with a small POSITIVE X gap (<100px) — those are single
            // strings the engine drew character-by-character, e.g.
            // "$0.00" split into "$", "0", ".", "0", "0" at incrementing
            // X. The X check must require a strictly-positive delta,
            // otherwise crossing into a new visual row (which has a
            // slightly different Y but resets to the panel's left) gets
            // mis-grouped into the previous buffer because the delta is
            // a large NEGATIVE number that still passes "<= 100".
            const float rowTolerance = 20f;
            const float charGapLimit = 100f;

            var groups = new List<(string Text, float X, float Y)>();
            string buffer = "";
            float bufferX = float.NaN;
            float bufferY = float.NaN;
            float lastX   = float.NaN;
            float rowY    = float.NaN;

            void Flush()
            {
                if (!string.IsNullOrWhiteSpace(buffer))
                    groups.Add((buffer, bufferX, bufferY));
                buffer = "";
            }

            foreach (var it in items)
            {
                if (string.IsNullOrWhiteSpace(it.Text)) continue;
                if (it.Text == "Session Results") continue;  // already announced in OnEnter

                bool sameRow = !float.IsNaN(rowY) && Math.Abs(it.Y - rowY) <= rowTolerance;
                bool closeX  = !float.IsNaN(lastX) && (it.X - lastX) > 0 && (it.X - lastX) <= charGapLimit;

                if (sameRow && closeX)
                {
                    buffer += it.Text;
                }
                else
                {
                    Flush();
                    buffer = it.Text;
                    bufferX = it.X;
                    bufferY = it.Y;
                    rowY = it.Y;
                }
                lastX = it.X;
            }
            Flush();

            // ── Build the announcement list ────────────────────────
            // Two specific transforms; otherwise speak everything in
            // reading order:
            //   1. Suppress noisy achievement "+N" pop-ups. The panel
            //      keeps drawing each achievement's individual XP value
            //      ("+100", "+30", running totals like "+230") all over
            //      the place. They're not useful to read out — the
            //      player just wants the final total. We keep only the
            //      top-row "+N" (the XP total at the progress bar).
            //   2. Read the rating value directly from the game's
            //      Maybe<int> rating field. The panel shows stars as
            //      texture draws (not text), so a pure text-dump can't
            //      announce a star count. Falls back to whatever text
            //      label the engine drew (e.g. "No Rating Given").
            int topXpIdx = FindTopXpGroup(groups);
            int continueIdx = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Text == "Continue") { continueIdx = i; break; }
            }

            int? rating = TryReadRating();

            var announcements = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i == continueIdx) continue;
                string t = groups[i].Text;
                if (IsPlusN(t) && i != topXpIdx) continue;   // suppress achievement values
                announcements.Add(t);
            }

            if (rating.HasValue)            announcements.Add($"Client Rating: {rating.Value} of 5 stars.");
            if (continueIdx >= 0)           announcements.Add("Continue button.");

            foreach (string a in announcements) Speech.Speak(a);

            Log($"[SessionResultsSub] panel dump: {announcements.Count} announcements from " +
                $"{groups.Count} groups / {items.Length} drawn texts " +
                $"(rating={(rating.HasValue ? rating.Value.ToString() : "<none>")}, " +
                $"topXp={(topXpIdx >= 0 ? groups[topXpIdx].Text : "<none>")})");
            // Diagnostic — exactly what got grouped and what got spoken.
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                Log($"[SessionResultsSub]   group[{i}] Y={g.Y:F0} X={g.X:F0}: \"{g.Text}\"");
            }
            for (int i = 0; i < announcements.Count; i++)
            {
                Log($"[SessionResultsSub]   speak[{i}]: \"{announcements[i]}\"");
            }
        }

        // Returns the index of the highest-Y "+N" group, which is the
        // XP-total value next to the progress bar at the top of the
        // panel. Returns -1 if no "+N" group exists.
        private static int FindTopXpGroup(List<(string Text, float X, float Y)> groups)
        {
            int bestIdx = -1;
            float bestY = float.MinValue;
            for (int i = 0; i < groups.Count; i++)
            {
                if (!IsPlusN(groups[i].Text)) continue;
                if (groups[i].Y > bestY) { bestY = groups[i].Y; bestIdx = i; }
            }
            return bestIdx;
        }

        private static bool IsPlusN(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '+') return false;
            return IsPositiveInteger(s.Substring(1));
        }

        // Read the rating from maybe_22 — the Maybe<int> field on
        // WorldScreen that holds the just-awarded rating (0-5). We
        // don't know its obfuscated name so we scan every Maybe<int>
        // field looking for one currently HasValue with value in
        // [0..5]. Reliable during a normal session-end; returns null
        // when no Maybe<int> is in range (e.g. save resumed at a
        // "No Rating Given" panel — that text shows up in the dump
        // anyway via the verbatim path).
        private int? TryReadRating()
        {
            if (_maybeIntHasValue == null || _maybeIntGetValue == null) return null;
            foreach (FieldInfo f in _maybeIntFields)
            {
                try
                {
                    object? boxed = f.GetValue(Parent);
                    if (boxed == null) continue;
                    if (!(bool)_maybeIntHasValue.Invoke(boxed, null)!) continue;
                    int v = (int)_maybeIntGetValue.Invoke(boxed, null)!;
                    if (v >= 0 && v <= 5) return v;
                }
                catch { }
            }
            return null;
        }

        private static bool IsPositiveInteger(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        public override void OnLeave()
        {
            SessionResultsState.IsActive = false;
            _lastSideHotspotIdx = -1;
            _panelDumpDone = false;
        }

        private bool TryReadMaybeFloat(FieldInfo f, out float value)
        {
            value = float.NaN;
            if (_maybeFloatHasValue == null || _maybeFloatGetValue == null) return false;
            try
            {
                object? boxed = f.GetValue(Parent);
                if (boxed == null) return false;
                if (!(bool)_maybeFloatHasValue.Invoke(boxed, null)!) return false;
                value = (float)_maybeFloatGetValue.Invoke(boxed, null)!;
                return true;
            }
            catch { return false; }
        }

        // ── Static helpers ─────────────────────────────────────────────

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
    }
}
