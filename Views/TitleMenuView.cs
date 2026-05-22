using System;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the game's title menu. Replaces the legacy
    /// ~2160-line V2TitleMenuObserver with the standard cursor-on-hotspot
    /// pattern.
    ///
    /// Each menu button is registered as a <see cref="ButtonElement"/>
    /// keyed by its on-screen label, so focus speaks "Continue,
    /// button" instead of bare "Continue". The set is fixed
    /// (Continue / New Game / Chapter Select / Options / Exit Game —
    /// TitleScreen.cs:102-138), with Continue gated on save data and
    /// Exit Game hidden on Switch. Hidden buttons don't draw, so
    /// their labels never get paired by HotspotLabelCapture and the
    /// registered elements stay dormant — no harm in pre-registering
    /// all of them.
    ///
    /// All buttons sit under a single "Title menu" <see cref="RowContainer"/>.
    /// Its label is the player's readiness signal: the first focus
    /// announce on a fresh screen says "Title menu, Continue, button",
    /// telling the player simultaneously where they are and that the
    /// menu is interactable. If that first focus takes longer than
    /// <see cref="LoadingAnnounceAfter"/> to arrive (intro fade-in
    /// still running, hotspots not yet registered), we say "Scene
    /// loading." once so the player knows the mod hasn't hung.
    /// </summary>
    internal sealed class TitleMenuView : ScreenView
    {
        public override string ScreenTypeName => "TitleScreen";

        // OnFocus timestamp + one-shot "Scene loading." gate. Same
        // shape as ExplorationSubView — fast loads stay quiet, slow
        // loads get one intermediate cue and then the container-
        // prefixed first-focus announce when ready.
        private DateTime _focusedAtUtc;
        private bool     _loadingAnnounced;
        private bool     _firstFocusSeen;
        private static readonly TimeSpan LoadingAnnounceAfter = TimeSpan.FromMilliseconds(500);

        protected override void OnAdded(object screen)
        {
            // Labels are localized at draw time; we register under
            // English literals which is what the cursor-pair captures
            // in the English locale. Other locales fall back to the
            // bare-label path (loc-key int → localized string mapping
            // for these labels isn't catalogued today — easy follow-up
            // when there's a non-English request).
            var menu = new RowContainer("Title menu");

            void AddButton(string label)
            {
                var btn = new ButtonElement(label);
                menu.Add(btn);
                Elements.RegisterByKey(label, btn);
            }

            AddButton("Continue");
            AddButton("New Game");
            AddButton("Chapter Select");
            AddButton("Options");
            AddButton("Exit Game");
        }

        protected override void OnFocus()
        {
            _focusedAtUtc     = DateTime.UtcNow;
            _loadingAnnounced = false;
            _firstFocusSeen   = false;
        }

        protected override void OnUpdate()
        {
            if (_firstFocusSeen || _loadingAnnounced) return;
            if (DateTime.UtcNow - _focusedAtUtc < LoadingAnnounceAfter) return;
            _loadingAnnounced = true;
            Speech.Speak("Scene loading.");
        }

        protected override void OnFocusChanged(object? key, string label)
        {
            _firstFocusSeen = true;
            base.OnFocusChanged(key, label);
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();
    }
}
