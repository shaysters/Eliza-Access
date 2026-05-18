namespace ElizaAccess.Common
{
    /// <summary>
    /// Cross-observer signal for "which exploration scene the player is
    /// currently in," e.g. <c>"light-rail01"</c> during the intro monorail
    /// scene.
    ///
    /// Written by <see cref="Views.WorldScreenSubs.ExplorationSubView"/>
    /// from the live ScriptState → ExplorationData → SessionData chain
    /// (the running-session view, not the script step's static scene def).
    /// Read by <c>V2PhoneObserver</c> to gate chapter-specific phrasing —
    /// notably <c>IsIntroChapter()</c>, which checks for the
    /// <c>"light-rail"</c> prefix to decide whether home-grid slots beyond
    /// the unlocked ones should be announced as locked vs. just missing.
    ///
    /// Null when no exploration session is active.
    /// </summary>
    internal static class ExplorationState
    {
        public static string? CurrentSessionLabel;
    }
}
