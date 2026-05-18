using System;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Thin wrapper over <see cref="SpeechEngine"/> that adds:
    ///   - duplicate suppression — never speak the same text twice in a row
    ///   - one place to extend with entry/focus sequencing later
    ///
    /// Observers that currently keep their own <c>_lastAnnouncedLabel</c> /
    /// <c>_lastAnnouncedSlot</c> pair plus a manual <c>if (label != _last)</c>
    /// gate can use <see cref="SpeakIfChanged"/> instead. Each observer owns
    /// its own Announcer so cross-observer announcements (e.g. "Main menu" in
    /// Title vs "Pause menu" in Escape) do not deduplicate against each other.
    ///
    /// Foreground-window gating, backend fallback, and the <c>LastSpoken</c>
    /// tracking that <see cref="SpeechEngine.Speak"/> already provides remain
    /// in <see cref="SpeechEngine"/> and are not duplicated here.
    /// </summary>
    internal sealed class Announcer
    {
        private readonly SpeechEngine _speech;
        private string _lastSpoken = "";

        public Announcer(SpeechEngine speech)
        {
            _speech = speech;
        }

        /// <summary>The most recent string passed to any Speak call on this instance.</summary>
        public string LastSpoken => _lastSpoken;

        /// <summary>
        /// Speak <paramref name="text"/> only if it differs from the last text
        /// spoken via this instance. Returns true if speech actually fired.
        /// Empty / whitespace-only strings are ignored without updating state.
        /// </summary>
        public bool SpeakIfChanged(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text == _lastSpoken) return false;
            _lastSpoken = text;
            _speech.Speak(text, interrupt);
            return true;
        }

        /// <summary>
        /// Speak <paramref name="text"/> unconditionally, including if it
        /// matches the last spoken value. Use for events where the user
        /// explicitly re-requested the same announcement (R-key repeat,
        /// re-navigating into the same focus slot, etc.).
        /// Updates <see cref="LastSpoken"/>.
        /// </summary>
        public void SpeakForced(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _lastSpoken = text;
            _speech.Speak(text, interrupt);
        }

        /// <summary>
        /// Forget the last-spoken text so the next <see cref="SpeakIfChanged"/>
        /// will fire even if it matches what was previously spoken. Use on
        /// screen exit / re-entry boundaries.
        /// </summary>
        public void Reset()
        {
            _lastSpoken = "";
        }
    }
}
