using System;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

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
        private string? _confirmLabel;

        protected override void OnAdded(object screen)
        {
            // GClass169 has two declared string fields in source
            // order: string_0 (the question) and string_1 (the
            // confirm-button label — varies per caller: "Continue",
            // "Okay", etc.). Reflection metadata preserves
            // declaration order so the first string is the question
            // and the second is the confirm label, no obfuscated
            // name lookup needed.
            const BindingFlags inst =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;

            int stringFieldsSeen = 0;
            foreach (FieldInfo f in screen.GetType().GetFields(inst))
            {
                if (f.FieldType != typeof(string)) continue;
                string? value;
                try { value = f.GetValue(screen) as string; }
                catch { value = null; }
                if (stringFieldsSeen == 0)      _questionText = value;
                else if (stringFieldsSeen == 1) _confirmLabel = value;
                stringFieldsSeen++;
                if (stringFieldsSeen >= 2) break;
            }

            // Register the dialog's buttons. The caller-specified
            // confirm label is whatever string_1 holds. "Cancel"
            // (shown when bool_1 is true) and "Okay" (the message-
            // only variant when bool_1 is false) are the two standard
            // labels the dialog draws itself (GClass169.cs:59,76).
            // Hidden buttons stay dormant — their labels never get
            // paired by HotspotLabelCapture, so the registry entries
            // just sit there.
            if (!string.IsNullOrEmpty(_confirmLabel))
                Elements.RegisterByKey(_confirmLabel!, new ButtonElement(_confirmLabel!));
            Elements.RegisterByKey("Cancel", new ButtonElement("Cancel"));
            Elements.RegisterByKey("Okay",   new ButtonElement("Okay"));

            Log($"[ConfirmDialogView] added: question=\"{Trunc(_questionText, 80)}\" confirm=\"{_confirmLabel}\"");
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
