using System;
using System.Collections.Generic;
using ElizaAccess.Common;
using ElizaAccess.Views.PhoneScreenSubs;

namespace ElizaAccess.Views
{
    /// <summary>
    /// View for the in-game PhoneScreen (a real IScreen pushed onto the
    /// stack, not a WorldScreen overlay). While the phone is open, exactly
    /// one of several "apps" can be active: home grid (no app), MailApp,
    /// ChatApp, FortuneApp, ElizaApp. Apps are NOT separate screens — they
    /// flip the <c>PhoneState.activeApp</c> field. SolitaireScreen is the
    /// exception; it gets pushed as its own screen and is handled by its
    /// own view.
    ///
    /// Each app maps to a sibling <see cref="SubView"/>. They're mutually
    /// exclusive — exactly one is active at a time based on which type
    /// the active-app Maybe holds. The framework's IsActive gating
    /// dispatches the OnEnter/OnLeave transitions for free as the user
    /// switches between apps.
    ///
    /// PhoneScreen itself has no top-level cursor-on-hotspot focus
    /// concept; the home grid uses polygon containment over the cursor
    /// position. So <see cref="ReadCurrentFocus"/> returns null and each
    /// sub-view runs its own focus tracking.
    /// </summary>
    internal sealed class PhoneScreenView : ScreenView
    {
        public override string ScreenTypeName => "PhoneScreen";

        private readonly List<SubView> _subs = new List<SubView>();

        protected override void OnAdded(object screen)
        {
            _subs.Add(new HomeGridSubView());
            _subs.Add(new ChatAppSubView());
            _subs.Add(new MailAppSubView());
            _subs.Add(new FortuneAppSubView());
            _subs.Add(new ElizaAppSubView());

            foreach (var s in _subs)
                s.Wire(screen, Speech, Log);

            Log($"[PhoneScreenView] added with {_subs.Count} sub-view(s).");
        }

        protected override void OnUpdate()
        {
            foreach (var s in _subs) s.Drive();
        }

        protected override void OnUnfocus()
        {
            // SolitaireScreen pushing on top of PhoneScreen — fire each
            // sub-view's OnLeave so when the phone regains focus the
            // active sub re-announces its context.
            foreach (var s in _subs) s.MarkParentUnfocused();
        }

        protected override void OnRemoved()
        {
            foreach (var s in _subs)
            {
                try { s.OnRemoved(); }
                catch (Exception ex)
                {
                    Log($"[PhoneScreenView] {s.GetType().Name} OnRemoved threw: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            _subs.Clear();
        }

        protected override (object? key, string? label) ReadCurrentFocus() => (null, null);
    }
}
