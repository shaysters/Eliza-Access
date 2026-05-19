namespace ElizaAccess.UI.Announcements
{
    /// <summary>Supplementary description spoken after the primary
    /// label/type/status. Used for hover-tooltips and any longer
    /// explanatory text the player might want trailing the
    /// fundamentals.</summary>
    internal sealed class TooltipAnnouncement : Announcement
    {
        public TooltipAnnouncement(string? text) : base(text) { }
    }
}
