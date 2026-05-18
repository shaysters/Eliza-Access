using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the game's title menu. Replaces the legacy
    /// ~2160-line V2TitleMenuObserver with the standard cursor-on-hotspot
    /// pattern.
    ///
    /// Behaviour: on focus, queue "Title menu" so the focus-change
    /// announcement queues after it. <c>InputInterrupter</c> handles
    /// clearing any in-flight speech when the user provides input.
    /// </summary>
    internal sealed class TitleMenuView : ScreenView
    {
        public override string ScreenTypeName => "TitleScreen";

        protected override void OnFocus()
        {
            Speech.Speak("Title menu");
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();
    }
}
