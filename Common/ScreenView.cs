using System;
using ElizaAccess.Common.Patches;
using ElizaAccess.UI;
using ElizaAccess.UI.Elements;

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

        /// <summary>Per-screen registry of <see cref="UIElement"/>
        /// descriptions. Subclasses register elements during
        /// <see cref="OnAdded"/>; <see cref="ReadFocusFromCursor"/>
        /// consults the registry to resolve a hotspot label into a
        /// full focus announcement, and the manager-driven update
        /// loop polls each registered element for reactive changes
        /// (slider value, checkbox toggle). Default behaviour is
        /// unchanged when the registry is empty — every existing
        /// screen continues to speak bare labels until it opts in.</summary>
        protected ElementRegistry Elements { get; } = new ElementRegistry();

        // Container path tracker. ReadFocusFromCursor reads it to
        // figure out which container labels to prepend; NotifyUpdate
        // updates it AFTER speaking so the next diff compares
        // against the just-announced path.
        private readonly FocusContext _focusContext = new FocusContext();
        // Element returned by the most recent ReadFocusFromCursor
        // call — staged so NotifyUpdate can advance the focus
        // context when focus actually changes (without changing the
        // (key, label) tuple contract).
        private UIElement? _stagedFocusElement;

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
        /// Focus diff key is the hotspot index in
        /// <c>VirtualInput.list_0</c> — unchanged from the pre-element
        /// behaviour, so no current screen sees any difference until it
        /// opts in by registering elements.
        ///
        /// When a screen registers a <see cref="UIElement"/> under the
        /// captured label string (the layout-robust identifier
        /// <see cref="HotspotLabelCapture"/> already produces), the
        /// element's composed focus text replaces the bare label.
        /// Otherwise the bare label is spoken as before.
        ///
        /// Returns (null, null) when the cursor is outside every
        /// hotspot.</summary>
        protected (object? key, string? label) ReadFocusFromCursor()
        {
            int idx = CursorFocus.FindHotspotIndex();
            if (idx < 0)
            {
                _stagedFocusElement = null;
                return (null, null);
            }
            string? label = HotspotLabelCapture.GetLabel(idx);

            // Registry lookup by label string. Screens register
            // elements under the same label HotspotLabelCapture pairs;
            // that key has already proven robust across layout shifts.
            // Focus diff still keys on the hotspot index — preserves
            // pre-element behaviour for non-opted-in screens, and
            // re-announces correctly even on the rare case where two
            // different hotspots happen to capture the same label.
            string? composed = ResolveFocusLabel(label, label);
            return ((object)idx, composed);
        }

        /// <summary>
        /// Resolve a registry key to its element's composed focus
        /// text (with container path-diff applied), falling back to
        /// <paramref name="fallbackLabel"/> when no element is
        /// registered under the key. Available to screens with
        /// custom focus models so they can opt into the element
        /// announcement pipeline without going through
        /// <see cref="ReadFocusFromCursor"/>.
        ///
        /// Side effect: stages the resolved element so
        /// <see cref="NotifyUpdate"/> can advance the container
        /// path tracker after speaking. Passing a null key clears
        /// the staged element (used when focus moves off any
        /// known thing).
        /// </summary>
        protected string? ResolveFocusLabel(object? registryKey, string? fallbackLabel)
        {
            UIElement? el = registryKey != null ? Elements.GetByKey(registryKey) : null;
            _stagedFocusElement = el;
            if (el == null) return fallbackLabel;

            // Container path-diff: prepend labels of containers the
            // player just entered. Already-entered containers (or
            // all of them, on the very first focus into this
            // screen) don't repeat. _focusContext is updated in
            // NotifyUpdate AFTER speaking, so this diff always
            // reflects the baseline from the last spoken focus.
            string elementText = el.ResolveFocusText();
            var newContainers = _focusContext.NewContainersOnPathTo(el);
            if (newContainers.Count == 0) return elementText;

            var sb = new System.Text.StringBuilder();
            foreach (Container c in newContainers)
            {
                if (string.IsNullOrEmpty(c.Label)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(c.Label).Append('.');
            }
            if (sb.Length == 0) return elementText;
            if (!string.IsNullOrEmpty(elementText)) sb.Append(' ').Append(elementText);
            return sb.ToString();
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
            // context while the dialog above was up). Also clear the
            // container path so re-entry re-announces the group
            // labels, not just the inner element.
            _lastFocusKey = null;
            _focusContext.Reset();
            _stagedFocusElement = null;
            OnUnfocus();
        }

        internal void NotifyUpdate()
        {
            try { OnUpdate(); }
            catch (Exception ex)
            {
                Log($"[{ScreenTypeName}View] OnUpdate threw: {ex.GetType().Name}: {ex.Message}");
            }
            // Reactive-change drain: each registered element decides
            // whether its state shifted since the last poll and yields
            // a bare-value announcement. No-op for screens that haven't
            // registered any elements (every screen, today).
            if (Elements.Count > 0)
            {
                foreach (UIElement el in Elements.All)
                {
                    Announcement? change;
                    try { change = el.PollReactiveChange(); }
                    catch (Exception ex)
                    {
                        Log($"[{ScreenTypeName}View] {el.GetType().Name} PollReactiveChange threw: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                    if (change == null) continue;
                    if (!string.IsNullOrWhiteSpace(change.Text))
                        Speech.Speak(change.Text);
                }
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

                // Focus actually changed — advance the container
                // path tracker so the next focus's diff compares
                // against the just-announced baseline. Resets the
                // path when we land on a non-element hotspot so
                // returning into a container re-announces it.
                _focusContext.UpdatePathTo(_stagedFocusElement);
            }
            catch (Exception ex)
            {
                Log($"[{ScreenTypeName}View] focus-drive threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
