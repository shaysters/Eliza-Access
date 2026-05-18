namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "is an in-VN texting overlay currently
    /// up?" — the inline chat UI rendered on WorldScreen via
    /// <c>ChatApp.smethod_0(...)</c> when <c>WorldScreen.maybe_14</c> is
    /// set with a texting partner.
    ///
    /// Set by <see cref="Views.WorldScreenSubs.TextingOverlaySubView"/>
    /// each tick in OnPoll, so it stays current regardless of whether
    /// the overlay is currently driving its own OnUpdate.
    ///
    /// Read by <see cref="Views.WorldScreenSubs.DialogueSubView"/> to
    /// suppress its dialogue-line speech path while the overlay owns the
    /// message. <c>WorldScreen.method_24</c> sets the dialogue-line
    /// field (<c>maybe_8</c>) the moment a texting message begins
    /// processing — DialogueSub would otherwise speak the message bare
    /// (no speaker prefix, like internal monologue), and then
    /// TextingOverlay would speak the same line a couple seconds later
    /// with the proper "rainer: …" attribution once the typing timer
    /// expires. The double-speak in two different formats is what users
    /// perceive as "Evelyn's thoughts" being mixed into the convo.
    /// </summary>
    internal static class TextingState
    {
        /// <summary>True while WorldScreen.maybe_14 has value (the
        /// in-VN texting overlay is up). False during normal gameplay
        /// and during therapy sessions.</summary>
        public static bool IsActive;

        /// <summary><c>Environment.TickCount</c> of the most recent
        /// chat message the overlay spoke. Used by
        /// <see cref="Views.WorldScreenSubs.ChoiceSubView"/> to defer
        /// the "Make a choice" announcement until the latest incoming
        /// text has been read — otherwise the choice instruction fires
        /// the moment the choice struct lands (immediately, in engine
        /// time), but the message announce waits for the typing
        /// animation to complete, so the player hears the prompt
        /// before the message that prompted it.</summary>
        public static int LastMessageTick;
    }
}
