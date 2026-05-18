using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Human-written descriptions for the four FortuneApp images.
    /// FortuneApp renders one of four pre-baked image animations per
    /// language (city / plants / ramen / smoke; see
    /// <c>GClass60</c> for the load mapping). The `.tex` format the
    /// game uses for these isn't standard PNG/DDS, so we can't OCR or
    /// pixel-describe them at runtime — instead a sighted observer
    /// looked at each fortune and the description is recorded here.
    ///
    /// Keys are 1-based fortune numbers matching the "Number N of 4"
    /// announcement and the music tracks fortune1.ogg-fortune4.ogg.
    /// Description = empty string means "not yet described" — the
    /// announcement falls back to just "Fortune number N of 4. Theme."
    /// </summary>
    internal static class FortuneDescriptions
    {
        public readonly struct Entry
        {
            public string Theme { get; }
            public string Description { get; }
            public Entry(string theme, string description)
            {
                Theme = theme;
                Description = description;
            }
        }

        // Theme names come from the asset paths
        // (textures/fortune/{name}.animation). Number→theme mapping
        // verified against FortuneApp.cs case dispatch:
        //   case 0 (default) → gclass55_3 → smoke
        //   case 1           → gclass55_1 → plants
        //   case 2           → gclass55_0 → city
        //   case 3           → gclass55_2 → ramen
        // So the player-facing "Number N of 4" (= internal index + 1)
        // maps as: 1=Smoke, 2=Plants, 3=City, 4=Ramen.
        private static readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>
        {
            [1] = new Entry("Smoke",  "\"Think back to what brought you here.\" A row of red and white paper lanterns hangs on wires strung diagonally across a pale sky; below, two red-and-white-striped industrial smokestacks rise from a stretch of bare winter trees and low buildings, their white plumes drifting upward."),
            [2] = new Entry("Plants", "\"What does a fulfilled life look like?\" A pixel-art balcony garden: red camellias in bloom on a slatted shelf, a small bonsai in a green pot beside them, and larger pots on the floor below. A diagonal shaft of warm orange light splits the vertical wooden screen behind into sunlit and shadowed halves; white petals drift through the air, and a beige air-conditioner unit anchors the right side of the scene."),
            [3] = new Entry("City",   "\"There's no need to rush.\" A woman stands on a bridge at night, looking out over a glowing pixel-art city skyline reflected in the water below."),
            [4] = new Entry("Ramen",  "\"Who is important to you?\" Two figures sit side by side on stools at an open-air pixel-art ramen stand at night, lit by a red lantern with vertical Japanese characters. A bare tree and pink blossoms drift overhead; warm-lit apartment windows glow in the background, and a small black cat sits beside the stall."),
        };

        public static bool TryGet(int oneBasedNumber, out Entry entry)
            => _entries.TryGetValue(oneBasedNumber, out entry);
    }
}
