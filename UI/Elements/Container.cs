using System.Collections.Generic;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// Groups related <see cref="UIElement"/>s under a shared label
    /// (e.g. an "Auto-Advance" row holds three Off / Normal / Slower
    /// radio buttons). When the player focuses one of the children,
    /// the container's label gets prepended to the spoken
    /// announcement — but only the first time focus enters this
    /// container. Subsequent moves between siblings inside it
    /// announce only the child, matching how a screen-reader
    /// traverses a standard form: "Auto-Advance. Off. Radio
    /// button. Selected." on entry, then "Normal. Radio button."
    /// as the player tabs through.
    ///
    /// The path-diff that decides "is this container new?" lives in
    /// <see cref="FocusContext"/>, owned by <see cref="Common.ScreenView"/>.
    ///
    /// Containers are themselves UIElements so they can nest (a
    /// container inside another container), though Eliza screens
    /// rarely need more than one level today.
    /// </summary>
    internal abstract class Container : UIElement
    {
        private readonly List<UIElement> _children = new List<UIElement>();

        /// <summary>Spoken label for this container — read aloud
        /// when focus crosses into it from outside. Null/empty
        /// suppresses the announcement (useful for purely logical
        /// groupings that shouldn't add speech noise).</summary>
        public string? Label { get; }

        public IReadOnlyList<UIElement> Children => _children;

        protected Container(string? label) { Label = label; }

        public void Add(UIElement child)
        {
            _children.Add(child);
            child.Parent = this;
        }

        /// <summary>Direct focus on the container — if the engine
        /// ever surfaces one as the focus target — speaks just the
        /// label. Children get their own announcements through the
        /// usual cursor-on-hotspot path.</summary>
        public override IEnumerable<Announcement> GetFocusAnnouncements()
        {
            if (!string.IsNullOrEmpty(Label))
                yield return new LabelAnnouncement(Label);
        }
    }
}
