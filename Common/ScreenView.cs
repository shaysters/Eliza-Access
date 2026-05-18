using System;
using ElizaAccess.Common.Patches;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Per-instance handler for one screen on the game's ScreenStack.
    ///
    /// <see cref="ScreenViewManager"/> mirrors the game's stack: every time
    /// a screen is pushed, the manager constructs a fresh <see cref="ScreenView"/>
    /// of the type registered for that screen's type name; every time a
    /// screen is popped, the manager discards the view. State on the view
    /// is therefore implicitly scoped to one stack lifetime — there is no
    /// "is this a fresh entry or a re-entry" book-keeping for subclasses
    /// to get wrong.
    ///
    /// Hook ordering during stack transitions:
    /// <code>
    /// push A:           new A → A.OnAdded → A.OnFocus
    /// push B onto A:    A.OnUnfocus → new B → B.OnAdded → B.OnFocus
    /// pop B:            B.OnUnfocus → B.OnRemoved → discard B → A.OnFocus
    /// pop A:            A.OnUnfocus → A.OnRemoved → discard A
    /// </code>
    /// While a view is topmost, the manager calls <see cref="OnUpdate"/> and
    /// drives focus-change detection (<see cref="ReadCurrentFocus"/> + the
    /// resulting <see cref="OnFocusChanged"/>) every poll. Non-top views in
    /// the mirror are dormant.
    ///
    /// Subclasses must provide:
    /// - <see cref="ScreenTypeName"/> — the <c>Type.Name</c> of the game
    ///   screen this view handles (e.g. <c>"EscapeMenuScreen"</c>).
    /// - <see cref="ReadCurrentFocus"/> — the screen's focus model. For
    ///   cursor-on-hotspot screens, return
    ///   <see cref="ReadFocusFromCursor"/>; for screens that determine
    ///   focus some other way (a field on the screen instance, a scroll
    ///   line, an internal selection state), implement it directly.
    /// </summary>
    internal abstract class ScreenView
    {
        // Wired by the manager once, right after construction, before any
        // hook fires. Subclasses access via the protected properties.
        protected Action<string> Log     { get; private set; } = _ => { };
        protected SpeechEngine   Speech  { get; private set; } = null!;

        /// <summary>The screen instance this view was constructed for. Set
        /// just before <see cref="OnAdded"/> and cleared in
        /// <see cref="OnRemoved"/>; null outside that window.</summary>
        protected object? ScreenInstance { get; private set; }

        /// <summary>The <see cref="Type.Name"/> of the screen this view
        /// handles. Used by <see cref="ScreenViewManager"/> to pick the
        /// right view type when a new screen is pushed.</summary>
        public abstract string ScreenTypeName { get; }

        // ── Lifecycle hooks ────────────────────────────────────────────
        // All optional; default no-ops. Subclasses override what they need.

        /// <summary>Called once, immediately after construction, with the
        /// game's screen instance. Use to capture reflection handles on
        /// the screen.</summary>
        protected virtual void OnAdded(object screen) { }

        /// <summary>Called when this view's screen is removed from the
        /// stack. The view object is discarded after this returns.</summary>
        protected virtual void OnRemoved() { }

        /// <summary>Called when this view's screen becomes the topmost
        /// entry on the stack. Fires after <see cref="OnAdded"/> for the
        /// initial push, and again whenever a screen above it pops.</summary>
        protected virtual void OnFocus() { }

        /// <summary>Called when this view's screen stops being topmost
        /// (another screen pushed on top of it, or this one is about to
        /// be removed).</summary>
        protected virtual void OnUnfocus() { }

        /// <summary>Called every manager poll while this view is topmost.
        /// Use for per-frame state observation (slider value changes,
        /// dialog text appearing, etc.) — anything that isn't a focus
        /// change should go here.</summary>
        protected virtual void OnUpdate() { }

        // ── Focus model ────────────────────────────────────────────────

        /// <summary>Read the screen's "what is focused right now?" pair.
        /// Key is anything Equals-comparable that uniquely identifies the
        /// focused thing (hotspot index, line number, button enum value);
        /// label is the speech to read aloud. Return (null, null) when
        /// nothing is focused. The manager only fires
        /// <see cref="OnFocusChanged"/> when the key changes.</summary>
        protected abstract (object? key, string? label) ReadCurrentFocus();

        /// <summary>Called by the manager when <see cref="ReadCurrentFocus"/>'s
        /// key changed since last poll. Default: speak the label, interrupting
        /// anything currently in flight. Override to add prefixes, mute, etc.</summary>
        protected virtual void OnFocusChanged(object? key, string label)
        {
            // All speech queues. The mid-utterance cut is handled by
            // InputInterrupter when the user provides input; views never
            // interrupt directly.
            Speech.Speak(label);
        }

        /// <summary>Cursor-on-hotspot focus reader. Subclasses opt into it
        /// by returning its result from <see cref="ReadCurrentFocus"/>:
        /// <code>
        /// protected override (object?, string?) ReadCurrentFocus()
        ///     => ReadFocusFromCursor();
        /// </code>
        /// Key is the hotspot index in <c>VirtualInput.list_0</c>; label
        /// is whatever <see cref="HotspotLabelCapture"/> paired with that
        /// hotspot this frame. Returns (null, null) if the cursor is
        /// outside every hotspot.</summary>
        protected static (object? key, string? label) ReadFocusFromCursor()
        {
            int idx = CursorFocus.FindHotspotIndex();
            if (idx < 0) return (null, null);
            return ((object)idx, HotspotLabelCapture.GetLabel(idx));
        }

        // ── Manager-only entry points ──────────────────────────────────
        // These are the methods ScreenViewManager calls. They mediate
        // between the manager's lifecycle and the subclass hooks so that
        // bookkeeping (last-focus-key reset, screen-instance capture) is
        // centralized here and subclasses can't forget to do it.

        private object? _lastFocusKey;

        internal void Wire(Action<string> log, SpeechEngine speech)
        {
            Log = log;
            Speech = speech;
        }

        internal void NotifyAdded(object screen)
        {
            ScreenInstance = screen;
            OnAdded(screen);
        }

        internal void NotifyRemoved()
        {
            OnRemoved();
            ScreenInstance = null;
        }

        internal void NotifyFocus() => OnFocus();

        internal void NotifyUnfocus()
        {
            // Reset the change-detection key so re-focusing the screen
            // re-announces whatever's focused (the user lost speech
            // context while the dialog above was up).
            _lastFocusKey = null;
            OnUnfocus();
        }

        internal void NotifyUpdate()
        {
            try { OnUpdate(); }
            catch (Exception ex)
            {
                Log($"[{ScreenTypeName}View] OnUpdate threw: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                var (key, label) = ReadCurrentFocus();
                // null key = "no focus right now" (cursor between hotspots,
                // mid-frame hotspot rebuild, transient list_0 mutation).
                // Treat as "no change" so a momentary unfocus doesn't reset
                // _lastFocusKey and cause the next return-to-same-slot to
                // re-announce. Real focus changes go from key A → key B.
                if (key == null) return;
                if (Equals(key, _lastFocusKey)) return;
                _lastFocusKey = key;
                if (!string.IsNullOrEmpty(label))
                    OnFocusChanged(key, label!);
            }
            catch (Exception ex)
            {
                Log($"[{ScreenTypeName}View] focus-drive threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
