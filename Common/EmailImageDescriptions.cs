namespace ElizaAccess.Common
{
    /// <summary>
    /// Spoken descriptions for the embedded image tokens that appear
    /// in email bodies. The engine looks up these keys in
    /// <c>GClass127.dictionary_0</c> and renders the matching Texture
    /// inline; for the screen reader we substitute a verbal
    /// description of what the image depicts.
    ///
    /// The descriptions live in each language's images.json as
    /// <c>image.email.{token}.desc</c>, with English fallback through
    /// <see cref="Loc"/>. They were authored by inspecting the dumped
    /// PNG outputs (F9 in-game triggers <see cref="TextureDumper"/>),
    /// so they reflect the actual visual content rather than just the
    /// token name. Most images are company logos that paired with each
    /// email's sender; HEDONIC is a full event flyer with time, place,
    /// lineup.
    /// </summary>
    internal static class EmailImageDescriptions
    {
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
            return Loc.TryGet($"image.email.{key.ToLowerInvariant()}.desc");
        }
    }
}
