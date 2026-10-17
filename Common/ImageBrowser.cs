using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// J / L step through the images currently on screen that we have
    /// descriptions for, speaking a short label; K speaks the full
    /// description of the current one. (Not A/S/D: plain A is the
    /// game's own auto-advance toggle in WorldScreen.) On a controller the
    /// right stick does the same: left / right step, up describes (the
    /// game never reads the right stick). Moving past either end is silent.
    ///
    /// What's on screen depends on context:
    /// - A Messages conversation is open → the partner's avatar, then the
    ///   phone owner's (contacts without a picture are skipped).
    /// - WorldScreen is on top (or under a look-around text popup) →
    ///   the background, then the characters
    ///   left to right (<see cref="SceneReader"/>).
    /// - Anything else → nothing.
    ///
    /// The list is rebuilt on every key press, so it always matches the
    /// picture. The cursor remembers which image it was on; if that
    /// image is gone (new location, character left) it starts over at
    /// the first.
    /// </summary>
    internal sealed class ImageBrowser
    {
        private readonly struct Item
        {
            public readonly string Id, Label;
            public readonly string? Description;
            public Item(string id, string label, string? description)
            { Id = id; Label = label; Description = description; }
        }

        private readonly SpeechEngine _speech;
        private readonly KeyEdge _kbPrev     = new KeyEdge(Keyboard.VK_J, PadDirection.Left);
        private readonly KeyEdge _kbNext     = new KeyEdge(Keyboard.VK_L, PadDirection.Right);
        private readonly KeyEdge _kbDescribe = new KeyEdge(Keyboard.VK_K, PadDirection.Up);
        private string? _currentId;

        public ImageBrowser(SpeechEngine speech) { _speech = speech; }

        public void Poll()
        {
            bool prev = _kbPrev.Pressed();
            bool next = _kbNext.Pressed();
            bool describe = _kbDescribe.Pressed();
            if (!prev && !next && !describe) return;

            List<Item> items = BuildItems();
            if (items.Count == 0) return;
            int idx = items.FindIndex(i => i.Id == _currentId);

            if (describe)
            {
                Item item = items[idx < 0 ? 0 : idx];
                _currentId = item.Id;
                _speech.Speak(item.Description ?? Loc.Get("image.no_description"));
                return;
            }
            if (prev == next) return;

            int target = idx < 0 ? 0 : idx + (next ? 1 : -1);
            if (target < 0 || target >= items.Count) return;
            _currentId = items[target].Id;
            _speech.Speak(items[target].Label);
        }

        private static List<Item> BuildItems()
        {
            var items = new List<Item>();

            string? partner = PhoneAvatarState.PartnerKey;
            if (partner != null)
            {
                AddAvatar(items, partner);
                string? owner = PhoneAvatarState.OwnerKey;
                if (owner != null && owner != partner) AddAvatar(items, owner);
                return items;
            }

            object? top = VisibleWorldScreen();
            if (top == null) return items;

            string? location = SceneReader.LocationKey(top);
            if (location != null)
            {
                string? label = SceneReader.LocationLabel(location);
                if (label != null)
                    items.Add(new Item("bg:" + location, Loc.Fmt("image.background_label", label),
                                       SceneReader.LocationDescription(location)));
            }
            List<SceneReader.Figure>? figures = SceneReader.Figures(top, location);
            if (figures != null)
                foreach (var f in figures)
                    items.Add(new Item("fig:" + f.Key,
                        Loc.Fmt("image.figure_label", SceneReader.FigureName(f), SceneReader.PositionWord(f.Slot)),
                        SceneReader.FigureDescription(f)));
            return items;
        }

        // The WorldScreen whose scene is on screen: either it's on top, or
        // the top is a look-around text popup (FakeAppScreen) drawn over
        // it — the scene stays fully visible behind that text. A popup
        // over anything else (e.g. the phone) doesn't count.
        private static object? VisibleWorldScreen()
        {
            System.Collections.IList? stack = ScreenStackBinding.GetStack();
            if (stack == null || stack.Count == 0) return null;
            object? top = stack[stack.Count - 1];
            if (top?.GetType().Name == "WorldScreen") return top;
            if (top?.GetType().Name == "FakeAppScreen" && stack.Count >= 2)
            {
                object? below = stack[stack.Count - 2];
                if (below?.GetType().Name == "WorldScreen") return below;
            }
            return null;
        }

        private static void AddAvatar(List<Item> items, string key)
        {
            string? desc = Loc.TryGet($"image.avatar.{key}.desc");
            if (desc == null) return;   // contact has no picture
            string name = Loc.TryGet($"image.avatar.{key}.name") ?? key;
            items.Add(new Item("avatar:" + key, Loc.Fmt("image.avatar_label", name), desc));
        }
    }
}
