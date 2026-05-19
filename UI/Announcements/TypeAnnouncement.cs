namespace ElizaAccess.UI.Announcements
{
    /// <summary>The element's kind — "button", "checkbox", "slider",
    /// "radio button", etc. The string is what gets spoken; conventionally
    /// lowercase, one or two words.</summary>
    internal sealed class TypeAnnouncement : Announcement
    {
        public TypeAnnouncement(string? text) : base(text) { }
    }
}
