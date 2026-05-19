using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Spoken descriptions for the embedded image tokens that appear
    /// in email bodies. The engine looks up these keys in
    /// <c>GClass127.dictionary_0</c> and renders the matching Texture
    /// inline; for the screen reader we substitute a verbal
    /// description of what the image depicts.
    ///
    /// Descriptions were authored by inspecting the dumped PNG
    /// outputs (F9 in-game triggers <see cref="TextureDumper"/>),
    /// so they reflect the actual visual content rather than just
    /// the token name. Most images are company logos that paired
    /// with each email's sender; HEDONIC is a full event flyer with
    /// time, place, lineup.
    /// </summary>
    internal static class EmailImageDescriptions
    {
        private static readonly Dictionary<string, string> _descriptions =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "activalet",  "Activalet logo" },
            { "aponia",     "Aponia logo, white text on a cyan rounded rectangle" },
            { "avalon",     "Avalon Fitness logo" },
            { "eliza",      "Eliza app logo, lowercase green text" },
            { "hedonic",    "Event flyer. People You May Know presents HEDONIC, " +
                            "with MASIC, LI'L SAPPHO, and CLONE WORLD featuring KAYO. " +
                            "January 17, Club Jouissance, 8 PM to 2 AM, 21 plus only" },
            { "horizontal", "Horizontal divider line" },
            { "limestone",  "Limestone PR logo, muted yellow text on a sandy background" },
            { "northwest",  "Northwest Voices logo" },
            { "pairup",     "PairUp logo with a butterfly icon" },
            { "palki",      "Palki logo, a stylized white P on a magenta square next to bold gray text" },
            { "redmond",    "The Redmond Post newspaper banner" },
            { "rosewater",  "Rosewater Hotel and Casino logo, pink text in a pink-bordered cream box" },
            { "skandha",    "Skandha logo, green text with a stacked-cube icon" },
            { "spencer",    "Spencer Spruce Gallery logo" },
            { "steward",    "My Health Steward logo" },
            { "viridian",   "Viridian University logo" },
            { "wellspring", "Wellspring Ventures logo" },
            { "westlake",   "Westlake Power and Light logo" },
        };

        /// <summary>
        /// Returns a verbal description of the embedded image with
        /// the given key, or null if the key is unknown. Unknown
        /// keys fall back to a generic "Image: {key}" announcement
        /// at the caller — better to surface the raw token than to
        /// silently swallow a new image type Zachtronics added.
        /// </summary>
        public static string? TryGet(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return _descriptions.TryGetValue(key, out string? d) ? d : null;
        }
    }
}
