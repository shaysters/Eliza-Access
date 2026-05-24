using System.Collections.Generic;
using ElizaAccess.Common;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// A clickable action. Speaks "{label}, Button" on focus, or
    /// "{label}, Button, {status}" when an optional status is set —
    /// useful for buttons that can be in a non-interactive sub-state
    /// like "locked" or "disabled" without semantically becoming a
    /// different control type. Reactive announcement is no-op
    /// because buttons don't carry persistent state worth diffing
    /// (the status is set at construction time, not polled).
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(TypeAnnouncement),
                       typeof(StatusAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class ButtonElement : UIElement
    {
        private readonly string  _label;
        private readonly string? _status;
        private readonly string? _tooltip;

        public ButtonElement(string label, string? status = null, string? tooltip = null)
        {
            _label   = label ?? string.Empty;
            _status  = status;
            _tooltip = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            yield return new TypeAnnouncement(Loc.Get("type.button"));
            if (!string.IsNullOrEmpty(_status))
                yield return new StatusAnnouncement(_status);
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }
    }
}
