using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Speaks the "Chapter N" card the engine shows on a black screen at
    /// chapter transitions.
    ///
    /// The card is drawn by WorldScreen.method_6 while the current script
    /// event (WorldScreen.method_0() → GClass10) is <c>ShowChapterTitle</c>.
    /// That event blocks script advancement (the advance loop stops on it),
    /// so it stays current for the whole time the card is up and a poll
    /// reliably sees it. (The earlier <c>BeginChapter</c> event is consumed
    /// within the same frame it's reached and is almost never observable —
    /// and its LocalizedText payload is the checkpoint name, not a title.)
    ///
    /// The card's text is just <c>string.Format(Loc("Chapter {0}"),
    /// phoneState.int_0 + 1)</c> — no subtitle — so we read the same
    /// chapter field and the same localized format string.
    ///
    /// Speaks once per appearance of the card; a reload that shows the
    /// same chapter's card again re-announces it.
    /// </summary>
    internal sealed class ChapterTitleSubView : SubView
    {
        // WorldScreen.method_0() → current ScriptEvent (GClass10).
        private const string ScriptEventMethod   = "#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=";
        // WorldScreen.phoneState_0 (same field ChatAppSubView reads).
        private const string PhoneStateField     = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        // PhoneState.int_0 — current chapter index, 0-based.
        private const string ChapterIndexField   = "#=qlSSqADYiwqAiTqbJgR43gaP_RGb3N92I4fqYfUGCImA=";

        // GClass10.Enum0: BeginChapter = 0, ShowChapterTitle = 1.
        private const int ShowChapterTitleEnumValue = 1;

        // ── Reflected handles ──────────────────────────────────────────
        private MethodInfo? _scriptEventMethod;
        private FieldInfo?  _eventEnumField;     // GClass10 discriminator
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _chapterIndexField;

        // True while the card is up and has been spoken.
        private bool _announced;

        public override void OnInit()
        {
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            Type tWS = Parent.GetType();
            foreach (MethodInfo m in tWS.GetMethods(inst))
            {
                if (m.Name == ScriptEventMethod && m.GetParameters().Length == 0)
                {
                    _scriptEventMethod = m;
                    break;
                }
            }
            if (_scriptEventMethod == null)
            {
                Log("[ChapterTitleSub] init: scriptEventMethod NOT FOUND — sub disabled");
                return;
            }

            // The only enum-typed field on the script event is the Enum0
            // discriminator.
            foreach (FieldInfo f in _scriptEventMethod.ReturnType.GetFields(inst))
            {
                if (f.FieldType.IsEnum) { _eventEnumField = f; break; }
            }

            _phoneStateField   = tWS.GetField(PhoneStateField, inst);
            _chapterIndexField = _phoneStateField?.FieldType.GetField(ChapterIndexField, inst);

            Log($"[ChapterTitleSub] init: enum={(_eventEnumField != null)} " +
                $"phoneState={(_phoneStateField != null)} chapterIndex={(_chapterIndexField != null)}");
        }

        public override bool IsActive => _scriptEventMethod != null && _eventEnumField != null;

        public override void OnUpdate()
        {
            if (!IsChapterCardShowing())
            {
                _announced = false;
                return;
            }
            if (_announced) return;
            _announced = true;

            string format = Localizer.TryLocalizeStringKey("Chapter {0}") ?? "Chapter {0}";
            int? index = ReadChapterIndex();
            string spoken;
            try
            {
                spoken = index.HasValue
                    ? string.Format(format, index.Value + 1)
                    : format.Replace("{0}", "").Trim();
            }
            catch (FormatException) { spoken = $"Chapter {index + 1}"; }

            Speech.Speak(spoken);
            Log($"[ChapterTitleSub] {spoken}");
        }

        private bool IsChapterCardShowing()
        {
            try
            {
                object? ev = _scriptEventMethod!.Invoke(Parent, null);
                if (ev == null) return false;
                return Convert.ToInt32(_eventEnumField!.GetValue(ev)) == ShowChapterTitleEnumValue;
            }
            catch { return false; }
        }

        private int? ReadChapterIndex()
        {
            if (_phoneStateField == null || _chapterIndexField == null) return null;
            try
            {
                object? phoneState = _phoneStateField.GetValue(Parent);
                return phoneState == null ? (int?)null : (int)_chapterIndexField.GetValue(phoneState)!;
            }
            catch { return null; }
        }
    }
}
