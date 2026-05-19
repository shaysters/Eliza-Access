using System;
using System.Collections.Generic;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// Read-only display value with no interaction. Useful for
    /// header fields like "Proxy Level 5" or status readouts the
    /// player can focus on but not modify. Skips
    /// <see cref="TypeAnnouncement"/> entirely — there's no
    /// interactive type to announce, just label + current value.
    ///
    /// The value getter is optional. When null the element acts as
    /// a plain text label; when present, its current return value
    /// becomes the status part and reactive change announcements
    /// fire when the formatted value differs from the last poll.
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(StatusAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class LabelElement : UIElement
    {
        private readonly string         _label;
        private readonly Func<string>?  _getValue;
        private readonly string?        _tooltip;
        private string? _lastValue;

        public LabelElement(string label, Func<string>? getValue = null, string? tooltip = null)
        {
            _label    = label ?? string.Empty;
            _getValue = getValue;
            _tooltip  = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            if (_getValue != null)
            {
                string v;
                try { v = _getValue() ?? string.Empty; } catch { v = string.Empty; }
                if (!string.IsNullOrEmpty(v)) yield return new StatusAnnouncement(v);
            }
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }

        public override Announcement? PollReactiveChange()
        {
            if (_getValue == null) return null;
            string now;
            try { now = _getValue() ?? string.Empty; } catch { return null; }
            if (_lastValue == null) { _lastValue = now; return null; }
            if (now == _lastValue) return null;
            _lastValue = now;
            return new StatusAnnouncement(now);
        }
    }
}
