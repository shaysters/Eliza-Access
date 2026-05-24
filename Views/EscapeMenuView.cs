using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the in-game pause / escape menu.
    /// Three buttons in a vertical stack — History, Options, Title
    /// Screen (EscapeMenuScreen.cs:193-243). The X / close icon at
    /// top-right is hit-tested manually with bounds.Contains, not
    /// registered as a hotspot, so it doesn't appear in our focus
    /// tracking; pressing Escape directly is the keyboard
    /// equivalent.
    /// </summary>
    internal sealed class EscapeMenuView : ScreenView
    {
        public override string ScreenTypeName => "EscapeMenuScreen";

        protected override void OnAdded(object screen)
        {
            Elements.RegisterByKey("History",      new ButtonElement("History"));
            Elements.RegisterByKey("Options",      new ButtonElement("Options"));
            Elements.RegisterByKey("Title Screen", new ButtonElement("Title Screen"));
        }

        protected override void OnFocus()
        {
            Speech.Speak(Loc.Get("escape.pause_menu"));
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();
    }
}
