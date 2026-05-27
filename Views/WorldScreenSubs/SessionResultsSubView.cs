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
    /// Reads the post-session "Session Results" overlay (XP / achievements,
    /// client rating, client tip, Continue button) drawn over WorldScreen
    /// after a therapy session ends.
    ///
    /// Rather than scraping the drawn text (which animates — the tip
    /// counts up from $0.00, the XP bar fills, the stars pop in — so a
    /// snapshot catches mid-animation values), this reads the panel's
    /// backing state directly and reports the final, exact values with
    /// proper labels:
    /// - Achievements: <c>PhoneState.list_0</c> — a List&lt;GClass27&gt;,
    ///   one per award. Each has <c>int_0</c> (XP) and <c>gclass23_0</c>
    ///   (localized display name). Total XP = the sum.
    /// - Rating: <c>WorldScreen.maybe_22</c> (Maybe&lt;int&gt;, 0-5).
    /// - Tip: <c>WorldScreen.maybe_21</c> (Maybe&lt;float&gt;, dollars).
    /// Both maybe fields are set by the AwardRating / AwardTip script
    /// steps that fire just before EndTherapy.
    ///
    /// Activation: the EndTherapy script step
    /// (<c>GClass10.Enum0.EndTherapy</c>). We find the EndTherapy cast
    /// method via the 36-Action visitor (<c>method_72</c>) and treat a
    /// successful invoke as the active signal — same as before.
    ///
    /// The "Continue" button is a real hotspot (WorldScreen.cs:2257), so
    /// the side-hotspot tracker announces it when the cursor lands on it.
    /// </summary>
    internal sealed class SessionResultsSubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        // WorldScreen.phoneState_0 — same field TextingOverlaySubView uses.
        private const string PhoneStateFieldName = "#=qzY9KTcekY7cB9h8RuKFjKw==";

        // ── Activation (EndTherapy cast) ───────────────────────────────
        private MethodInfo? _currentStepMethod;
        private MethodInfo? _endTherapyCastMethod;

        // ── Results-content reflection (OnInit) ────────────────────────
        private FieldInfo?  _phoneStateField;       // WorldScreen → PhoneState
        private FieldInfo?  _achievementsListField; // PhoneState.list_0 (List<GClass27>)
        private FieldInfo?  _gc27XpField;           // GClass27.int_0
        private FieldInfo?  _gc27NameField;         // GClass27.gclass23_0 (localized)

        private FieldInfo?  _tipField;              // WorldScreen.maybe_21 (Maybe<float>)
        private FieldInfo?  _ratingField;           // WorldScreen.maybe_22 (Maybe<int>)
        private MethodInfo? _maybeFloatHasValue, _maybeFloatGetValue;
        private MethodInfo? _maybeIntHasValue, _maybeIntGetValue;

        // ── Content-stability gate ─────────────────────────────────────
        // The panel's content (maybe_21 tip, maybe_22 rating, list_0
        // achievements) is set by award script steps; the reveal then
        // animates it in. Rather than try to detect the animation's
        // timer chain (fragile — depends on catching a no-value
        // baseline before the engine's first render frame sets it), we
        // just read the real fields each poll and announce once the
        // composite content has stopped changing for StableThresholdPolls.
        // Robust whether the data is ready at entry or fills in over the
        // reveal; a minimum wait avoids announcing on the first poll
        // before population, and a hard deadline is the backstop.
        private int    _pollsWaited;
        private int    _pollsSinceChange;
        private string _lastSig = "";
        private bool   _tipOrRatingSeen;
        private const int MinWaitPolls         = 40;    // ~200ms before earliest announce
        private const int StableThresholdPolls = 100;   // ~500ms unchanged → settled
        // achievements populate early but tip/rating arrive later in the
        // reveal; don't announce until they've shown up (or this cap is
        // hit, for a genuine no-tip / no-rating session).
        private const int ExtendedWaitPolls    = 700;   // ~3.5s
        private const int HardDeadlinePolls    = 1200;  // ~6s backstop
        // The signature when nothing is populated — see BuildContentSignature.
        private const string EmptySig = "a0:x0:r-:t-";

        // ── Per-activation state ───────────────────────────────────────
        private bool _contentAnnounced;
        private int  _lastSideHotspotIdx = -1;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // currentStep accessor — instance no-arg method whose return
            // type has 30+ bool no-arg discriminators (GClass10's 36-case
            // tagged union). Same approach as ExplorationSubView.
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
            if (tGClass10 != null) ResolveEndTherapyCast(tGClass10);

            ResolveContentFields(tWS, inst);

            Log($"[SessionResultsSub] init: currentStep={(_currentStepMethod != null)} " +
                $"endTherapyCast={(_endTherapyCastMethod != null)} " +
                $"phoneState={(_phoneStateField != null)} " +
                $"achievements={_achievementsListField?.Name ?? "<null>"} " +
                $"gc27Xp={_gc27XpField?.Name ?? "<null>"} gc27Name={_gc27NameField?.Name ?? "<null>"} " +
                $"tip={_tipField?.Name ?? "<null>"} rating={_ratingField?.Name ?? "<null>"}");
        }

        // ── Content-field resolution ───────────────────────────────────

        private void ResolveContentFields(Type tWS, BindingFlags inst)
        {
            // PhoneState + its achievement list.
            _phoneStateField = tWS.GetField(PhoneStateFieldName, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                foreach (FieldInfo f in tPhoneState.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(List<>)) continue;
                    Type elem = f.FieldType.GetGenericArguments()[0];
                    if (!LooksLikeAchievement(elem, out FieldInfo? xp, out FieldInfo? name)) continue;
                    _achievementsListField = f;
                    _gc27XpField   = xp;
                    _gc27NameField = name;
                    break;
                }
            }

            // maybe_21 (tip) / maybe_22 (rating): declared immediately
            // after maybe_20 (the patient, the LAST Maybe<ScriptCharacter>
            // on WorldScreen). Walk fields in metadata-token (declaration)
            // order: the first Maybe<float> after the last
            // Maybe<ScriptCharacter> is the tip; the first Maybe<int>
            // after it is the rating. Structural anchor avoids relying on
            // value heuristics.
            Type? tScriptChar = Nagoya.FindType("ScriptCharacter");
            var fields = new List<FieldInfo>(tWS.GetFields(inst));
            fields.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

            int patientIdx = -1;
            if (tScriptChar != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    Type ft = fields[i].FieldType;
                    if (ft.IsGenericType && ft.GetGenericArguments().Length == 1
                        && ft.GetGenericArguments()[0] == tScriptChar)
                        patientIdx = i;   // keep the LAST one
                }
            }

            if (patientIdx >= 0)
            {
                for (int i = patientIdx + 1; i < fields.Count; i++)
                {
                    Type ft = fields[i].FieldType;
                    if (!ft.IsGenericType || ft.GetGenericArguments().Length != 1) continue;
                    Type arg = ft.GetGenericArguments()[0];
                    if (_tipField == null && arg == typeof(float))
                    {
                        _tipField = fields[i];
                        ResolveMaybeMethods(ft, out _maybeFloatHasValue, out _maybeFloatGetValue);
                    }
                    else if (_ratingField == null && arg == typeof(int))
                    {
                        _ratingField = fields[i];
                        ResolveMaybeMethods(ft, out _maybeIntHasValue, out _maybeIntGetValue);
                    }
                    if (_tipField != null && _ratingField != null) break;
                }
            }
        }

        // GClass27: a reference type with an int field (XP), two
        // localized-text fields (gclass23_0/1), and two Texture fields
        // (icons). We want int_0 and the FIRST localized-text field
        // (gclass23_0 — the drawn name). The localized-text type's name
        // ("GClass23") does NOT survive obfuscation, so we can't match
        // it by name — instead the name field is identified as the
        // first reference-typed field that is neither a string nor a
        // Texture. "Texture" IS a preserved runtime name, so the icon
        // fields are excluded reliably.
        private static bool LooksLikeAchievement(Type t, out FieldInfo? xp, out FieldInfo? name)
        {
            xp = null; name = null;
            if (t.IsValueType || t == typeof(string)) return false;
            bool hasTexture = false;
            var fields = new List<FieldInfo>(t.GetFields(BindingFlags.Public | BindingFlags.Instance));
            fields.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            foreach (FieldInfo f in fields)
            {
                Type ft = f.FieldType;
                if (ft == typeof(int)) { if (xp == null) xp = f; }
                else if (ft.Name == "Texture") hasTexture = true;
                else if (!ft.IsValueType && ft != typeof(string) && name == null) name = f;
            }
            return xp != null && name != null && hasTexture;
        }

        private static void ResolveMaybeMethods(Type maybeType, out MethodInfo? hasValue, out MethodInfo? getValue)
        {
            hasValue = null; getValue = null;
            Type arg = maybeType.GetGenericArguments()[0];
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool)) hasValue = m;
                else if (m.ReturnType == arg)     getValue = m;
            }
        }

        // ── Activation ─────────────────────────────────────────────────

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
            // its dialogue-line speech while we own the screen, so a
            // stray Eliza line doesn't play right after the results.
            SessionResultsState.IsActive = true;
            _contentAnnounced  = false;
            _pollsWaited       = 0;
            _pollsSinceChange  = 0;
            _lastSig           = "";
            _tipOrRatingSeen   = false;
            _lastSideHotspotIdx = -1;

            // Interrupt anything in flight (queued narration / a stale
            // load-handler dialogue line on save-resume), then announce
            // the header. The content follows once it stops changing.
            Speech.Speak(Loc.Get("session.results_title"), interrupt: true);
            Log("[SessionResultsSub] enter");
        }

        public override void OnLeave()
        {
            SessionResultsState.IsActive = false;
            _contentAnnounced = false;
            _lastSideHotspotIdx = -1;
        }

        public override void OnUpdate()
        {
            if (!_contentAnnounced)
            {
                _pollsWaited++;

                // Composite signature of everything we'd announce. When
                // it stops changing, the reveal has finished populating
                // the fields and they hold their final values.
                string sig = BuildContentSignature();
                if (sig != _lastSig) { _lastSig = sig; _pollsSinceChange = 0; }
                else                 { _pollsSinceChange++; }

                // Track whether tip/rating have populated yet. They
                // arrive later in the reveal than the achievements, so
                // achievements going stable is NOT "done."
                bool tipNow    = TryReadMaybeFloat(out _);
                bool ratingNow = TryReadMaybeInt(out _);
                if (tipNow || ratingNow) _tipOrRatingSeen = true;

                // Periodic trace of the population timeline — invaluable
                // for confirming when each field becomes ready.
                if (_pollsWaited % 20 == 0)
                    Log($"[SessionResultsSub] waiting: sig=[{sig}] stableFor={_pollsSinceChange} " +
                        $"waited={_pollsWaited} tipNow={tipNow} ratingNow={ratingNow}");

                // Announce once the content is populated (non-empty) AND
                // stable, AND tip/rating have shown up — or, for a
                // session that genuinely gives neither, after the
                // extended cap. Hard deadline is the final backstop.
                bool nonEmpty = sig != EmptySig;
                bool stable   = nonEmpty && _pollsWaited >= MinWaitPolls
                                         && _pollsSinceChange >= StableThresholdPolls;
                bool revealDone = _tipOrRatingSeen || _pollsWaited >= ExtendedWaitPolls;
                bool deadline = _pollsWaited >= HardDeadlinePolls;
                if (!(stable && revealDone) && !deadline) return;

                AnnounceResults();
                _contentAnnounced = true;
                // Seed the cursor tracker so we don't immediately read a
                // stale label the cursor happens to be resting on (the
                // in-session AR-console log keeps registering hotspots
                // behind the panel).
                _lastSideHotspotIdx = CursorFocus.FindHotspotIndex();
                Log($"[SessionResultsSub] announce: stable={stable} deadline={deadline} " +
                    $"waited={_pollsWaited} sig=[{_lastSig}]");
                return;
            }

            // Continue button — the one labeled hotspot the overlay
            // registers. Announce via ButtonElement when the cursor
            // lands on it (label comes from HotspotLabelCapture, the
            // game's own localized "Continue").
            int idx = CursorFocus.FindHotspotIndex();
            if (idx < 0 || idx == _lastSideHotspotIdx) return;
            _lastSideHotspotIdx = idx;
            string? label = HotspotLabelCapture.GetLabel(idx);
            if (string.IsNullOrEmpty(label)) return;
            Speech.Speak(new ButtonElement(label!).ResolveFocusText());
            Log($"[SessionResultsSub] focus → [{idx}] \"{label}\"");
        }

        // ── Content readout ────────────────────────────────────────────

        private void AnnounceResults()
        {
            var achievements = ReadAchievements();
            int total = 0;
            foreach (var (name, xp) in achievements)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    Speech.Speak(Loc.Fmt("session.results.achievement", name, xp));
                total += xp;
            }
            if (achievements.Count > 0)
                Speech.Speak(Loc.Fmt("session.results.total_xp", total));

            // Rating: maybe_22 (0-5) or "No rating given."
            if (TryReadMaybeInt(out int rating) && rating >= 0 && rating <= 5)
                Speech.Speak(Loc.Fmt("session.results.rating", rating));
            else
                Speech.Speak(Loc.Get("session.results.no_rating"));

            // Tip: maybe_21 holds the dollar amount when a tip was
            // given; no-value means none (the panel's $0.00 is just
            // its method_4(0f) default). Announce the real amount when
            // present, "No tip given" otherwise — parallel to the
            // rating's "No rating given".
            if (TryReadMaybeFloat(out float tip))
                Speech.Speak(Loc.Fmt("session.results.tip", tip.ToString("0.00")));
            else
                Speech.Speak(Loc.Get("session.results.no_tip"));

            Log($"[SessionResultsSub] announced: {achievements.Count} achievements, total {total} XP");
        }

        // Snapshot of the announceable content as a compact string, so
        // we can tell when it stops changing. Achievement count + total
        // XP + rating/tip states; if any field is unresolved it
        // contributes a constant, so the signature still settles.
        private string BuildContentSignature()
        {
            var achievements = ReadAchievements();
            int total = 0;
            foreach (var (_, xp) in achievements) total += xp;
            string rating = TryReadMaybeInt(out int r) ? r.ToString() : "-";
            string tip    = TryReadMaybeFloat(out float t) ? t.ToString("0.00") : "-";
            return $"a{achievements.Count}:x{total}:r{rating}:t{tip}";
        }

        private List<(string Name, int Xp)> ReadAchievements()
        {
            var result = new List<(string, int)>();
            if (_phoneStateField == null || _achievementsListField == null
                || _gc27XpField == null || _gc27NameField == null) return result;
            try
            {
                object? ps = _phoneStateField.GetValue(Parent);
                if (ps == null) return result;
                if (!(_achievementsListField.GetValue(ps) is IEnumerable list)) return result;
                foreach (object? entry in list)
                {
                    if (entry == null) continue;
                    int xp = 0;
                    string name = "";
                    try { xp = (int)_gc27XpField.GetValue(entry)!; } catch { }
                    try { name = _gc27NameField.GetValue(entry)?.ToString() ?? ""; } catch { }
                    result.Add((name, xp));
                }
            }
            catch (Exception ex)
            {
                Log($"[SessionResultsSub] ReadAchievements error: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        private bool TryReadMaybeInt(out int value)
        {
            value = 0;
            if (_ratingField == null || _maybeIntHasValue == null || _maybeIntGetValue == null) return false;
            try
            {
                object? boxed = _ratingField.GetValue(Parent);
                if (boxed == null) return false;
                if (!(bool)_maybeIntHasValue.Invoke(boxed, null)!) return false;
                value = (int)_maybeIntGetValue.Invoke(boxed, null)!;
                return true;
            }
            catch { return false; }
        }

        private bool TryReadMaybeFloat(out float value)
            => TryReadMaybeFloatField(_tipField, out value);

        private bool TryReadMaybeFloatField(FieldInfo? f, out float value)
        {
            value = 0f;
            if (f == null || _maybeFloatHasValue == null || _maybeFloatGetValue == null) return false;
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

        // ── EndTherapy cast resolution (visitor lookup) ────────────────

        private void ResolveEndTherapyCast(Type tGClass10)
        {
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // GClass10.method_72 takes 36 Action<T> params, one per
            // tagged-union case in declaration order. Param 17 →
            // Action<EndTherapy payload>.
            MethodInfo? visitor = null;
            foreach (MethodInfo m in tGClass10.GetMethods(inst))
            {
                if (m.ReturnType != typeof(void) || m.IsSpecialName) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 36) continue;
                bool allActions = true;
                foreach (ParameterInfo p in ps)
                {
                    Type pt = p.ParameterType;
                    if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Action<>))
                    { allActions = false; break; }
                }
                if (allActions) { visitor = m; break; }
            }
            if (visitor == null) { Log("[SessionResultsSub] visitor method_72 not found"); return; }

            Type tEndTherapyPayload = visitor.GetParameters()[17].ParameterType.GetGenericArguments()[0];

            foreach (MethodInfo m in tGClass10.GetMethods(inst))
            {
                if (m.GetParameters().Length != 0 || m.IsSpecialName) continue;
                if (m.ReturnType != tEndTherapyPayload) continue;
                _endTherapyCastMethod = m;
                break;
            }
            Log($"[SessionResultsSub] endTherapyPayload={tEndTherapyPayload.Name} " +
                $"cast={_endTherapyCastMethod?.Name ?? "<null>"}");
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
    }
}
