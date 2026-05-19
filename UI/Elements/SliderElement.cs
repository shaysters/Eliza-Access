using System;
using System.Collections.Generic;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// Continuous-value control. Focus says "{label}. Slider.
    /// {formatted value}." Reactive announcement on value change is
    /// the bare formatted value — "95 percent" — matching screen-
    /// reader convention for in-place adjustment.
    ///
    /// The format delegate decides the presentation (percent, raw int,
    /// "low/medium/high", etc.) so the element stays unit-agnostic.
    /// The reader pulls the value via the supplied getter and never
    /// writes; the screen handles the actual increment / decrement.
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(TypeAnnouncement),
                       typeof(StatusAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class SliderElement : UIElement
    {
        private readonly string             _label;
        private readonly Func<float>        _getValue;
        private readonly Func<float, string> _format;
        private readonly string?            _tooltip;
        private string? _lastFormatted;

        public SliderElement(
            string label,
            Func<float> getValue,
            Func<float, string> format,
            string? tooltip = null)
        {
            _label    = label ?? string.Empty;
            _getValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
            _format   = format   ?? throw new ArgumentNullException(nameof(format));
            _tooltip  = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            yield return new TypeAnnouncement("slider");
            yield return new StatusAnnouncement(SafeFormat());
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }

        public override Announcement? PollReactiveChange()
        {
            string now = SafeFormat();
            if (_lastFormatted == null) { _lastFormatted = now; return null; }
            if (now == _lastFormatted) return null;
            _lastFormatted = now;
            return new StatusAnnouncement(now);
        }

        private string SafeFormat()
        {
            try { return _format(_getValue()); }
            catch { return string.Empty; }
        }
    }
}
