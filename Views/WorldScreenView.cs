using System;
using System.Collections.Generic;
using ElizaAccess.Common;
using ElizaAccess.Views.WorldScreenSubs;

namespace ElizaAccess.Views
{
    /// <summary>
    /// View for the in-game WorldScreen. WorldScreen is on the stack the
    /// entire time the player is in gameplay; while it's there, several
    /// distinct concerns are observable in parallel — dialogue choices,
    /// the running therapy session, autoplay state. Each is a separate
    /// <see cref="SubView"/> that the view hosts and dispatches to.
    ///
    /// SubViews are constructed in <see cref="OnAdded"/> and torn down in
    /// <see cref="OnRemoved"/>. Per-poll dispatch is a simple foreach over
    /// the sub list — each sub gates its own activity via its own
    /// <see cref="SubView.IsActive"/>, so they don't need any coordination
    /// from the parent.
    ///
    /// WorldScreen has no top-level focus concept (there's no cursor-on-
    /// hotspot navigation outside specific sub-modes like choices), so
    /// <see cref="ReadCurrentFocus"/> returns null — focus tracking lives
    /// inside the sub-views that need it.
    /// </summary>
    internal sealed class WorldScreenView : ScreenView
    {
        public override string ScreenTypeName => "WorldScreen";

        private readonly List<SubView> _subs = new List<SubView>();

        protected override void OnAdded(object screen)
        {
            _subs.Add(new ExplorationSubView());
            // Drive order matters — each sub below reads state that an
            // earlier sub wrote this same tick. The chain:
            //   SessionSub writes SessionState + TransparencyState
            //   TextingOverlaySub reads TransparencyState, writes TextingState
            //   DialogueSub reads TextingState
            //   NarrationSub reads SessionState + TransparencyState + TextingState
            // If any reader runs before its writer, it reads the
            // previous tick's value — that's how the first frame of
            // Transparency Mode used to leak a "Text from X." or a
            // bootloader replay.
            _subs.Add(new SessionSubView());
            _subs.Add(new TextingOverlaySubView());
            _subs.Add(new TransparencySubView());
            _subs.Add(new WellnessIntakeSubView());
            // Inline phone-app subs for Transparency Mode. During that
            // mode the phone is rendered on WorldScreen (not pushed as
            // PhoneScreen), so the regular PhoneScreenView MailApp /
            // ChatApp / HomeGrid subs never activate. These siblings of
            // those subs share the class with a TransparencyState gate
            // so they only fire in the inline context. The home-grid
            // sub also gets the inline render offsets (523, 268 from
            // WorldScreen.cs:1329) instead of PhoneScreen's (669, 268).
            _subs.Add(new PhoneScreenSubs.MailAppSubView(
                additionalGate: () => Common.TransparencyState.IsActive));
            _subs.Add(new PhoneScreenSubs.ChatAppSubView(
                additionalGate: () => Common.TransparencyState.IsActive));
            _subs.Add(new PhoneScreenSubs.HomeGridSubView(
                additionalGate: () => Common.TransparencyState.IsActive,
                homeOffsetX:    523f,
                homeOffsetY:    268f));
            _subs.Add(new PhoneScreenSubs.ElizaAppSubView(
                additionalGate: () => Common.TransparencyState.IsActive,
                phoneOriginX:   523f,
                phoneOriginY:   268f));
            _subs.Add(new DialogueSubView());
            _subs.Add(new NotificationSubView());
            _subs.Add(new NarrationSubView());
            // ChoiceSubView runs AFTER NarrationSubView so when both
            // fire in the same tick — the engine appends a narration
            // line (e.g. "Set phase: INTERVENTION") and sets the
            // DialogueChoice script step at the same time — the
            // narration speaks first and "Make a choice." speaks
            // second. Without this order the player hears the prompt
            // before the log entry that prompted it.
            _subs.Add(new ChoiceSubView());
            _subs.Add(new SessionResultsSubView());
            _subs.Add(new ChapterTitleSubView());

            foreach (var s in _subs)
                s.Wire(screen, Speech, Log);

            Log($"[WorldScreenView] added with {_subs.Count} sub-view(s).");
        }

        protected override void OnUpdate()
        {
            foreach (var s in _subs) s.Drive();
        }

        protected override void OnUnfocus()
        {
            // When something pushes on top of WorldScreen (escape menu,
            // confirm dialog, FakeAppScreen popup, etc.), pause the active
            // sub-views so that when the player returns, the framework
            // re-fires OnEnter and the sub re-announces its context
            // (e.g., "Look around. 3 things to interact with."). Without
            // this, _wasActive stays true across the focus gap and the
            // next Drive() sees no transition.
            foreach (var s in _subs) s.MarkParentUnfocused();
        }

        protected override void OnRemoved()
        {
            foreach (var s in _subs)
            {
                try { s.OnRemoved(); }
                catch (Exception ex)
                {
                    Log($"[WorldScreenView] {s.GetType().Name} OnRemoved threw: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            _subs.Clear();
        }

        protected override (object? key, string? label) ReadCurrentFocus() => (null, null);
    }
}
