namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "is a therapy session currently in progress?".
    ///
    /// Set by <see cref="Views.WorldScreenSubs.SessionSubView"/> whenever it
    /// detects either:
    /// - a formal session (<c>PhoneState.therapySession</c> has value), or
    /// - a shadow session (first-chapter sessions like Darren's where
    ///   <c>therapySession</c> never gets set but the star array activates).
    ///
    /// Read by <see cref="Views.WorldScreenSubs.ChoiceSubView"/> to decide
    /// whether to mark a focused choice with the "Proxy Response" prefix.
    /// The in-game Eliza panel that labels one choice as Eliza's
    /// recommended response only renders during sessions; outside of
    /// sessions there is no "proxy" concept, so the marker would be
    /// misleading.
    /// </summary>
    internal static class SessionState
    {
        /// <summary>True while a formal or shadow therapy session is
        /// active. False during normal gameplay (exploration, between-
        /// session dialogue, etc.).</summary>
        public static bool IsActive;
    }
}
