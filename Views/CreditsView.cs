using System;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Reads the end-game credits screen aloud.
    ///
    /// <c>CreditsScreen</c> renders four static blocks scrolling slowly
    /// up the screen — team, cast, localization, and special thanks —
    /// across roughly a minute of animation, with a final note about
    /// the Hospitalfield residency at the end. There's no interactivity
    /// (Escape skips after a delay, but that's it), so the best a
    /// screen reader can do is dump the whole thing on entry: speech
    /// queues, so each Speak() call plays after the previous finishes,
    /// and the user hears the credits read in order while the visual
    /// scroll plays out at its own pace.
    ///
    /// Source data lives in static fields on <c>CreditsScreen</c>:
    ///   Tuple&lt;string,string&gt;[7]  — team (role, name)
    ///   Tuple&lt;string,string&gt;[14] — cast (character, actor)
    ///   Tuple&lt;string,string&gt;[9]  — localization (language, names)
    ///   string[14]                    — special thanks
    /// Reflection grabs all four by signature (the only static
    /// Tuple&lt;string,string&gt;[] and the only static string[] on the
    /// type), in declaration order, so the obfuscated field names
    /// don't matter.
    /// </summary>
    internal sealed class CreditsView : ScreenView
    {
        public override string ScreenTypeName => "CreditsScreen";

        private Tuple<string, string>[]? _team;
        private Tuple<string, string>[]? _cast;
        private Tuple<string, string>[]? _localization;
        private string[]?                _specialThanks;

        protected override void OnAdded(object screen)
        {
            Type tCredits = screen.GetType();
            const BindingFlags stat =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.DeclaredOnly;

            var tupleArrays = new List<Tuple<string, string>[]>();
            string[]? stringArray = null;

            foreach (FieldInfo f in tCredits.GetFields(stat))
            {
                object? val;
                try { val = f.GetValue(null); }
                catch { continue; }
                if (val is Tuple<string, string>[] ta) tupleArrays.Add(ta);
                else if (val is string[] sa && stringArray == null) stringArray = sa;
            }

            // Declaration order in source is team, cast, localization.
            // C# reflection preserves field-declaration order, so the
            // three tuple arrays come out in the same order without us
            // having to hardcode obfuscated field names.
            if (tupleArrays.Count >= 1) _team         = tupleArrays[0];
            if (tupleArrays.Count >= 2) _cast         = tupleArrays[1];
            if (tupleArrays.Count >= 3) _localization = tupleArrays[2];
            _specialThanks = stringArray;

            Log($"[CreditsView] added: team={_team?.Length ?? -1} " +
                $"cast={_cast?.Length ?? -1} loc={_localization?.Length ?? -1} " +
                $"thanks={_specialThanks?.Length ?? -1}");
        }

        protected override void OnFocus()
        {
            Speech.Speak("Credits.");
            SpeakTupleList(_team);
            Speech.Speak("Cast.");
            SpeakTupleList(_cast);
            Speech.Speak("Localization.");
            SpeakTupleList(_localization, joinNewlines: true);
            Speech.Speak("Special thanks.");
            if (_specialThanks != null)
            {
                foreach (string name in _specialThanks)
                {
                    if (!string.IsNullOrWhiteSpace(name)) Speech.Speak(name);
                }
            }
            Speech.Speak("Written in part at Hospitalfield Interdisciplinary Residency 2018, " +
                         "Arbroath, U K.");
        }

        private void SpeakTupleList(Tuple<string, string>[]? entries, bool joinNewlines = false)
        {
            if (entries == null) return;
            foreach (var t in entries)
            {
                if (t == null) continue;
                string label = t.Item1 ?? "";
                string body  = t.Item2 ?? "";
                if (joinNewlines) body = body.Replace('\n', ' ').Replace("  ", " ");
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(body)) continue;
                Speech.Speak($"{label}: {body}.");
            }
        }

        // Credits has no focusable controls — Escape skips the whole
        // screen but isn't a focused widget. Nothing for a focus poll.
        protected override (object? key, string? label) ReadCurrentFocus() => (null, null);
    }
}
