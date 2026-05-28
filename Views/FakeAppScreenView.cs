using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for <c>FakeAppScreen</c>, the simple one-line-
    /// at-a-time text screen that's pushed when the player clicks an
    /// exploration-mode entry on WorldScreen (Evelyn's thoughts, NPC
    /// snippets) — see <c>WorldScreen.cs:1795</c>:
    /// <code>
    /// ScreenStack.smethod_11(new FakeAppScreen(entry.gclass23_0));
    /// </code>
    /// The whole point of this screen is to step through a small array of
    /// localized strings, one per A/SPACE press. Structure from
    /// <c>FakeAppScreen.cs</c>:
    /// <code>
    /// private readonly GClass23[] gclass23_0;     // lines to display
    /// private int int_0;                          // current line index
    /// (plus floats / Maybe&lt;float&gt; for fade timers — irrelevant here)
    /// </code>
    /// V2AutoObserver doesn't pick these up because it reads WorldScreen's
    /// dialogue-line field — but FakeAppScreen is a separate screen
    /// pushed on top, and the lines live on the screen instance itself.
    ///
    /// Focus model: the "focused thing" is the current line. Key is the
    /// line index, label is its text. Each time int_0 advances, the
    /// framework's focus-change detection fires <c>OnFocusChanged</c> and
    /// we speak the new line.
    /// </summary>
    internal sealed class FakeAppScreenView : ScreenView
    {
        public override string ScreenTypeName => "FakeAppScreen";

        // Resolved in OnAdded by shape (no hardcoded obfuscated names).
        private FieldInfo? _linesField;
        private FieldInfo? _indexField;

        protected override void OnAdded(object screen)
        {
            const BindingFlags inst =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // FakeAppScreen has exactly one array field and one int field
            // declared on the type. Match by shape so we don't rely on
            // de4dot's obfuscated-name renames staying stable.
            foreach (FieldInfo f in screen.GetType().GetFields(inst))
            {
                if (_linesField == null && f.FieldType.IsArray
                    && f.FieldType.GetElementType() is Type elem
                    && !elem.IsValueType && elem != typeof(string))
                {
                    _linesField = f;
                }
                else if (_indexField == null && f.FieldType == typeof(int))
                {
                    _indexField = f;
                }
            }

            Log($"[FakeAppScreenView] added: lines={_linesField?.Name ?? "<null>"} " +
                $"index={_indexField?.Name ?? "<null>"}");
        }

        protected override (object? key, string? label) ReadCurrentFocus()
        {
            if (ScreenInstance == null || _linesField == null || _indexField == null)
                return (null, null);
            try
            {
                int idx = (int)_indexField.GetValue(ScreenInstance)!;
                if (!(_linesField.GetValue(ScreenInstance) is Array lines)) return (null, null);
                if (idx < 0 || idx >= lines.Length) return (null, null);
                string? text = lines.GetValue(idx)?.ToString();
                if (string.IsNullOrWhiteSpace(text)) return ((object)idx, null);
                return ((object)idx, text);
            }
            catch
            {
                return (null, null);
            }
        }

        // Each advance reads the new line. This screen carries the
        // narrative content for exploration-entry thoughts (Evelyn's
        // thoughts), look-around snippets, and the locked-app text, so
        // mark it R-key replayable — unlike menu/UI focus labels.
        protected override void OnFocusChanged(object? key, string label)
        {
            Speech.Speak(label, repeatable: true);
        }
    }
}
