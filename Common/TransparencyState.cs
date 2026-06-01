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

        /// <summary>Build the on-demand review announcement spoken
        /// when the player presses Q. Returns the active "three
        /// conclusions" readout with current answer states, or the
        /// "not active" string when there's no Transparency Mode
        /// session up.
        ///
        /// Kept here (not in HomeGridSubView) because Q is polled
        /// globally in Main — the SubView's lifecycle only ran while
        /// the inline phone home grid was the active focus, so the
        /// key felt dead once the player opened Mail or Chat to read
        /// the inbox.</summary>
        public static string BuildReviewAnnouncement()
        {
            string[] statements = Statements;
            if (!IsActive || statements.Length == 0)
                return Loc.Get("transparency.no_active");

            bool?[] answers = Answers;
            var sb = new System.Text.StringBuilder();
            sb.Append(Loc.Get("transparency.three_conclusions"));
            for (int i = 0; i < statements.Length; i++)
            {
                bool? ans = i < answers.Length ? answers[i] : null;
                string state = !ans.HasValue
                    ? Loc.Get("transparency.answer_not_yet")
                    : Loc.Get(ans.Value ? "transparency.answer_agreed" : "transparency.answer_disagreed");
                sb.Append(' ');
                sb.Append(Loc.Fmt("transparency.statement_full", i + 1, statements[i], state));
            }
            return sb.ToString();
        }
    }
}
