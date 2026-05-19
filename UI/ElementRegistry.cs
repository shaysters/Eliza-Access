using System;
using System.Collections.Generic;

namespace ElizaAccess.UI
{
    /// <summary>
    /// Per-screen lookup of "the focus key currently reported by this
    /// screen → which <see cref="UIElement"/> describes it." The key
    /// scheme is the screen's choice — it should reuse whatever stable
    /// identifier the screen's existing focus model already produces:
    /// <list type="bullet">
    ///   <item>Cursor-on-hotspot screens: the captured label string
    ///         (already proven robust by <c>HotspotLabelCapture</c>
    ///         across hotspot-list layout changes).</item>
    ///   <item>Custom-focus screens (ChapterSelectView,
    ///         ExplorationSubView): the same composite key they use
    ///         today — e.g. <c>("chapter", i)</c>,
    ///         <c>("episode", i, j)</c>, integer nav-list indices.</item>
    /// </list>
    ///
    /// Keys use <see cref="object.Equals(object)"/> / GetHashCode for
    /// matching, so tuples and strings work directly without
    /// boilerplate wrapping.
    /// </summary>
    internal sealed class ElementRegistry
    {
        private readonly Dictionary<object, UIElement> _byKey = new Dictionary<object, UIElement>();

        public void RegisterByKey(object key, UIElement element)
        {
            if (key == null)     throw new ArgumentNullException(nameof(key));
            if (element == null) throw new ArgumentNullException(nameof(element));
            _byKey[key] = element;
        }

        public UIElement? GetByKey(object? key)
        {
            if (key == null) return null;
            return _byKey.TryGetValue(key, out UIElement? el) ? el : null;
        }

        /// <summary>All registered elements in registration order
        /// (Dictionary preserves insertion order on .NET Framework
        /// in practice; we don't rely on it for correctness, just
        /// for predictable reactive-change traversal).</summary>
        public IEnumerable<UIElement> All => _byKey.Values;

        public int Count => _byKey.Count;

        public void Clear() => _byKey.Clear();
    }
}
