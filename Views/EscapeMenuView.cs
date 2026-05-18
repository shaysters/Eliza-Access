using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the in-game pause / escape menu. First
    /// real <see cref="ScreenView"/> implementation — proves the base
    /// class shape on the simplest possible screen (vertical list of
    /// buttons, focus tracked by cursor-on-hotspot).
    ///
    /// Behaviour: on focus, queue "Pause menu" so the focus-change
    /// announcement (the currently-focused item's label) plays after
    /// it. The queue is cleared by <c>InputInterrupter</c> when the
    /// user pressed something to reach this screen, so there's no
    /// stale speech to cut through.
    /// </summary>
    internal sealed class EscapeMenuView : ScreenView
    {
        public override string ScreenTypeName => "EscapeMenuScreen";

        protected override void OnFocus()
        {
            Speech.Speak("Pause menu");
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();
    }
}
