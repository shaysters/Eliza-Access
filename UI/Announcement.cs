namespace ElizaAccess.UI
{
    /// <summary>
    /// Atomic piece of focus-time speech for a <see cref="UIElement"/>.
    /// An element yields a small set of these from
    /// <see cref="UIElement.GetFocusAnnouncements"/>; the
    /// <see cref="AnnouncementComposer"/> orders them by type per the
    /// element class's <see cref="AnnouncementOrderAttribute"/> and
    /// joins the parts into the final spoken text.
    ///
    /// Subclasses are marker types — the composer keys on the runtime
    /// type, not on any property of the announcement itself, so each
    /// distinct slot in the spoken phrase (label / type / status /
    /// position / tooltip) has its own subclass.
    /// </summary>
    internal abstract class Announcement
    {
        public string Text { get; }
        protected Announcement(string? text) { Text = text ?? string.Empty; }
    }
}
