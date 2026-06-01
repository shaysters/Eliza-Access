namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "is the Transparency-Mode survey
    /// screen currently up?" — the Eliza UI that shows a patient
    /// summary plus an agree/disagree questionnaire on Eliza's
    /// conclusions about that patient.
    ///
    /// Set by <see cref="Views.WorldScreenSubs.SessionSubView"/> each
    /// tick in <see cref="Common.SubView.OnPoll"/>, based on whether
    /// <c>PhoneState.maybe_4</c> (a <c>Maybe&lt;ScriptTransparency&gt;</c>)
    /// has value. WorldScreen.cs:3702 sets that field when a
    /// ShowTransparency script event fires; line 3721 clears it when
    /// the mode ends.
    ///
    /// Read by <see cref="Views.WorldScreenSubs.NarrationSubView"/> to
    /// suppress its AR-bootloader / sentiment-analyzer readout while
    /// Transparency Mode is up — that UI covers the AR display, so
    /// reading it would be reading content the sighted player can't
    /// see either. Also read by
    /// <see cref="Views.WorldScreenSubs.TextingOverlaySubView"/>:
    /// Transparency Mode also sets <c>WorldScreen.maybe_14</c> (the
    /// chat partner) for its bundled chat-history view, which would
    /// otherwise trigger a stray "Text from X." announcement.
    /// </summary>
    internal static class TransparencyState
    {
        /// <summary>True while PhoneState.maybe_4 has value (the
        /// Transparency-Mode survey screen is up). False at all
        /// other times.</summary>
        public static bool IsActive;

        /// <summary>Display name of the patient being reviewed (Maya /
        /// Holiday / Soren). Updated by SessionSubView each tick.
        /// Empty when <see cref="IsActive"/> is false.
        ///
        /// Read by ChatAppSubView when it speaks player-side messages
        /// from the inline phone — those messages are written by the
        /// patient (not Evelyn), so attribution should match the
        /// subject of the survey rather than the regular player
        /// character.</summary>
        public static string SubjectName = "";

        /// <summary>The three conclusion statements the player is
        /// asked to agree or disagree with, snapshotted on
        /// <see cref="Views.WorldScreenSubs.TransparencySubView.OnEnter"/>.
        /// Read by HomeGridSubView's Q-key handler so the player can
        /// review the questions from the inline phone's home grid
        /// without waiting for the engine to render the answer
        /// buttons (which only become interactable after all emails
        /// and texts are read).
        ///
        /// Empty when <see cref="IsActive"/> is false.</summary>
        public static string[] Statements = System.Array.Empty<string>();

        /// <summary>Current answer state per statement: null=unanswered,
        /// true=Agreed, false=Disagreed. Kept in sync each tick by
        /// TransparencySubView so the Q-key review reflects what the
        /// player has answered so far.</summary>
        public static bool?[] Answers = new bool?[3];
    }
}
