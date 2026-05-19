using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

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
    ///
    /// Each menu button is registered as a <see cref="ButtonElement"/>
    /// keyed by its on-screen label, so focus speaks "Continue.
    /// Button." instead of bare "Continue". The set is fixed
    /// (Continue / New Game / Chapter Select / Options / Exit Game —
    /// TitleScreen.cs:102-138), with Continue gated on save data and
    /// Exit Game hidden on Switch. Hidden buttons don't draw, so
    /// their labels never get paired by HotspotLabelCapture and the
    /// registered elements stay dormant — no harm in pre-registering
    /// all of them.
    /// </summary>
    internal sealed class TitleMenuView : ScreenView
    {
        public override string ScreenTypeName => "TitleScreen";

        protected override void OnAdded(object screen)
        {
            // Labels are localized at draw time; we register under
            // English literals which is what the cursor-pair captures
            // in the English locale. Other locales fall back to the
            // bare-label path (loc-key int → localized string mapping
            // for these labels isn't catalogued today — easy follow-up
            // when there's a non-English request).
            Elements.RegisterByKey("Continue",       new ButtonElement("Continue"));
            Elements.RegisterByKey("New Game",       new ButtonElement("New Game"));
            Elements.RegisterByKey("Chapter Select", new ButtonElement("Chapter Select"));
            Elements.RegisterByKey("Options",        new ButtonElement("Options"));
            Elements.RegisterByKey("Exit Game",      new ButtonElement("Exit Game"));
        }

        protected override void OnFocus()
        {
            Speech.Speak("Title menu");
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();
    }
}
