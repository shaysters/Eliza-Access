using System;
using System.Collections.Generic;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.UI
{
    /// <summary>
    /// Tracks the container path to the last-announced element and
    /// computes which containers along a new path are newly entered
    /// since the last announcement. The screen prepends those
    /// containers' labels to the focus speech — so the player hears
    /// the group context the first time they enter it, then just
    /// the child as they move between siblings.
    ///
    /// Reset whenever the screen loses focus or the cursor leaves
    /// every registered element (a non-element bare-label hotspot,
    /// or no hotspot at all) — that way returning to a previously-
    /// visited container re-announces its label.
    /// </summary>
    internal sealed class FocusContext
    {
        private List<Container> _lastPath = new List<Container>();

        /// <summary>
        /// Returns the containers on the path from the root to
        /// <paramref name="element"/> that are NOT on the last
        /// announced path. Order is outermost-first, which is the
        /// natural reading order ("Auto-Advance. Off." not
        /// "Off. Auto-Advance.").
        /// </summary>
        public IReadOnlyList<Container> NewContainersOnPathTo(UIElement element)
        {
            var newPath = BuildPath(element);
            int divergeIdx = FindDivergenceIndex(_lastPath, newPath);
            if (divergeIdx >= newPath.Count) return Array.Empty<Container>();
            return newPath.GetRange(divergeIdx, newPath.Count - divergeIdx);
        }

        /// <summary>Update the tracked path. Call after the screen
        /// has spoken the focus announcement for <paramref name="element"/>
        /// so the next diff compares against this baseline.</summary>
        public void UpdatePathTo(UIElement? element)
        {
            _lastPath = element != null ? BuildPath(element) : new List<Container>();
        }

        /// <summary>Forget the tracked path entirely. The next
        /// element focus will re-announce every container in its
        /// path.</summary>
        public void Reset() => _lastPath = new List<Container>();

        // Root-first walk: ancestor.ancestor.parent → root.
        private static List<Container> BuildPath(UIElement element)
        {
            var path = new List<Container>();
            for (Container? c = element.Parent; c != null; c = c.Parent)
                path.Add(c);
            path.Reverse();
            return path;
        }

        private static int FindDivergenceIndex(List<Container> oldPath, List<Container> newPath)
        {
            int min = Math.Min(oldPath.Count, newPath.Count);
            for (int i = 0; i < min; i++)
            {
                if (!ReferenceEquals(oldPath[i], newPath[i])) return i;
            }
            return min;
        }
    }
}
