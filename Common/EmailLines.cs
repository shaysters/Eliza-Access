using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Builds the readable lines of an opened email — used by both the
    /// Mail app and the Eliza app's Skandha inbox, which draw message
    /// detail identically (MailApp.cs / ElizaApp.cs detail views):
    ///
    /// - Header block (sender, subject, date) occupies the first 275
    ///   units; it's line 0.
    /// - Body is split on '\n' and trimmed. Each entry then advances:
    ///     empty      → 21
    ///     "{key}"    → image height + 22 (nothing, if no texture)
    ///     text       → measured height + 6 (mail body font, scale 1.1,
    ///                  double-newline 0.4, wrap 1005)
    ///   Each non-empty entry becomes a line at its starting offset.
    ///
    /// Speech for body lines keeps the existing substitutions: image
    /// tokens read their hand-written description, ":emoji:" shortcodes
    /// lose their colons.
    /// </summary>
    internal static class EmailLines
    {
        private const float HeaderHeight   = 275f;
        private const float BlankAdvance   = 21f;
        private const float ImageGap       = 22f;
        private const float TextGap        = 6f;
        private const float BodyScale      = 1.1f;
        private const float BodyDoubleNl   = 0.4f;
        private const float BodyWrapWidth  = 1005f;

        public static List<LineReader.Line> Build(string sender, string date, string subject, string body)
        {
            var lines = new List<LineReader.Line>();
            string dateChunk = date.Length > 0 ? $" {date}." : "";
            // Matches the visible header: sender left, date right,
            // subject below. No "From" prefix — the UI has none.
            lines.Add(new LineReader.Line($"{sender}.{dateChunk} {subject}.", 0f));

            float y = HeaderHeight;
            foreach (string raw in body.Split('\n'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) { y += BlankAdvance; continue; }

                if (entry.StartsWith("{"))
                {
                    string key = entry.Replace("{", "").Replace("}", "").Trim();
                    if (key.Length > 0)
                    {
                        string desc = EmailImageDescriptions.TryGet(key) ?? key.Replace("_", " ");
                        lines.Add(new LineReader.Line(Loc.Fmt("image.desc", desc), y));
                    }
                    float? h = TextLayout.ImageHeight(key.ToLowerInvariant());
                    if (h.HasValue) y += h.Value + ImageGap;
                    continue;
                }

                // ":fire:"-style shortcodes are drawn as icons; strip the
                // colons so TTS reads the keyword, not "colon fire colon".
                string speech = entry;
                if (speech.StartsWith(":") && speech.EndsWith(":") && speech.Length > 2)
                    speech = speech.Substring(1, speech.Length - 2);
                lines.Add(new LineReader.Line(speech, y));

                float height = TextLayout.MeasureHeight(entry, TextLayout.Font.MailBody, 0,
                                   BodyScale, BodyDoubleNl, BodyWrapWidth) ?? 0f;
                y += height + TextGap;
            }
            return lines;
        }

        /// <summary>Queue every line — the on-open "read all".</summary>
        public static void SpeakAll(SpeechEngine speech, List<LineReader.Line> lines)
        {
            foreach (LineReader.Line l in lines) speech.Speak(l.Speech);
        }
    }
}
