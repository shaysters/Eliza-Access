namespace ElizaAccess.Common
{
    /// <summary>
    /// The two avatars visible in an open Messages conversation —
    /// published by ChatAppSubView while a conversation is open, read by
    /// <see cref="ImageBrowser"/> for J/K/L. Keys are ScriptCharacter
    /// internal names ("Nora"), matching image.avatar.* descriptions.
    /// </summary>
    internal static class PhoneAvatarState
    {
        /// <summary>Conversation partner, or null when no conversation
        /// is open.</summary>
        public static volatile string? PartnerKey;
        /// <summary>The phone's owner (Evelyn, or the patient in
        /// Transparency Mode).</summary>
        public static volatile string? OwnerKey;

        public static void Clear()
        {
            PartnerKey = null;
            OwnerKey = null;
        }
    }
}
