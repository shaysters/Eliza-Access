using System;
using System.Collections.Generic;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// One option in a mutually-exclusive set. Each option in the
    /// set is its own <see cref="RadioButtonElement"/>; the
    /// selection state is read via the <c>isSelected</c> predicate
    /// (typically <c>() =&gt; settings.Language == EnglishValue</c>).
    /// Focus says "{label}. Radio button. Selected." /
    /// "... Not selected."
    ///
    /// Reactive announcement fires only on the FALSE → TRUE
    /// transition (became selected) — a radio button's TRUE → FALSE
    /// transition is implicit in the new selection's TRUE event and
    /// announcing both would be noise: "Not selected. Selected."
    /// every time the player picks a new option.
    ///
    /// The <c>isSelected</c> predicate is optional. Pass
    /// <c>null</c> when the group is known to be a radio set but
    /// the backing field hasn't been identified yet — focus speech
    /// still announces "Radio button" so the player knows the type,
    /// but the status part is omitted instead of guessing "not
    /// selected" (which would be wrong half the time).
    /// </summary>
    [AnnouncementOrder(typeof(LabelAnnouncement),
                       typeof(TypeAnnouncement),
                       typeof(StatusAnnouncement),
                       typeof(TooltipAnnouncement))]
    internal sealed class RadioButtonElement : UIElement
    {
        private readonly string      _label;
        private readonly Func<bool>? _isSelected;
        private readonly string?     _tooltip;
        private bool _hasBaseline;
        private bool _lastState;

        public RadioButtonElement(string label, Func<bool>? isSelected, string? tooltip = null)
        {
            _label      = label ?? string.Empty;
            _isSelected = isSelected;
            _tooltip    = tooltip;
        }

        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            yield return new LabelAnnouncement(_label);
            yield return new TypeAnnouncement("radio button");
            if (_isSelected != null)
            {
                bool sel;
                try { sel = _isSelected(); }
                catch { sel = false; }
                yield return new StatusAnnouncement(sel ? "selected" : "not selected");
            }
            if (!string.IsNullOrEmpty(_tooltip))
                yield return new TooltipAnnouncement(_tooltip);
        }

        public override Announcement? PollReactiveChange()
        {
            if (_isSelected == null) return null;
            bool now;
            try { now = _isSelected(); } catch { return null; }
            if (!_hasBaseline) { _lastState = now; _hasBaseline = true; return null; }
            if (now == _lastState) return null;
            _lastState = now;
            // Only announce the gain. The losing option's TRUE→FALSE
            // is implicit in the new option's announcement.
            return now ? new StatusAnnouncement("selected") : null;
        }
    }
}
