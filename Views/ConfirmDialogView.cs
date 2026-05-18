using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the game's universal confirmation dialog
    /// (<c>GClass169</c> at runtime, de4dot-named for its signature).
    ///
    /// One screen type covers every caller across the game:
    /// - Title: "Starting a new game will reset your progress…" + Continue/Cancel
    /// - Title: "This game saves automatically…" + Okay (message-only)
    /// - Chapter Select: "Returning to checkpoint will overwrite…" + Continue/Cancel
    /// - Settings: display-quality apply prompt
    /// - any future caller — they all push the same screen type.
    ///
    /// Structure (from decompiled <c>GClass169.cs</c>):
    /// <code>
    /// private readonly string string_0;   // question text (already-resolved localized string)
    /// private readonly string string_1;   // confirm-button label
    /// private readonly bool   bool_0;     // auto-dismiss on confirm
    /// private readonly bool   bool_1;     // show Cancel button
    /// private readonly Maybe&lt;Action&gt; maybe_0;  // confirm callback
    /// </code>
    /// We need only <c>string_0</c> for the entry announcement; the
    /// button labels ("Continue", "Cancel", "Okay") are captured by
    /// <see cref="Patches.HotspotLabelCapture"/> from their text-draws,
    /// so <see cref="ScreenView.ReadFocusFromCursor"/> handles the rest
    /// without any further reflection.
    ///
    /// Behaviour matches the EscapeMenuView pattern:
    /// - <c>OnFocus</c> speaks the question with interrupt, sets a flag
    ///   so the first focus-change announcement queues after instead of
    ///   cutting it off.
    /// - <c>OnFocusChanged</c> speaks the focused button's label,
    ///   respecting the queue-after-question flag.
    /// </summary>
    internal sealed class ConfirmDialogView : ScreenView
    {
        // At runtime the type still carries its obfuscated Eazfuscator name —
        // de4dot only renames during decompile, the loaded DLL has the
        // original. Confirmed via V2TitleMenuObserver.FindConfirmDialogFields
        // which resolves the same hash.
        public const string DialogTypeName = "#=qhDLHR1qQRJ30X6EUdNn6j5l46hBcbdoyLdI4gLHOajM=";
        public override string ScreenTypeName => DialogTypeName;

        private string? _questionText;

        protected override void OnAdded(object screen)
        {
            // The dialog's question is the first declared string instance
            // field on the type (string_0 in source order). C# preserves
            // declaration order in reflection metadata, so the first
            // string field returned is reliably the question — no need to
            // hardcode the obfuscated field name. (The other string field
            // is string_1, the confirm button label, which we don't need
            // because HotspotLabelCapture already labels its hotspot.)
            const BindingFlags inst =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (FieldInfo f in screen.GetType().GetFields(inst))
            {
                if (f.FieldType != typeof(string)) continue;
                try { _questionText = f.GetValue(screen) as string; }
                catch { _questionText = null; }
                break;
            }

            Log($"[ConfirmDialogView] added: question=\"{Trunc(_questionText, 80)}\"");
        }

        protected override void OnFocus()
        {
            if (!string.IsNullOrEmpty(_questionText))
                Speech.Speak(_questionText!);
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();

        private static string Trunc(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "<null>";
            return s!.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
