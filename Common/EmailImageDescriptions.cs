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
            { "activalet",  "Activalet logo. The word Activalet in a rounded sans-serif, " +
                            "the first half black and the second half teal, with a small " +
                            "leaf-like flourish curling off the top." },
            { "aponia",     "Aponia logo. The word aponia in lowercase, thin rounded white " +
                            "letters on a bright sky-blue rounded rectangle, with a small " +
                            "registered-trademark R in the corner." },
            { "avalon",     "Avalon Fitness logo. AVALON in tall bold black capitals framed " +
                            "by thin vertical lines, with FITNESS in lighter widely-spaced " +
                            "capitals beneath. Black on white, no icon." },
            { "eliza",      "Eliza app logo. The word eliza in lowercase, rounded minimal " +
                            "green letters, centered with lots of empty space around it." },
            { "hedonic",    "A nightclub event flyer. Light grey background with jagged gold " +
                            "and black geometric shards forming a mountain or flame shape " +
                            "across the bottom. Black text reads: People You May Know " +
                            "present, HEDONIC. The lineup is MASIC, LI'L SAPPHO, and " +
                            "CLONE WORLD featuring KAYO. January 17, Club Jouissance, " +
                            "8 PM to 2 AM, 21 plus only." },
            { "horizontal", "A thin grey horizontal divider line." },
            { "limestone",  "Limestone PR logo. The words Limestone PR in bold muted-gold " +
                            "capitals over a photo of pale sand scattered with small dark " +
                            "pebbles, like a beach seen from above." },
            { "northwest",  "Northwest Voices logo. northwest in lowercase rounded black " +
                            "sans-serif run together with Voices in a black italic serif, " +
                            "all one word. Monochrome, no icon." },
            { "pairup",     "PairUp logo. A stylized butterfly drawn in pink and red curving " +
                            "strokes, beside the word PairUp in bold black with a capital " +
                            "P and capital U." },
            { "palki",      "Palki logo. A magenta square bearing a white letter P whose " +
                            "tail trails off like a flame or flag, next to PALKI in bold " +
                            "dark-grey capitals on a light grey background." },
            { "redmond",    "The Redmond Post newspaper masthead. The words The and Post in " +
                            "black serif capitals, with Redmond between them in red serif, " +
                            "classic newspaper-banner styling." },
            { "rosewater",  "Rosewater Hotel and Casino logo. ROSEWATER in bold pink " +
                            "capitals above HOTEL & CASINO in spaced pink capitals, inside " +
                            "a thin pink border on a pale cream background." },
            { "skandha",    "Skandha logo. SKANDHA in green letter-spaced capitals beside an " +
                            "icon of three green isometric cubes stacked like blocks." },
            { "spencer",    "Spencer Spruce Gallery logo. Spencer Spruce in bold near-black " +
                            "letters, a thin red vertical divider bar, then Gallery in " +
                            "lighter grey." },
            { "steward",    "My Health Steward logo. A lowercase script my on a soft salmon-" +
                            "pink left panel, then Health Steward in bold white letters on a " +
                            "brighter coral-red panel, with a small trademark symbol." },
            { "viridian",   "Viridian University logo. Viridian in bold black serif followed " +
                            "by University in a regular black serif, set on a single line " +
                            "like a traditional university wordmark." },
            { "wellspring", "Wellspring Ventures logo. WELLSPRING in black serif capitals " +
                            "above VENTURES in thin widely-spaced capitals, an elegant " +
                            "understated wordmark with no icon." },
            { "westlake",   "Westlake Power and Light logo. Westlake in bold white letters on " +
                            "a blue block, joined to Power & Light in white on a green block " +
                            "— a two-tone utility-company banner." },
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
