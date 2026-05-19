namespace ElizaAccess.UI.Announcements
{
    /// <summary>Position within a list-like container — "3 of 8",
    /// "item 2 of 4". Conventionally added by container elements
    /// (or by the focus-diff loop when the parent is a known
    /// navigable container) rather than by the element itself.</summary>
    internal sealed class PositionAnnouncement : Announcement
    {
        public PositionAnnouncement(string? text) : base(text) { }
    }
}
