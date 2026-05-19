namespace ElizaAccess.UI.Announcements
{
    /// <summary>The element's current value or state — "checked",
    /// "unchecked", "95 percent", "selected", "English". Also emitted
    /// on its own (no label, no type) when the element's value changes
    /// while focus is still on it (the user pressed Enter on a
    /// checkbox; "checked" alone is the expected screen-reader
    /// feedback).</summary>
    internal sealed class StatusAnnouncement : Announcement
    {
        public StatusAnnouncement(string? text) : base(text) { }
    }
}
