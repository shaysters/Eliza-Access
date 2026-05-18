using System;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "the player just confirmed a dialogue choice."
    ///
    /// Written by <see cref="Views.ChoiceView"/> when it observes
    /// <c>WorldScreen._selectedField</c> transition from "no value" to a
    /// valid index. Read by <c>V2AutoObserver</c> to suppress speaking the
    /// chosen line back to the user (the player just heard themselves
    /// navigate to and pick this option; echoing it as an arriving
    /// dialogue line is redundant).
    ///
    /// <see cref="LastConfirmedChoiceTick"/> is <see cref="Environment.TickCount"/>
    /// at the moment of confirmation; readers should check tick-age and
    /// ignore stale entries (Auto uses a ~4 s suppression window).
    /// </summary>
    internal static class ChoiceState
    {
        public static string? LastConfirmedChoiceText;
        public static int     LastConfirmedChoiceTick;
    }
}
