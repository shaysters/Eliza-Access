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

        // ── Per-activation state ───────────────────────────────────────
        private bool _contentAnnounced;
        private int  _pollsWaited;
        private int  _lastSideHotspotIdx = -1;
        // Give the engine a beat to populate list_0 after EndTherapy
        // becomes current, but don't wait forever (a session with no
        // achievements would leave the list empty). ~100 polls ≈ 0.5s.
        private const int ReadyDeadlinePolls = 100;

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
                $"phoneState={(_phoneStateField != null)} achievements={(_achievementsListField != null)} " +
                $"gc27Xp={(_gc27XpField != null)} gc27Name={(_gc27NameField != null)} " +
                $"tip={(_tipField != null)} rating={(_ratingField != null)}");
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

        // GClass27: a reference type with an int field (XP), a GClass23
        // field (localized name), and a Texture field (icon). We need the
        // int and the FIRST GClass23 (gclass23_0 — the drawn name;
        // gclass23_1 is a secondary string we don't read).
        private static bool LooksLikeAchievement(Type t, out FieldInfo? xp, out FieldInfo? name)
        {
            xp = null; name = null;
            if (t.IsValueType || t == typeof(string)) return false;
            bool hasTexture = false;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType == typeof(int) && xp == null) xp = f;
                else if (f.FieldType.Name == "GClass23" && name == null) name = f;
                else if (f.FieldType.Name == "Texture") hasTexture = true;
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
            _lastSideHotspotIdx = -1;

            // Interrupt anything in flight (queued narration / a stale
            // load-handler dialogue line on save-resume), then announce
            // the header. The content follows once list_0 populates.
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
                // Announce once list_0 has populated, or after the
                // deadline (covers a session with no achievements).
                bool ready = AchievementCount() > 0 || _pollsWaited >= ReadyDeadlinePolls;
                if (!ready) return;
                AnnounceResults();
                _contentAnnounced = true;
                // Seed the cursor tracker so we don't immediately read a
                // stale label the cursor happens to be resting on (the
                // in-session AR-console log keeps registering hotspots
                // behind the panel).
                _lastSideHotspotIdx = CursorFocus.FindHotspotIndex();
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

            // Tip: maybe_21 dollars or "No tip given."
            if (TryReadMaybeFloat(out float tip))
                Speech.Speak(Loc.Fmt("session.results.tip", tip.ToString("0.00")));
            else
                Speech.Speak(Loc.Get("session.results.no_tip"));

            Log($"[SessionResultsSub] announced: {achievements.Count} achievements, total {total} XP");
        }

        private int AchievementCount()
        {
            if (_phoneStateField == null || _achievementsListField == null) return 0;
            try
            {
                object? ps = _phoneStateField.GetValue(Parent);
                if (ps == null) return 0;
                return (_achievementsListField.GetValue(ps) is ICollection c) ? c.Count : 0;
            }
            catch { return 0; }
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
        {
            value = 0f;
            if (_tipField == null || _maybeFloatHasValue == null || _maybeFloatGetValue == null) return false;
            try
            {
                object? boxed = _tipField.GetValue(Parent);
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
