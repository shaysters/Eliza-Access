using System.Collections.Generic;
using System.Linq;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Speaks short scene transitions as the picture changes:
    ///
    /// - New location → its label, then each character present
    ///   ("Nora, casual and confident").
    /// - A character fades in, or the script swaps one version of a
    ///   person for another (Darren → DarrenHappy, a location outfit) →
    ///   that version's short fade-in line.
    /// - A person leaves entirely → "{name} leaves."
    ///
    /// State comes from <see cref="SceneReader"/> (location + the
    /// on-screen character set), so this tracks exactly what the
    /// ChangeLocation / ShowOrHideCharacter script events do. Full
    /// descriptions are on demand via the J/K/L browser
    /// (<see cref="ImageBrowser"/>).
    ///
    /// State survives OnLeave (pause menu, phone) so returning to the
    /// same scene doesn't repeat it; a new WorldScreen (loading a game)
    /// gets a fresh sub and announces where you are.
    ///
    /// Registered before DialogueSubView so the scene is spoken ahead of
    /// the first line in it.
    /// </summary>
    internal sealed class SceneSubView : SubView
    {
        private string? _location;
        private bool    _hasBaseline;
        // Description key → spoken name, for what was on screen last tick.
        private Dictionary<string, string> _present = new Dictionary<string, string>();

        public override void OnUpdate()
        {
            string? location = SceneReader.LocationKey(Parent);
            if (location == null) return;
            List<SceneReader.Figure>? figures = SceneReader.Figures(Parent, location);
            if (figures == null) return;   // set mid-mutation; try next tick

            var now = new Dictionary<string, string>();
            foreach (var f in figures) now[f.Key] = SceneReader.FigureName(f);

            if (!_hasBaseline || location != _location)
            {
                _hasBaseline = true;
                _location = location;
                _present = now;
                string? label = SceneReader.LocationLabel(location);
                if (label != null) Speech.Speak(label);
                foreach (var f in figures) Speech.Speak(SceneReader.FigureFadeIn(f));
                Log($"[SceneSub] location {location}: {string.Join(", ", now.Keys)}");
                return;
            }

            if (now.Count == _present.Count && now.Keys.All(_present.ContainsKey)) return;

            // Arrivals and version swaps: any key not on screen before.
            foreach (var f in figures)
                if (!_present.ContainsKey(f.Key))
                    Speech.Speak(SceneReader.FigureFadeIn(f));

            // Departures: only when the person is gone in every version
            // (a swap to another version isn't someone leaving).
            var namesNow = new HashSet<string>(now.Values);
            foreach (var kv in _present)
                if (!now.ContainsKey(kv.Key) && !namesNow.Contains(kv.Value))
                {
                    Speech.Speak(Loc.Fmt("scene.leaves", kv.Value));
                    namesNow.Add(kv.Value);   // one "leaves" per person
                }

            Log($"[SceneSub] figures: {string.Join(", ", _present.Keys)} → {string.Join(", ", now.Keys)}");
            _present = now;
        }
    }
}
