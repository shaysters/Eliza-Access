using System.Collections.Generic;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// A clickable action with no persistent state. Speaks "{label}.
    /// Button." on focus. Has no reactive announcement — buttons
    /// don't change state between activations.
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(TypeAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class ButtonElement : UIElement
    {
        private readonly string  _label;
        private readonly string? _tooltip;

        public ButtonElement(string label, string? tooltip = null)
        {
            _label   = label ?? string.Empty;
            _tooltip = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            yield return new TypeAnnouncement("button");
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }
    }
}
