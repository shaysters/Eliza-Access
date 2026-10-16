using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI;
using ElizaAccess.UI.Announcements;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Reads the Chapter Select screen: a list of chapters, with the
    /// currently-selected one expanded inline to show its episodes.
    /// Selecting an episode pushes a confirm dialog ("Returning to
    /// checkpoint will overwrite your current progress…") which is
    /// handled by <see cref="ConfirmDialogView"/>; this view just
    /// owns the chapter-list reading.
    ///
    /// Data model (on the screen instance):
    ///   <c>List&lt;List&lt;EpisodeItem&gt;&gt;</c> — outer index =
    ///     chapter, inner = episodes in that chapter.
    ///   <c>int selectedChapter</c> — which one is currently expanded.
    ///
    /// Hotspot list layout (engine-driven, source-derived):
    ///   chapter 0 row, chapter 1 row, …,
    ///   when chapter K is the selected one, K's chapter row is
    ///     followed by ep[0], ep[1], … of chapter K,
    ///   then chapter K+1 row, …
    ///
    /// Each chapter is modelled as a <see cref="Container"/> whose
    /// own focus speech is the rich row announcement ("Chapter 3.
    /// Expanded, 4 episodes.") and whose children are its episodes
    /// (registered as <see cref="ButtonElement"/>s). Moving from a
    /// chapter row into one of its episodes leverages the container
    /// path-diff so the chapter label doesn't re-announce inside
    /// its own episode list. Moving to a different chapter row
    /// announces it in full.
    /// </summary>
    internal sealed class ChapterSelectView : ScreenView
    {
        public const string ChapterScreenTypeName = "#=qFl48jjBw3vFdtWuUxajLBTzFpr1CdBJlHbB2kXThQ_g=";
        public override string ScreenTypeName => ChapterScreenTypeName;

        // ── Obfuscated names ───────────────────────────────────────────
        private const string ChapterListFieldName     = "#=quyrSMHzYcHaS8LqTCt4RPQ==";
        private const string SelectedChapterFieldName = "#=q7lzP40mUNdqKFcR3$xAuM0yXs50nOGvqe4n$4GIvECE=";
        private const string ItemTitleFieldName       = "#=qXdTSmQTKX9qsN3s$h8kf5w==";

        // ── Reflected handles ──────────────────────────────────────────
        private FieldInfo? _chapterListField;     // List<List<EpisodeItem>>
        private FieldInfo? _selectedChapterField; // int
        private FieldInfo? _itemTitleField;       // LocString (.ToString gives the title)

        protected override void OnAdded(object screen)
        {
            Type t = screen.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            _chapterListField     = t.GetField(ChapterListFieldName,     inst);
            _selectedChapterField = t.GetField(SelectedChapterFieldName, inst);

            // Episode item type is the inner generic of the chapter
            // list (List<List<EpisodeItem>>): outer.GenericArgs[0] is
            // List<EpisodeItem>, its GenericArgs[0] is the item type.
            // Resolved off the field's declared type so the obfuscated
            // type name doesn't need hardcoding.
            Type? tEpisode = null;
            if (_chapterListField != null && _chapterListField.FieldType.IsGenericType)
            {
                Type innerList = _chapterListField.FieldType.GetGenericArguments()[0];
                if (innerList.IsGenericType)
                    tEpisode = innerList.GetGenericArguments()[0];
            }
            if (tEpisode != null) _itemTitleField = tEpisode.GetField(ItemTitleFieldName, inst);

            Log($"[ChapterSelectView] added: chapterList={(_chapterListField != null)} " +
                $"selectedChapter={(_selectedChapterField != null)} " +
                $"itemTitle={(_itemTitleField != null)}");
        }

        protected override void OnFocus()
        {
            // Rebuild the element registry from the live data model.
            // Done on every OnFocus so that any data shifts since the
            // last visit (a chapter unlocking, new episode added)
            // get picked up. Lifecycle cost is negligible — at most
            // a few dozen chapters × a handful of episodes each.
            RegisterChapterElements();

            int chapters = ReadChapterCount();
            int sel = ReadSelectedChapter();
            string selPart = (sel >= 0 && sel < chapters)
                ? Loc.Fmt("chapter.sel_part", sel + 1)
                : "";
            Speech.Speak(Loc.Fmt("chapter.select", chapters, selPart));
            _kbHome.Reset();
            _kbEnd.Reset();
        }

        // Home / End jump to the first / last focusable row (hotspots
        // register in list order: chapter rows, with the expanded
        // chapter's episodes after its row). The normal focus diff
        // announces wherever the cursor lands.
        private readonly KeyEdge _kbHome = new KeyEdge(Keyboard.VK_HOME);
        private readonly KeyEdge _kbEnd  = new KeyEdge(Keyboard.VK_END);

        protected override void OnUpdate()
        {
            if (_kbHome.Pressed()) CursorFocus.MoveCursorToHotspot(0);
            if (_kbEnd.Pressed())  CursorFocus.MoveCursorToHotspot(-1);
        }

        protected override (object? key, string? label) ReadCurrentFocus()
        {
            int slot = CursorFocus.FindHotspotIndex();
            if (slot < 0 || ScreenInstance == null || _chapterListField == null)
                return (null, null);

            try
            {
                if (!(_chapterListField.GetValue(ScreenInstance) is IList outer)) return (null, null);
                int selectedCh = ReadSelectedChapter();
                int remaining  = slot;

                for (int i = 0; i < outer.Count; i++)
                {
                    // Chapter row
                    if (remaining == 0)
                    {
                        var key = ((object)"chapter", i);
                        return (key, ResolveFocusLabel(key, FormatChapterRowFallback(i, outer, selectedCh)));
                    }
                    remaining--;

                    // Episodes are registered only for the expanded chapter,
                    // in data order.
                    if (i == selectedCh && outer[i] is IList eps)
                    {
                        for (int j = 0; j < eps.Count; j++)
                        {
                            if (remaining == 0)
                            {
                                var key = ((object)"episode", i, j);
                                return (key, ResolveFocusLabel(key, FormatEpisodeFallback(i, j, eps[j])));
                            }
                            remaining--;
                        }
                    }
                }
                return (null, null);
            }
            catch { return (null, null); }
        }

        // ── Element registration ───────────────────────────────────────

        private void RegisterChapterElements()
        {
            Elements.Clear();
            if (ScreenInstance == null || _chapterListField == null) return;
            IList? outer;
            try { outer = _chapterListField.GetValue(ScreenInstance) as IList; }
            catch { return; }
            if (outer == null) return;

            for (int i = 0; i < outer.Count; i++)
            {
                int capturedChapterIdx = i;
                var chapterContainer = new ChapterRowElement(
                    label:        $"Chapter {i + 1}",
                    isExpanded:   () => ReadSelectedChapter() == capturedChapterIdx,
                    episodeCount: () => (outer[capturedChapterIdx] is IList eps) ? eps.Count : 0);
                Elements.RegisterByKey(((object)"chapter", i), chapterContainer);

                // Register episodes for EVERY chapter, not just the
                // currently-expanded one. Cheap (just dictionary
                // entries), and avoids needing to re-register when
                // the player expands a different chapter — the
                // engine reshuffles hotspots automatically and our
                // tuple keys keep matching.
                if (outer[i] is IList episodes)
                {
                    for (int j = 0; j < episodes.Count; j++)
                    {
                        string label = FormatEpisodeLabel(i, j, episodes[j]);
                        var episodeBtn = new ButtonElement(label);
                        chapterContainer.Add(episodeBtn);
                        Elements.RegisterByKey(((object)"episode", i, j), episodeBtn);
                    }
                }
            }
            Log($"[ChapterSelectView] registered {Elements.Count} element(s) across {outer.Count} chapter(s)");
        }

        // ── Label formatting ───────────────────────────────────────────

        // Used as the ButtonElement label for an episode AND as the
        // fallback when an element isn't registered for some reason.
        // Format is short ("Episode 3: Title") because the container
        // path-diff already announces "Chapter 3" when the player
        // first enters this chapter's episode list.
        private string FormatEpisodeLabel(int chapterIdx, int episodeIdx, object? ep)
        {
            string title = "";
            if (ep != null && _itemTitleField != null)
            {
                try { title = _itemTitleField.GetValue(ep)?.ToString()?.Trim() ?? ""; }
                catch { }
            }
            if (string.IsNullOrEmpty(title))
                return $"Episode {episodeIdx + 1}";
            return $"Episode {episodeIdx + 1}: {title}";
        }

        // Fallback text if a chapter row's element somehow isn't
        // registered (shouldn't happen in normal use). Mirrors the
        // pre-element format so behaviour degrades gracefully.
        private string FormatChapterRowFallback(int chapterIdx, IList outer, int selectedCh)
        {
            int total = 0;
            if (outer[chapterIdx] is IList eps) total = eps.Count;
            if (total == 0) return $"Chapter {chapterIdx + 1}.";
            if (chapterIdx == selectedCh)
                return $"Chapter {chapterIdx + 1}, expanded. {total} episodes.";
            return $"Chapter {chapterIdx + 1}, collapsed. {total} episodes.";
        }

        // Fallback used when ReadCurrentFocus locates an episode the
        // element registry can't resolve (shouldn't happen — every
        // episode is registered in RegisterChapterElements). Keeps
        // the chapter prefix since without the container path-diff
        // the user wouldn't otherwise know the context.
        private string FormatEpisodeFallback(int chapterIdx, int episodeIdx, object? ep)
        {
            string label = FormatEpisodeLabel(chapterIdx, episodeIdx, ep);
            return $"Chapter {chapterIdx + 1}, {label.Substring(0, 1).ToLower() + label.Substring(1)}.";
        }

        // ── Helpers ────────────────────────────────────────────────────

        private int ReadChapterCount()
        {
            if (ScreenInstance == null || _chapterListField == null) return 0;
            try
            {
                if (_chapterListField.GetValue(ScreenInstance) is IList outer) return outer.Count;
            }
            catch { }
            return 0;
        }

        private int ReadSelectedChapter()
        {
            if (ScreenInstance == null || _selectedChapterField == null) return -1;
            try { return (int)_selectedChapterField.GetValue(ScreenInstance)!; }
            catch { return -1; }
        }

        // ── Nested chapter row element ─────────────────────────────────

        /// <summary>
        /// A chapter row that is also a container for its episodes.
        /// Focus speech includes the expand/collapse state and the
        /// episode count ("Chapter 3. Expanded, 4 episodes.").
        /// Reactive announcement fires "Expanded." when the player
        /// triggers an expansion (the simultaneous collapse of the
        /// previously-expanded chapter is suppressed — same pattern
        /// as RadioButtonElement; the user is focused on the new
        /// selection, not the old one).
        /// </summary>
        private sealed class ChapterRowElement : Container
        {
            private readonly Func<bool> _isExpanded;
            private readonly Func<int>  _episodeCount;
            private bool _hasBaseline;
            private bool _lastExpanded;

            public ChapterRowElement(
                string      label,
                Func<bool>  isExpanded,
                Func<int>   episodeCount)
                : base(label)
            {
                _isExpanded   = isExpanded;
                _episodeCount = episodeCount;
            }

            public override IEnumerable<Announcement> GetFocusAnnouncements()
            {
                yield return new LabelAnnouncement(Label);

                bool exp;
                try { exp = _isExpanded(); } catch { exp = false; }
                int n;
                try { n = _episodeCount(); } catch { n = 0; }

                string state = Loc.Get(exp ? "chapter.expanded" : "chapter.collapsed");
                string status = n == 0 ? state
                              : n == 1 ? Loc.Fmt("chapter.state_one_episode", state)
                              :          Loc.Fmt("chapter.state_n_episodes", state, n);
                yield return new StatusAnnouncement(status);
            }

            public override Announcement? PollReactiveChange()
            {
                bool now;
                try { now = _isExpanded(); } catch { return null; }
                if (!_hasBaseline) { _lastExpanded = now; _hasBaseline = true; return null; }
                if (now == _lastExpanded) return null;
                _lastExpanded = now;
                // Suppress the collapse — only announce the gain.
                // The user just expanded this chapter and is
                // focused on it; the simultaneous deselection of
                // the previously-expanded chapter is implicit.
                return now ? new StatusAnnouncement(Loc.Get("chapter.expanded")) : null;
            }
        }
    }
}
