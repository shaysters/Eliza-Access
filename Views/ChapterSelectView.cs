using System;
using System.Collections;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.Common.Patches;

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
    /// Focus is read via <see cref="CursorFocus"/>; the focused hotspot
    /// index is mapped back to (chapter, episode?) by walking the
    /// chapter list in the same order the engine renders.
    /// HotspotLabelCapture isn't reliable on this screen — V2 went the
    /// data-model route and it worked, so we follow.
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

        // ── Per-activation state ───────────────────────────────────────
        private int _lastSelectedChapter = -1;

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
            int chapters = ReadChapterCount();
            int sel = ReadSelectedChapter();
            string selPart = (sel >= 0 && sel < chapters)
                ? $" Chapter {sel + 1} expanded."
                : "";
            Speech.Speak($"Chapter Select. {chapters} chapters.{selPart}");
            _lastSelectedChapter = sel;
        }

        protected override void OnUpdate()
        {
            int sel = ReadSelectedChapter();
            if (sel != _lastSelectedChapter && sel >= 0)
            {
                _lastSelectedChapter = sel;
                Log($"[ChapterSelectView] selectedChapter → {sel + 1}");
            }
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
                        // Use (kind, i) so a (chapter,2) focus doesn't
                        // collide with an episode (chapter=2,episode=0).
                        return (("chapter", i), FormatChapterRow(i, outer, selectedCh));
                    }
                    remaining--;

                    // Episodes are registered only for the expanded chapter,
                    // in data order.
                    if (i == selectedCh && outer[i] is IList eps)
                    {
                        for (int j = 0; j < eps.Count; j++)
                        {
                            if (remaining == 0)
                                return (("episode", i, j), FormatEpisode(i, j, eps[j]));
                            remaining--;
                        }
                    }
                }
                return (null, null);
            }
            catch { return (null, null); }
        }

        // ── Label formatting ───────────────────────────────────────────

        private string FormatChapterRow(int chapterIdx, IList outer, int selectedCh)
        {
            int total = 0;
            if (outer[chapterIdx] is IList eps) total = eps.Count;
            if (total == 0) return $"Chapter {chapterIdx + 1}.";
            if (chapterIdx == selectedCh)
                return $"Chapter {chapterIdx + 1}, expanded. {total} episodes.";
            return $"Chapter {chapterIdx + 1}, collapsed. {total} episodes.";
        }

        private string FormatEpisode(int chapterIdx, int episodeIdx, object? ep)
        {
            string title = "";
            if (ep != null && _itemTitleField != null)
            {
                try { title = _itemTitleField.GetValue(ep)?.ToString()?.Trim() ?? ""; }
                catch { }
            }
            if (string.IsNullOrEmpty(title))
                return $"Chapter {chapterIdx + 1}, episode {episodeIdx + 1}.";
            return $"Chapter {chapterIdx + 1}, episode {episodeIdx + 1}: {title}.";
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
    }
}
