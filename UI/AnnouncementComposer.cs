using System;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.UI.Announcements;

namespace ElizaAccess.UI
{
    /// <summary>
    /// Turns a UIElement's <see cref="Announcement"/> set into the
    /// final spoken string by ordering parts according to the
    /// element class's <see cref="AnnouncementOrderAttribute"/>.
    ///
    /// Parts are grouped by announcement runtime type, then emitted
    /// in the order declared on the element class. Multiple
    /// announcements of the same type emit in yield order within
    /// their slot. Empty / whitespace text parts are dropped.
    /// </summary>
    internal static class AnnouncementComposer
    {
        // Sensible default if a class forgets the attribute. Matches
        // the canonical screen-reader sequence: what is it, what kind
        // is it, what is its current state, where am I in a list,
        // and any trailing detail.
        private static readonly Type[] DefaultOrder = new[]
        {
            typeof(LabelAnnouncement),
            typeof(TypeAnnouncement),
            typeof(StatusAnnouncement),
            typeof(PositionAnnouncement),
            typeof(TooltipAnnouncement),
        };

        public static string Compose(UIElement element, IEnumerable<Announcement> announcements)
        {
            Type[] order = element.GetType()
                .GetCustomAttribute<AnnouncementOrderAttribute>()?.Order
                ?? DefaultOrder;

            var byType = new Dictionary<Type, List<Announcement>>();
            foreach (Announcement a in announcements)
            {
                Type t = a.GetType();
                if (!byType.TryGetValue(t, out List<Announcement>? bucket))
                {
                    bucket = new List<Announcement>();
                    byType[t] = bucket;
                }
                bucket.Add(a);
            }

            var parts = new List<string>(order.Length);
            foreach (Type t in order)
            {
                if (!byType.TryGetValue(t, out List<Announcement>? bucket)) continue;
                foreach (Announcement a in bucket)
                {
                    if (!string.IsNullOrWhiteSpace(a.Text)) parts.Add(a.Text);
                }
            }

            if (parts.Count == 0) return string.Empty;
            return string.Join(". ", parts) + ".";
        }
    }
}
