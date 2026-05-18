using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Speaks the chapter title when the engine fires a
    /// <c>BeginChapter</c> script event. The visual is the "Chapter N
    /// — Title" overlay the engine fades onto the screen at chapter
    /// transitions; a sighted player reads it there, and we surface
    /// the same content for screen readers.
    ///
    /// Data comes off the current script event (WorldScreen.method_0()
    /// returns a GClass10 tagged union). When its discriminator enum
    /// reads <c>BeginChapter</c> (= 0, the first Enum0 value), the
    /// instance's first <c>int</c> field is the chapter number and
    /// the first LocalizedText (GClass23) field is the title.
    ///
    /// Dedup by (chapter number, title) so the announcement fires
    /// once per chapter transition even though the BeginChapter event
    /// may persist for several poll ticks before the engine moves on
    /// to ShowChapterTitle / the first dialogue line.
    /// </summary>
    internal sealed class ChapterTitleSubView : SubView
    {
        // WorldScreen.method_0() → current ScriptEvent (GClass10).
        private const string ScriptEventMethod        = "#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=";
        // LocalizedText.ToString equivalent (smethod_36 in clean,
        // already used by ChatAppSubView / ElizaAppSubView).
        private const string LocalizedTextToStringName = "#=qTivbHO_rGrDAcTGXa1E4rg==";
        // ScriptCharacter.name field — used to resolve the
        // LocalizedText type without hardcoding its obfuscated name.
        private const string CharacterNameField        = "#=qVcZOuQei28m8v7C2MtsdYA==";

        private const int BeginChapterEnumValue = 0;

        // ── Reflected handles ──────────────────────────────────────────
        private MethodInfo? _scriptEventMethod;
        private FieldInfo?  _eventEnumField;     // GClass10 discriminator
        private FieldInfo?  _chapterNumField;    // GClass10.int_0
        private FieldInfo?  _chapterTitleField;  // GClass10.gclass23_0 (LocalizedText)
        private MethodInfo? _localizedToString;

        // ── Per-activation state ───────────────────────────────────────
        private int     _lastChapterNum   = int.MinValue;
        private string? _lastChapterTitle;

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

            // Discriminator + payload fields on the script event type.
            // First int field = int_0 (chapter number). First field of
            // the LocalizedText type = gclass23_0 (title). Only enum-
            // typed field = the Enum0 discriminator.
            Type tEvent = _scriptEventMethod.ReturnType;
            Type? tLocalizedText = ResolveLocalizedTextType(inst);
            foreach (FieldInfo f in tEvent.GetFields(inst))
            {
                if (_eventEnumField == null && f.FieldType.IsEnum)
                    _eventEnumField = f;
                else if (_chapterNumField == null && f.FieldType == typeof(int))
                    _chapterNumField = f;
                else if (_chapterTitleField == null
                         && tLocalizedText != null && f.FieldType == tLocalizedText)
                    _chapterTitleField = f;
            }
            if (tLocalizedText != null)
            {
                _localizedToString = tLocalizedText.GetMethod(
                    LocalizedTextToStringName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
            }

            Log($"[ChapterTitleSub] init: scriptEvent={(_scriptEventMethod != null)} " +
                $"enum={(_eventEnumField != null)} chapterNum={(_chapterNumField != null)} " +
                $"title={(_chapterTitleField != null)} toString={(_localizedToString != null)}");
        }

        // LocalizedText (GClass23 in clean) is the type of
        // ScriptCharacter.name. Resolving via that anchor avoids
        // hardcoding the obfuscated type name.
        private static Type? ResolveLocalizedTextType(BindingFlags inst)
        {
            Type? tChar = Nagoya.FindType("ScriptCharacter");
            if (tChar == null) return null;
            FieldInfo? nameField = tChar.GetField(CharacterNameField, inst);
            return nameField?.FieldType;
        }

        public override bool IsActive => _scriptEventMethod != null;

        public override void OnUpdate()
        {
            if (_scriptEventMethod == null || _eventEnumField == null
                || _chapterNumField == null || _chapterTitleField == null) return;

            object? ev;
            try { ev = _scriptEventMethod.Invoke(Parent, null); }
            catch { return; }
            if (ev == null) return;

            int enumVal;
            try { enumVal = Convert.ToInt32(_eventEnumField.GetValue(ev)); }
            catch { return; }
            if (enumVal != BeginChapterEnumValue) return;

            int chapterNum;
            try { chapterNum = (int)_chapterNumField.GetValue(ev)!; }
            catch { return; }

            string title = "";
            try
            {
                object? titleObj = _chapterTitleField.GetValue(ev);
                if (titleObj != null)
                {
                    title = _localizedToString != null
                        ? (string)_localizedToString.Invoke(titleObj, null)! ?? ""
                        : titleObj.ToString() ?? "";
                    title = title.Trim();
                }
            }
            catch { }

            if (chapterNum == _lastChapterNum && title == (_lastChapterTitle ?? "")) return;
            _lastChapterNum   = chapterNum;
            _lastChapterTitle = title;

            string spoken = string.IsNullOrEmpty(title)
                ? $"Chapter {chapterNum + 1}."
                : $"Chapter {chapterNum + 1}. {title}.";
            Speech.Speak(spoken);
            Log($"[ChapterTitleSub] {spoken}");
        }
    }
}
