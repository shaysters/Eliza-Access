namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "is a phone app currently open in the
    /// inline phone render that runs during Transparency Mode?" — i.e.
    /// <c>WorldScreen.phoneState_0.maybe_0.HasValue</c> while
    /// <c>maybe_4</c> (ScriptTransparency) also has value.
    ///
    /// Set by <see cref="Views.WorldScreenSubs.SessionSubView"/> each
    /// tick in OnPoll.
    ///
    /// Read by <see cref="Patches.KeyboardToController"/> to decide
    /// whether to drive controller button 7 (the back / B button) from
    /// keyboard Escape. During Transparency Mode the inline phone
    /// consumes <c>Key.ESCAPE</c> directly via
    /// <c>PhoneScreen.smethod_1</c> to close the active app — if we
    /// ALSO drove button 7 → <c>Key.FAKE_KEY_4</c> in the same tick,
    /// <c>WorldScreen.method_12</c> would open the escape menu on top
    /// of the phone-back, producing a duplicate effect on a single
    /// Escape press.
    /// </summary>
    internal static class PhoneAppOpenState
    {
        /// <summary>True while the inline phone (Transparency-Mode
        /// path) has an app open. Outside Transparency Mode this is
        /// always false — the regular PhoneScreen pushes onto the
        /// stack and the engine's own input routing keeps Escape
        /// from double-firing.</summary>
        public static bool IsInlineAppOpen;
    }
}
