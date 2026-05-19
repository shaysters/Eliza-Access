using System;

namespace ElizaAccess.UI
{
    /// <summary>
    /// Declares the spoken order of an element class's announcements.
    /// The <see cref="AnnouncementComposer"/> reads this off the
    /// <see cref="UIElement"/> subclass and emits parts in the listed
    /// type order. Types not listed are dropped; types listed but not
    /// yielded are skipped silently.
    ///
    /// Example: a checkbox that speaks as "Use NVDA. Checkbox. Checked."
    /// is configured with
    /// <c>[AnnouncementOrder(typeof(LabelAnnouncement),
    ///                       typeof(TypeAnnouncement),
    ///                       typeof(StatusAnnouncement))]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    internal sealed class AnnouncementOrderAttribute : Attribute
    {
        public Type[] Order { get; }
        public AnnouncementOrderAttribute(params Type[] order) { Order = order; }
    }
}
