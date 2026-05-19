using System.Collections.Generic;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.UI
{
    /// <summary>
    /// Abstract base for every UI element the screen-reader layer
    /// describes. An element is a pure description: what to say when
    /// focused, what to say when its value changes while focus is
    /// still on it. It does NOT route player input — activation,
    /// keyboard navigation, slider increment etc. stay with the
    /// screen / engine. The element is announcement-only.
    ///
    /// Lifecycle:
    /// - Constructed by a screen during <c>OnAdded</c>, registered
    ///   with the screen's <see cref="ElementRegistry"/> under a
    ///   stable key.
    /// - <see cref="ResolveFocusText"/> called by the focus-diff
    ///   loop when the registry returns this element for a focus
    ///   change.
    /// - <see cref="PollReactiveChange"/> called every poll by the
    ///   screen's <c>OnUpdate</c>; returns a single
    ///   <see cref="Announcement"/> to speak if the element's state
    ///   changed since the last poll, otherwise null.
    /// </summary>
    internal abstract class UIElement
    {
        /// <summary>
        /// Parent container, if any — set by
        /// <see cref="Container.Add"/> when this element is added to
        /// a container. Used by <see cref="FocusContext"/> to walk
        /// the container path during focus diffing so the spoken
        /// announcement only includes container labels the user
        /// has just entered (instead of repeating them on every
        /// focus change within the same group).
        /// </summary>
        public Container? Parent { get; internal set; }


        /// <summary>
        /// Atomic announcements the composer assembles into the
        /// focus-time spoken text. Yield-order within a single
        /// announcement type is preserved; cross-type order is
        /// determined by the element class's
        /// <see cref="AnnouncementOrderAttribute"/>.
        /// </summary>
        public abstract IEnumerable<Announcement> GetFocusAnnouncements();

        /// <summary>
        /// Called every poll on every registered element. Return a
        /// single announcement (typically a
        /// <see cref="Announcements.StatusAnnouncement"/> with just
        /// the new value — "checked", "95 percent") if the element's
        /// state changed since the previous poll. Return null
        /// otherwise. The element owns its own baseline; the
        /// framework just calls this and speaks the result.
        ///
        /// Convention: emit bare value, NOT label-prefixed. The user
        /// changing a control is presumed focused on it, so screen-
        /// reader semantics expect "checked" not
        /// "Use NVDA. Checked." (which would be the focus-time form).
        /// </summary>
        public virtual Announcement? PollReactiveChange() => null;

        /// <summary>
        /// Composes the focus-time text from
        /// <see cref="GetFocusAnnouncements"/> via
        /// <see cref="AnnouncementComposer"/>. Returns an empty
        /// string if no announcements produced text.
        /// </summary>
        public string ResolveFocusText()
            => AnnouncementComposer.Compose(this, GetFocusAnnouncements());
    }
}
