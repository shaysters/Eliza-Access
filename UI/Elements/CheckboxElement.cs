using System;
using System.Collections.Generic;
using ElizaAccess.Common;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// Two-state toggle. Focus says "{label}. Checkbox. Checked." /
    /// "{label}. Checkbox. Unchecked." Reactive announcement when
    /// the state flips between polls is bare "Checked." / "Unchecked."
    /// — the user is presumed focused on the box they just toggled.
    ///
    /// The element pulls state via the supplied <c>getState</c>
    /// delegate; it never writes. Toggling is the screen / engine's
    /// responsibility.
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(TypeAnnouncement),
                       typeof(StatusAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class CheckboxElement : UIElement
    {
        private readonly string     _label;
        private readonly Func<bool> _getState;
        private readonly string?    _tooltip;
        private bool _hasBaseline;
        private bool _lastState;

        public CheckboxElement(string label, Func<bool> getState, string? tooltip = null)
        {
            _label    = label ?? string.Empty;
            _getState = getState ?? throw new ArgumentNullException(nameof(getState));
            _tooltip  = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            yield return new TypeAnnouncement(Loc.Get("type.checkbox"));
            yield return new StatusAnnouncement(Loc.Get(_getState() ? "status.checked" : "status.unchecked"));
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }

        public override Announcement? PollReactiveChange()
        {
            bool now;
            try { now = _getState(); } catch { return null; }
            if (!_hasBaseline) { _lastState = now; _hasBaseline = true; return null; }
            if (now == _lastState) return null;
            _lastState = now;
            return new StatusAnnouncement(Loc.Get(now ? "status.checked" : "status.unchecked"));
        }
    }
}
