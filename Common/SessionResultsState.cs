namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "is the post-session results overlay
    /// currently up?" — the EndTherapy script step that
    /// <see cref="Views.WorldScreenSubs.SessionResultsSubView"/> detects.
    ///
    /// Set by <see cref="Views.WorldScreenSubs.SessionResultsSubView"/>
    /// in OnEnter / cleared in OnLeave.
    ///
    /// Read by <see cref="Views.WorldScreenSubs.DialogueSubView"/> to
    /// suppress its dialogue-line speech path while the results overlay
    /// is up. The engine doesn't clear the in-session dialogue line
    /// (<c>WorldScreen.maybe_8</c>) when EndTherapy starts, and may
    /// even append a closing line during the EndTherapy step itself —
    /// without this gate the player hears "Session Results" followed
    /// by a stray Eliza line as DialogueSub fires on the still-set or
    /// newly-set value.
    /// </summary>
    internal static class SessionResultsState
    {
        public static bool IsActive;
    }
}
