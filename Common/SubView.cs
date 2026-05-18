using System;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Component owned by a <see cref="ScreenView"/> that represents one
    /// concern within its parent screen — e.g. "dialogue choice state"
    /// within WorldScreen, or "messages app open" within PhoneScreen.
    ///
    /// SubViews give us:
    /// - <see cref="IsActive"/>-gated dispatch: <see cref="OnUpdate"/> only
    ///   runs when this concern is currently relevant; <see cref="OnEnter"/>/
    ///   <see cref="OnLeave"/> fire on the transition edges.
    /// - First-poll <see cref="OnInit"/> for deferred reflection that
    ///   needs the live parent screen instance.
    ///
    /// Activation patterns the framework handles transparently:
    /// - <b>Parallel</b> — multiple SubViews of a parent run concurrently.
    ///   Each decides its own IsActive (e.g., ChoiceSubView.IsActive ⇔
    ///   scriptState.hasDialogChoice; SessionSubView.IsActive ⇔ session-
    ///   in-progress). They observe different state so don't conflict.
    /// - <b>Exclusive</b> — at most one SubView is active at a time, gated
    ///   by parent state (e.g., PhoneScreen's app subs each check
    ///   phone.currentApp). The framework dispatches identically; only
    ///   the one whose IsActive returns true will see OnUpdate.
    /// </summary>
    internal abstract class SubView
    {
        protected object Parent { get; private set; } = null!;
        protected SpeechEngine Speech { get; private set; } = null!;
        protected Action<string> Log { get; private set; } = _ => { };

        /// <summary>Called once by the parent ScreenView right after this
        /// SubView is constructed, before any other hook fires. Subclasses
        /// do not override this — they use <see cref="OnInit"/> for first-
        /// poll setup work.</summary>
        internal void Wire(object parent, SpeechEngine speech, Action<string> log)
        {
            Parent = parent;
            Speech = speech;
            Log = log;
        }

        /// <summary>First-poll setup. Use for reflection that needs the
        /// live parent screen instance. Throwing here disables this
        /// SubView for the remainder of the session (parent logs and
        /// moves on) but doesn't kill its siblings.</summary>
        public virtual void OnInit() { }

        /// <summary>Returns true while this SubView's concern is in
        /// effect. Default: always active. The framework detects
        /// transitions and fires <see cref="OnEnter"/>/<see cref="OnLeave"/>
        /// automatically — subclasses don't track "_wasActive" manually.</summary>
        public virtual bool IsActive => true;

        /// <summary>Fires on IsActive transition false → true. Use to
        /// announce screen-reader context (e.g., speak the choice prompt
        /// and the "Make a choice" instruction).</summary>
        public virtual void OnEnter() { }

        /// <summary>Fires on IsActive transition true → false. Use to
        /// clear per-activation state so the next OnEnter starts
        /// fresh.</summary>
        public virtual void OnLeave() { }

        /// <summary>Per-poll work while IsActive. Implement the state
        /// machine for this concern here.</summary>
        public abstract void OnUpdate();

        /// <summary>Per-poll work that runs regardless of <see cref="IsActive"/>.
        /// Use for ambient state tracking that must continue while the
        /// SubView's announcement concern is inactive — e.g., refreshing
        /// a shared chapter/session label that other observers read even
        /// when the mode that owns the label isn't currently active.
        /// Runs before <see cref="OnEnter"/>/<see cref="OnLeave"/>/<see cref="OnUpdate"/>
        /// each tick. Default no-op.</summary>
        public virtual void OnPoll() { }

        /// <summary>Parent screen popped from the stack — release any
        /// long-lived resources. Default no-op.</summary>
        public virtual void OnRemoved() { }

        // ── Framework-only dispatch ────────────────────────────────────

        private bool _wasActive;
        private bool _initDone;

        /// <summary>
        /// Called by the parent ScreenView when it loses focus (another
        /// screen pushed on top of it). If we were active, fire
        /// <see cref="OnLeave"/> and reset the tracker so that when the
        /// parent regains focus and we're still active, the next
        /// <see cref="Drive"/> sees a false→true transition and fires
        /// <see cref="OnEnter"/> again — re-orienting the user to the
        /// sub-screen they're returning to.
        /// </summary>
        internal void MarkParentUnfocused()
        {
            if (!_wasActive) return;
            try { OnLeave(); }
            catch (Exception ex)
            {
                Log($"[SubView] {GetType().Name} OnLeave (parent unfocus) threw: {ex.GetType().Name}: {ex.Message}");
            }
            _wasActive = false;
        }

        /// <summary>Called by the parent ScreenView each poll. Drives the
        /// IsActive transition detection and dispatches the appropriate
        /// hooks. Catches and logs subclass exceptions so one broken
        /// SubView doesn't take down its siblings.</summary>
        internal void Drive()
        {
            if (!_initDone)
            {
                _initDone = true;
                try { OnInit(); }
                catch (Exception ex)
                {
                    Log($"[SubView] {GetType().Name} OnInit threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }

            bool active;
            try { active = IsActive; }
            catch (Exception ex)
            {
                Log($"[SubView] {GetType().Name} IsActive threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            try { OnPoll(); }
            catch (Exception ex)
            {
                Log($"[SubView] {GetType().Name} OnPoll threw: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (active && !_wasActive) OnEnter();
                else if (!active && _wasActive) OnLeave();
                _wasActive = active;
                if (active) OnUpdate();
            }
            catch (Exception ex)
            {
                Log($"[SubView] {GetType().Name} drive threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
