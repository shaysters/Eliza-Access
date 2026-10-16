using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI;
using ElizaAccess.UI.Elements;

namespace ElizaAccess.Views.PhoneScreenSubs
{
    /// <summary>
    /// Announces the MailApp (inbox app) inside the phone — both the
    /// inbox list and the per-email detail view.
    ///
    /// Activation: <c>IsActive ⇔ PhoneState.activeApp</c> has value AND
    /// the active app's runtime type is <c>MailApp</c>.
    ///
    /// Two states inside the app:
    /// - <b>List</b> — <c>MailApp.selectedEmail</c> is no-value. Player
    ///   is scrolling the inbox. We announce sender + subject as the
    ///   selected index changes.
    /// - <b>Detail</b> — <c>MailApp.selectedEmail</c> has value. Player
    ///   has opened a specific email. On entry we read "From {sender}.
    ///   {subject}." then every non-empty body line. Body lines are
    ///   plain strings split on newline; emoji shortcodes like
    ///   <c>:fire:</c> (MailApp.cs:195-204 renders these as icons via a
    ///   texture dictionary) are stripped of the surrounding colons so
    ///   TTS speaks the keyword. A <see cref="LineReader"/> then lets
    ///   Up/Down browse the header and body line by line, scrolling the
    ///   email to follow (line building shared via <see cref="EmailLines"/>).
    ///
    /// Controller-only path: <c>MailApp.selectedIndex</c> is only updated
    /// by the game when VirtualInput controller mode is active
    /// (MailApp.cs:142-152). The mod uses Patches/KeyboardToController
    /// to keep that mode on, so D-pad / arrow-key navigation drives the
    /// selection naturally and TTS follows.
    ///
    /// Inbox order: the engine renders newest-first via Reverse() over
    /// <c>PhoneState.inbox()</c>; we mirror that so the index we read
    /// matches what the player sees on screen.
    ///
    /// Migrated from <c>V2PhoneObserver</c>'s Mail section.
    /// </summary>
    internal sealed class MailAppSubView : SubView
    {
        // ── Obfuscated names (stable across builds) ────────────────────
        // The phoneState_0 field exists on BOTH PhoneScreen and
        // WorldScreen at different obfuscated names. Same PhoneState
        // instance behind both — so this sub can be wired under either
        // parent depending on context. PhoneScreen path is the normal
        // phone open; WorldScreen path is the inline phone rendered
        // during Transparency Mode.
        private const string PhoneScreenPhoneStateField  = "#=qqzJofoxKCCizQVMcCRLhAA==";
        private const string WorldScreenPhoneStateField  = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string ActiveAppFieldName      = "#=qS3nVDip0NyI_pzkywVHdyQ==";
        private const string MailAppTypeName         = "MailApp";
        private const string MailSelectedEmailField  = "#=qian4VYkQdN2Gl8MtFU6iLA==";
        private const string MailSelectedIndexField  = "#=qvEcx8F3PLa8ko3qy8VwAEDEGT5guS5DJbYzpRgXIZKU=";
        // PhoneState.inbox() — note the SINGLE trailing '=' (PhoneState.cs:216).
        private const string InboxMethodName         = "#=qlT3cYi$p0e2GwVUfwM$ZpWqDBmtdx01cDnuGa3xJVG0=";
        private const string ScriptEmailTypeName     = "ScriptEmail";
        private const string EmailSenderField        = "#=qqyehqnoGNtxinWDp6xth0w==";
        private const string EmailSubjectField       = "#=qdlh3n2uNGIux6bl0e6K7uA==";
        private const string EmailBodyField          = "#=qj9q0554wTUaB3Ay8iPJ80g==";
        private const string EmailPlaceholderField   = "#=q6jB6zTsQ0W9G3bubWwlN3ph0MHOJjGshAIKC8O5xXg4==";
        private const string EmailDateField          = "#=qwg$KVuFrMweHKbfK0W5mdA==";
        // MailApp.gclass45_1 — the open email's scroller.
        private const string MailDetailScrollerField = "#=qmmCbYzQqQ67EuOxzae6jEd69lWgFfTkewugBiKzlN48=";

        // ── Reflection (OnInit) ────────────────────────────────────────
        private FieldInfo?  _detailScrollerField;
        private LineReader? _reader;
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _activeAppField;
        private MethodInfo? _maybeAppHasValue;
        private MethodInfo? _maybeAppGetValue;
        private Type?       _tMailApp;
        private FieldInfo?  _mailSelectedEmailField;
        private FieldInfo?  _mailSelectedIndexField;
        private MethodInfo? _maybeEmailHasValue;
        private MethodInfo? _maybeEmailGetValue;
        private MethodInfo? _inboxMethod;
        private FieldInfo?  _emailSenderField;
        private FieldInfo?  _emailSubjectField;
        private FieldInfo?  _emailBodyField;
        private FieldInfo?  _emailPlaceholderField;
        private FieldInfo?  _emailDateField;

        // ── Per-activation state ───────────────────────────────────────
        private int  _focusedListIdx  = -1;
        private bool _inDetailView;

        // ── Optional additional IsActive gate ──────────────────────────
        // Passed by the parent view. Lets the WorldScreen-mounted
        // instance require TransparencyState.IsActive on top of the
        // built-in "activeApp == MailApp" check, so the PhoneScreen
        // instance and the WorldScreen instance don't both fire when
        // the player opens the regular phone outside transparency.
        private readonly Func<bool>? _additionalGate;

        public MailAppSubView(Func<bool>? additionalGate = null)
        {
            _additionalGate = additionalGate;
        }

        public override void OnInit()
        {
            Type tPS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // Try PhoneScreen's name first, then WorldScreen's. Only
            // one will resolve depending on which view wired us.
            _phoneStateField = tPS.GetField(PhoneScreenPhoneStateField, inst)
                              ?? tPS.GetField(WorldScreenPhoneStateField, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                _activeAppField  = tPhoneState.GetField(ActiveAppFieldName, inst);
                if (_activeAppField != null)
                {
                    _maybeAppHasValue = ResolveMaybeMethod(_activeAppField.FieldType, typeof(bool));
                    _maybeAppGetValue = ResolveMaybeGetValue(_activeAppField.FieldType);
                }
                _inboxMethod = tPhoneState.GetMethod(InboxMethodName, inst);
            }

            _tMailApp = Nagoya.FindType(MailAppTypeName);
            if (_tMailApp != null)
            {
                _mailSelectedEmailField = _tMailApp.GetField(MailSelectedEmailField, inst);
                _mailSelectedIndexField = _tMailApp.GetField(MailSelectedIndexField, inst);
                _detailScrollerField    = _tMailApp.GetField(MailDetailScrollerField, inst);
                if (_mailSelectedEmailField != null)
                {
                    _maybeEmailHasValue = ResolveMaybeMethod(_mailSelectedEmailField.FieldType, typeof(bool));
                    _maybeEmailGetValue = ResolveMaybeGetValue(_mailSelectedEmailField.FieldType);
                }
            }

            Type? tScriptEmail = Nagoya.FindType(ScriptEmailTypeName);
            if (tScriptEmail != null)
            {
                _emailSenderField      = tScriptEmail.GetField(EmailSenderField, inst);
                _emailSubjectField     = tScriptEmail.GetField(EmailSubjectField, inst);
                _emailBodyField        = tScriptEmail.GetField(EmailBodyField, inst);
                _emailPlaceholderField = tScriptEmail.GetField(EmailPlaceholderField, inst);
                _emailDateField        = tScriptEmail.GetField(EmailDateField, inst);
            }

            _reader = new LineReader(Speech, Log, "MailAppSub");

            Log($"[MailAppSub] init: phoneState={(_phoneStateField != null)} " +
                $"activeApp={(_activeAppField != null)} " +
                $"mailApp={(_tMailApp != null)} " +
                $"inbox={(_inboxMethod != null)} " +
                $"selectedEmail={(_mailSelectedEmailField != null)} " +
                $"selectedIndex={(_mailSelectedIndexField != null)} " +
                $"detailScroller={(_detailScrollerField != null)} layout={TextLayout.IsReady} " +
                $"maybeEmail.Has={(_maybeEmailHasValue != null)} Get={(_maybeEmailGetValue != null)} " +
                $"scriptEmail={(_emailSenderField != null && _emailSubjectField != null && _emailBodyField != null)}");
        }

        public override bool IsActive
        {
            get
            {
                if (_additionalGate != null && !_additionalGate()) return false;
                object? app = TryGetActiveApp();
                return app != null && _tMailApp != null && app.GetType() == _tMailApp;
            }
        }

        public override void OnEnter()
        {
            _focusedListIdx = -1;
            _inDetailView   = false;
            Speech.Speak(Loc.Get("mail.title"));
            SpeakInboxCount();
            Log("[MailAppSub] mail app entered.");
        }

        public override void OnLeave()
        {
            _focusedListIdx = -1;
            _inDetailView   = false;
            _reader?.Close();
            Log("[MailAppSub] mail app left.");
        }

        public override void OnUpdate()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return;
            object? mailApp = TryGetActiveApp();
            if (mailApp == null) return;

            if (_mailSelectedEmailField == null || _mailSelectedIndexField == null) return;

            object? emailMaybe;
            try { emailMaybe = _mailSelectedEmailField.GetValue(mailApp); }
            catch { return; }
            if (emailMaybe == null) return;

            bool emailOpen;
            try { emailOpen = _maybeEmailHasValue != null && (bool)_maybeEmailHasValue.Invoke(emailMaybe, null)!; }
            catch { return; }

            if (!emailOpen)
            {
                // State A — inbox list
                if (_inDetailView)
                {
                    _inDetailView = false;
                    _reader?.Close();
                    Log("[MailAppSub] returned to list.");
                }
                int idx;
                try { idx = (int)_mailSelectedIndexField.GetValue(mailApp)!; }
                catch { return; }
                if (idx != _focusedListIdx)
                {
                    _focusedListIdx = idx;
                    SpeakInboxListItem(phoneState, idx);
                }
            }
            else
            {
                // State B — detail view. Open the reader once on the
                // transition (it auto-reads the email), then let it
                // drive Up/Down line browsing every poll.
                if (!_inDetailView)
                {
                    _inDetailView = true;
                    if (_maybeEmailGetValue == null) return;
                    object? email;
                    try { email = _maybeEmailGetValue.Invoke(emailMaybe, null); }
                    catch { return; }
                    if (email != null) OpenDetail(email, mailApp);
                }
                _reader?.Poll();
            }
        }

        // ── Inbox-count opening context ────────────────────────────────

        private void SpeakInboxCount()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null || _inboxMethod == null || _emailPlaceholderField == null) return;
            try
            {
                object? inboxRaw = _inboxMethod.Invoke(phoneState, null);
                if (inboxRaw == null) return;

                int count = 0;
                foreach (object? item in (IEnumerable)inboxRaw)
                {
                    if (item == null) continue;
                    object? isPlaceholder = _emailPlaceholderField.GetValue(item);
                    if (isPlaceholder is bool b && b) continue;
                    count++;
                }
                string ctx = count == 1 ? "1 email." : $"{count} emails.";
                Speech.Speak(ctx);
                Log($"[MailAppSub] inbox count: {ctx}");
            }
            catch (Exception ex)
            {
                Log($"[MailAppSub] SpeakInboxCount error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── List view ──────────────────────────────────────────────────

        private void SpeakInboxListItem(object phoneState, int idx)
        {
            if (_inboxMethod == null || _emailSenderField == null || _emailSubjectField == null) return;
            try
            {
                object? inboxRaw = _inboxMethod.Invoke(phoneState, null);
                if (inboxRaw == null) return;

                // Newest-first to match what MailApp renders (the engine
                // calls Reverse() before drawing).
                var emails = ((IEnumerable)inboxRaw).Cast<object>().Reverse().ToList();
                if (idx < 0 || idx >= emails.Count) return;

                object email = emails[idx];

                if (_emailPlaceholderField != null)
                {
                    object? isPlaceholder = _emailPlaceholderField.GetValue(email);
                    if (isPlaceholder is bool b && b) return;
                }

                string sender  = _emailSenderField.GetValue(email)?.ToString() ?? "";
                string subject = _emailSubjectField.GetValue(email)?.ToString() ?? "";
                string date    = ReadEmailDate(email);

                // Build the email-row label as comma-joined parts
                // (sender, subject, date) then wrap in ButtonElement
                // so the type marker appends per the framework
                // convention — "rainer, Re: your last session, Jan
                // 8, button".
                var labelParts = new List<string>(3);
                if (!string.IsNullOrEmpty(sender))  labelParts.Add(sender);
                if (!string.IsNullOrEmpty(subject)) labelParts.Add(subject);
                if (!string.IsNullOrEmpty(date))    labelParts.Add(date);
                string label = AnnouncementComposer.JoinSpeechParts(labelParts);
                Speech.Speak(new ButtonElement(label).ResolveFocusText());
                Log($"[MailAppSub] list focus: [{idx}] {sender} / {subject} / {date}");
            }
            catch (Exception ex)
            {
                Log($"[MailAppSub] SpeakInboxListItem error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Read ScriptEmail.dateTime_0 and format as the inbox UI shows
        // it ("Jan 8" / "Feb 14" / etc.). The visible column in the
        // game caps at "MMM d" with no year — older mail just keeps
        // climbing the month, so matching that format keeps spoken
        // context consistent with what a sighted player would see.
        // InvariantCulture pins the format regardless of the player's
        // OS locale.
        private string ReadEmailDate(object email)
        {
            if (_emailDateField == null) return "";
            try
            {
                object? raw = _emailDateField.GetValue(email);
                if (raw is DateTime dt)
                    return dt.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        // ── Detail view ────────────────────────────────────────────────

        // Read the whole email on open (header, then every body line)
        // with the line cursor on the header; Up/Down then browse line
        // by line and scroll the view to follow. Line building (image
        // descriptions, emoji shortcodes, layout offsets) is shared
        // with the Eliza app's Skandha inbox — see EmailLines.
        private void OpenDetail(object email, object mailApp)
        {
            if (_emailSenderField == null || _emailSubjectField == null || _emailBodyField == null) return;
            try
            {
                string sender  = _emailSenderField.GetValue(email)?.ToString() ?? "";
                string subject = _emailSubjectField.GetValue(email)?.ToString() ?? "";
                string body    = _emailBodyField.GetValue(email)?.ToString() ?? "";
                string date    = ReadEmailDate(email);

                List<LineReader.Line> lines = EmailLines.Build(sender, date, subject, body);
                EmailLines.SpeakAll(Speech, lines);
                _reader?.Open(lines, 0, () => ReadField(_detailScrollerField, mailApp));

                Log($"[MailAppSub] detail: {sender} / {subject} / {date} ({lines.Count} lines)");
            }
            catch (Exception ex)
            {
                Log($"[MailAppSub] OpenDetail error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static object? ReadField(FieldInfo? field, object target)
        {
            try { return field?.GetValue(target); }
            catch { return null; }
        }

        // ── PhoneState navigation ──────────────────────────────────────

        private object? TryGetPhoneState()
        {
            if (_phoneStateField == null) return null;
            try { return _phoneStateField.GetValue(Parent); }
            catch { return null; }
        }

        private object? TryGetActiveApp()
        {
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return null;
            if (_activeAppField == null || _maybeAppHasValue == null || _maybeAppGetValue == null) return null;

            try
            {
                object? maybe = _activeAppField.GetValue(phoneState);
                if (maybe == null) return null;
                if (!(bool)_maybeAppHasValue.Invoke(maybe, null)!) return null;
                return _maybeAppGetValue.Invoke(maybe, null);
            }
            catch { return null; }
        }

        // ── Maybe<T> method resolution ─────────────────────────────────

        private static MethodInfo? ResolveMaybeMethod(Type maybeType, Type returnType)
        {
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == returnType) return m;
            }
            return null;
        }

        private static MethodInfo? ResolveMaybeGetValue(Type maybeType)
        {
            Type tArg = maybeType.IsGenericType ? maybeType.GetGenericArguments()[0] : typeof(object);
            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length == 0 && m.ReturnType == tArg) return m;
            }
            return null;
        }
    }
}
