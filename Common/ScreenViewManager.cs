using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Mirrors the game's ScreenStack with one <see cref="ScreenView"/>
    /// instance per stack entry. Diffs the game stack each poll and fires
    /// <c>OnAdded</c> / <c>OnRemoved</c> / <c>OnFocus</c> / <c>OnUnfocus</c>
    /// / <c>OnUpdate</c> on the appropriate views.
    ///
    /// Views are registered by screen type name (<see cref="Register{T}"/>);
    /// the manager constructs a fresh view via <c>new T()</c> each time a
    /// screen of that type is pushed. Screens with no registered view are
    /// silently skipped — they just don't appear in the mirror.
    ///
    /// Mirror entries are matched against the game's stack by
    /// <c>ReferenceEquals(screenInstance)</c>, so two stack entries of the
    /// same screen type (e.g. nested confirm dialogs) get two distinct
    /// view instances with independent state.
    /// </summary>
    internal sealed class ScreenViewManager
    {
        private readonly Action<string> _log;
        private readonly SpeechEngine _speech;

        // screen type name → factory that produces a new ScreenView for it
        private readonly Dictionary<string, Func<ScreenView>> _factories =
            new Dictionary<string, Func<ScreenView>>(StringComparer.Ordinal);

        // The mirror: parallel to the game's ScreenStack list. Bottom is
        // index 0, top is the last entry. Each entry pairs the game's
        // screen instance with the view we created for it.
        private readonly List<MirrorEntry> _mirror = new List<MirrorEntry>(8);

        public ScreenViewManager(Action<string> log, SpeechEngine speech)
        {
            _log = log;
            _speech = speech;
        }

        /// <summary>
        /// Register a view type for a game screen. The manager will call
        /// <c>new T()</c> each time a screen with type name
        /// <paramref name="screenTypeName"/> is pushed. View's parameterless
        /// constructor must succeed without dependencies — initialization
        /// that needs the screen instance happens in <c>OnAdded</c>, and
        /// log/speech are wired by the manager before any hook fires.
        /// </summary>
        public void Register<T>(string screenTypeName) where T : ScreenView, new()
        {
            _factories[screenTypeName] = () => new T();
        }

        /// <summary>
        /// Diff the game stack and fire lifecycle hooks. Idempotent if the
        /// stack is unchanged. Safe to call at any rate — designed for
        /// Main's ~200 Hz poll loop.
        /// </summary>
        public void Poll()
        {
            IList? gameStack = ReadGameStack();
            if (gameStack == null) return;

            int gameCount = gameStack.Count;

            // Walk the mirror and the game stack together, dropping mirror
            // entries whose screen is no longer present and inserting new
            // views for screens that just appeared. Match by reference.
            //
            // We process bottom-up because that's the order the game
            // builds the stack and the order in which OnAdded/OnFocus
            // make sense (new entries always arrive at the top).

            // 1) Drop mirror entries whose screen is gone. Iterate in
            //    reverse so we can RemoveAt without disturbing indices.
            for (int i = _mirror.Count - 1; i >= 0; i--)
            {
                if (!StackContainsRef(gameStack, _mirror[i].Screen))
                {
                    MirrorEntry gone = _mirror[i];
                    if (gone.View != null)
                    {
                        bool wasTop = (i == _mirror.Count - 1);
                        SafeNotify(gone.View, v =>
                        {
                            if (wasTop) v.NotifyUnfocus();
                            v.NotifyRemoved();
                        });
                    }
                    _mirror.RemoveAt(i);
                }
            }

            // 2) Add mirror entries for new screens. Walk game stack in
            //    order; for each, if it's not in the mirror, insert a new
            //    view at the appropriate position.
            for (int gi = 0; gi < gameCount; gi++)
            {
                object? screen = gameStack[gi];
                if (screen == null) continue;
                if (gi < _mirror.Count && ReferenceEquals(_mirror[gi].Screen, screen))
                    continue;  // already present at the expected position

                // Either the mirror is shorter than the game stack here,
                // or the screen at this position differs from ours — in
                // either case, a new screen has appeared at gi. Push it.
                string typeName = screen.GetType().Name;
                if (!_factories.TryGetValue(typeName, out Func<ScreenView>? factory))
                {
                    // No view registered for this screen type. Insert a
                    // placeholder entry so the mirror stays aligned with
                    // the game stack by index — that way OnFocus dispatch
                    // for "top of game stack" can simply look at the last
                    // mirror entry.
                    _mirror.Insert(gi, new MirrorEntry(screen, null));
                    _log($"[ScreenViewManager] unregistered screen pushed: {typeName} at depth {gi}");
                    continue;
                }

                // If the previous top (if any) is about to be displaced,
                // unfocus it first. Skip if it has no view (unregistered
                // screen type).
                if (gi == gameCount - 1 && _mirror.Count > 0)
                {
                    ScreenView? prevTop = _mirror[_mirror.Count - 1].View;
                    if (prevTop != null) SafeNotify(prevTop, v => v.NotifyUnfocus());
                }
                // Reset _lastTopView if it pointed to a view we'll
                // displace — otherwise focus reconciliation below thinks
                // the previous top is still focused.
                if (_mirror.Count > 0
                    && ReferenceEquals(_lastTopView, _mirror[_mirror.Count - 1].View))
                {
                    _lastTopView = null;
                }

                ScreenView view = factory();
                view.Wire(_log, _speech);
                _mirror.Insert(gi, new MirrorEntry(screen, view));
                _log($"[ScreenViewManager] added view for {typeName} at depth {gi}");
                SafeNotify(view, v => v.NotifyAdded(screen));
            }

            // 3) Reconcile focus: whoever is at the top of the mirror
            //    should be the focused view. If the previous top differs,
            //    swap focus.
            int topIdx = _mirror.Count - 1;
            if (topIdx >= 0)
            {
                ScreenView? newTop = _mirror[topIdx].View;
                if (!ReferenceEquals(newTop, _lastTopView))
                {
                    // Old top loses focus if still present (it could have
                    // already lost it in step 1 if it was removed).
                    if (_lastTopView != null
                        && IndexOfView(_lastTopView) >= 0
                        && !ReferenceEquals(_lastTopView, newTop))
                    {
                        SafeNotify(_lastTopView, v => v.NotifyUnfocus());
                    }
                    if (newTop != null)
                    {
                        _log($"[ScreenViewManager] focus → {newTop.ScreenTypeName}");
                        SafeNotify(newTop, v => v.NotifyFocus());
                    }
                    _lastTopView = newTop;
                }

                // Drive the top each poll for OnUpdate + focus-change
                // dispatch.
                if (newTop != null) SafeNotify(newTop, v => v.NotifyUpdate());
            }
            else
            {
                // Stack is empty.
                _lastTopView = null;
            }
        }

        private ScreenView? _lastTopView;

        // ── Mirror entry ───────────────────────────────────────────────

        private readonly struct MirrorEntry
        {
            public readonly object Screen;
            public readonly ScreenView? View;  // null = screen type with no registered view
            public MirrorEntry(object screen, ScreenView? view)
            { Screen = screen; View = view; }
        }

        private static bool StackContainsRef(IList stack, object screen)
        {
            for (int i = 0; i < stack.Count; i++)
                if (ReferenceEquals(stack[i], screen)) return true;
            return false;
        }

        private int IndexOfView(ScreenView view)
        {
            for (int i = 0; i < _mirror.Count; i++)
                if (ReferenceEquals(_mirror[i].View, view)) return i;
            return -1;
        }

        private void SafeNotify(ScreenView view, Action<ScreenView> hook)
        {
            try { hook(view); }
            catch (Exception ex)
            {
                _log($"[ScreenViewManager] hook on {view.ScreenTypeName} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── ScreenStack reflection ─────────────────────────────────────

        private bool _stackLogged;

        private IList? ReadGameStack()
        {
            IList? stack = ScreenStackBinding.GetStack();
            if (!_stackLogged)
            {
                _stackLogged = true;
                _log($"[ScreenViewManager] stack-list method: {ScreenStackBinding.StackListMethodResolved}");
            }
            return stack;
        }
    }
}
