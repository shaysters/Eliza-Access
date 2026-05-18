using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Reads the VN scripted dialogue line currently displayed by WorldScreen
    /// and speaks new lines as they arrive. Speaks as "Speaker: text" when the
    /// speaker changes, just "text" while the same speaker continues.
    ///
    /// Source:
    /// <c>WorldScreen.#=qp1KryaWTbmfwRg3FYGc8Yi43QkgZg3K3P3sSd2qfGu8=</c>
    /// holds <c>Maybe&lt;DialogueLine&gt;</c> — the event currently being
    /// rendered. Set at WorldScreen.cs:3200, cleared at :3023/:3117/:3609/:3662.
    ///
    /// DialogueLine sub-fields (verified from decompile):
    ///   <c>#=q0opeQa51ufYxr7JOKJ$S5A==</c>  ScriptCharacter speaker
    ///   <c>#=qtyrJpA5pn9a39IUBdZVt5A==</c>  LocalizedText text wrapper
    ///   <c>#=q3jq43jt35t2Okh_SWk9dBQ==</c>  Maybe&lt;Sound&gt; voice clip
    /// LocalizedText.GetText() → string; ScriptCharacter.GetDisplayName() →
    /// string. Both methods are reflected by signature (first 0-arg public
    /// method returning string) so we don't pin the obfuscated method names.
    ///
    /// Suppression rules:
    /// - Voiced lines are skipped (the game itself plays the voice clip; TTS
    ///   would step on it).
    /// - Player-reply echo: when the player just confirmed a chat choice
    ///   <see cref="ChoiceState.LastConfirmedChoiceText"/> matches the
    ///   incoming line within ~4 s, we suppress (they just picked it).
    ///
    /// Re-entry: on WorldScreen re-focus (menu trip, save resume, session-
    /// results pop) <see cref="OnEnter"/> clears <c>_lastDialogueText</c>
    /// so whatever line is on screen — whether it's the same one the player
    /// left or a new one that appeared while another screen was on top —
    /// reads on the next poll. For a sighted player the displayed text is
    /// re-orientation enough; for a blind player it has to be spoken again.
    /// </summary>
    internal sealed class DialogueSubView : SubView
    {
        // ── Obfuscated names (stable across Eazfuscator 3.3 builds) ────
        private const string DialogueLineFieldName  = "#=qp1KryaWTbmfwRg3FYGc8Yi43QkgZg3K3P3sSd2qfGu8=";
        private const string DlSpeakerFieldName     = "#=q0opeQa51ufYxr7JOKJ$S5A==";
        private const string DlTextObjFieldName     = "#=qtyrJpA5pn9a39IUBdZVt5A==";
        private const string DlSoundFieldName       = "#=q3jq43jt35t2Okh_SWk9dBQ==";

        // ── WorldScreen-level reflection (OnInit) ──────────────────────
        private FieldInfo?  _currentDialogueLineField;
        private MethodInfo? _dlMaybeHasValue;
        private MethodInfo? _dlMaybeGetValue;

        // ── DialogueLine-level reflection (deferred to first live instance) ──
        private bool        _dialogueLineDeferredDone;
        private FieldInfo?  _dlSpeakerField;
        private FieldInfo?  _dlTextObjField;
        private MethodInfo? _dlGetTextMethod;
        private MethodInfo? _dlGetDisplayName;
        private FieldInfo?  _dlSoundField;
        private MethodInfo? _dlSoundHasValue;

        // ── Per-activation state ───────────────────────────────────────
        private string? _lastDialogueText;
        private string? _lastSpeakerName;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _currentDialogueLineField = tWS.GetField(DialogueLineFieldName, inst);
            if (_currentDialogueLineField == null)
            {
                Log("[DialogueSub] currentDialogueLineField NOT FOUND — sub disabled");
                return;
            }

            Type maybeType = _currentDialogueLineField.FieldType;
            Type? tDlArg = maybeType.IsGenericType && maybeType.GetGenericArguments().Length == 1
                ? maybeType.GetGenericArguments()[0] : null;

            foreach (MethodInfo m in maybeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType == typeof(bool))               { _dlMaybeHasValue = m; continue; }
                if (tDlArg != null && m.ReturnType == tDlArg)   { _dlMaybeGetValue = m; }
            }

            Log($"[DialogueSub] OnInit: hasValue={_dlMaybeHasValue != null} getValue={_dlMaybeGetValue != null}");
        }

        public override void OnEnter()
        {
            // Clear the de-dupe so whatever line is on screen at re-entry
            // (whether the player left it visible or a new one came up
            // while another screen was on top) reads on the next poll.
            // The visual text is the player's anchor; for a blind player
            // it has to be re-spoken to serve the same role.
            _lastDialogueText = null;
            _lastSpeakerName  = null;
        }

        public override void OnUpdate()
        {
            if (_currentDialogueLineField == null ||
                _dlMaybeHasValue == null || _dlMaybeGetValue == null)
                return;

            PollDialogueLine();
        }

        private void PollDialogueLine()
        {
            // In-VN texting overlay owns its own message readout via
            // TextingOverlaySubView (PhoneState.chatHistory path with
            // proper "rainer: …" attribution). WorldScreen.method_24
            // sets the dialogue-line field the moment a texting message
            // begins typing — without this gate we'd speak the line bare
            // (no speaker prefix, because BuildUtterance drops the
            // prefix when the speaker repeats), then a couple seconds
            // later TextingOverlay would speak the same line again with
            // the attribution. Users perceive the bare speech as
            // "Evelyn's thoughts" mixed into the convo.
            if (TextingState.IsActive) return;

            // Post-session results overlay owns the screen until the
            // player presses Continue. The engine leaves the in-session
            // dialogue line field set (and may append a closing line
            // during EndTherapy itself), so without this gate
            // DialogueSub speaks a stray Eliza line right after
            // SessionResultsSub announces "Session Results."
            if (SessionResultsState.IsActive) return;

            try
            {
                object? maybeDl = _currentDialogueLineField!.GetValue(Parent);
                if (maybeDl == null) return;

                bool hasLine = (bool)_dlMaybeHasValue!.Invoke(maybeDl, null)!;
                if (!hasLine)
                {
                    if (_lastDialogueText != null || _lastSpeakerName != null)
                    {
                        _lastDialogueText = null;
                        _lastSpeakerName  = null;
                    }
                    return;
                }

                object? lineVal = _dlMaybeGetValue!.Invoke(maybeDl, null);
                if (lineVal == null) return;

                if (!_dialogueLineDeferredDone)
                    DeferredReflectDialogueLine(lineVal);

                if (_dlTextObjField == null || _dlGetTextMethod == null) return;

                object? textObj = _dlTextObjField.GetValue(lineVal);
                if (textObj == null) return;

                string? text = _dlGetTextMethod.Invoke(textObj, null) as string;
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text == _lastDialogueText) return;

                string speakerName = ReadSpeakerName(lineVal);
                bool   voiced      = ReadVoiced(lineVal);
                string utterance   = BuildUtterance(speakerName, text!);

                if (voiced)
                {
                    _lastDialogueText = text;
                    // Stay silent (the engine plays the voice clip)
                    // but stash the utterance so R-key replay can
                    // surface the text on demand.
                    Speech.RecordSilent(utterance);
                    Log($"[DialogueSub] line rejected: voiced");
                    return;
                }

                if (IsPlayerReplyEcho(text!))
                {
                    _lastDialogueText = text;
                    ChoiceState.LastConfirmedChoiceText = null;
                    // Same idea — the player just chose this line so
                    // we don't need to echo it, but R-key should still
                    // be able to repeat what they said.
                    Speech.RecordSilent(utterance);
                    Log($"[DialogueSub] line rejected: player-reply echo");
                    return;
                }

                _lastDialogueText = text;
                if (speakerName != "?") _lastSpeakerName = speakerName;
                Speech.Speak(utterance);
                Log($"[DialogueSub] spoke: \"{Trunc(utterance, 80)}\"");
            }
            catch (Exception ex)
            {
                Log($"[DialogueSub] PollDialogueLine error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────

        private string ReadSpeakerName(object lineVal)
        {
            if (_dlSpeakerField == null || _dlGetDisplayName == null) return "?";
            try
            {
                object? sp = _dlSpeakerField.GetValue(lineVal);
                if (sp == null) return "?";
                string? n = _dlGetDisplayName.Invoke(sp, null) as string;
                return string.IsNullOrWhiteSpace(n) ? "?" : n!;
            }
            catch { return "?"; }
        }

        private bool ReadVoiced(object lineVal)
        {
            if (_dlSoundField == null || _dlSoundHasValue == null) return false;
            try
            {
                object? soundMaybe = _dlSoundField.GetValue(lineVal);
                if (soundMaybe == null) return false;
                return (bool)_dlSoundHasValue.Invoke(soundMaybe, null)!;
            }
            catch { return false; }
        }

        private string BuildUtterance(string speakerName, string text)
        {
            if (speakerName != "?" && speakerName != _lastSpeakerName)
                return speakerName + ": " + text;
            return text;
        }

        private static bool IsPlayerReplyEcho(string text)
        {
            if (ChoiceState.LastConfirmedChoiceText == null) return false;
            if (Environment.TickCount - ChoiceState.LastConfirmedChoiceTick >= 4000) return false;

            string chosen  = ChoiceState.LastConfirmedChoiceText.Trim();
            string current = text.Trim();
            if (chosen.Length == 0) return false;

            return current.Equals(chosen, StringComparison.Ordinal)
                || current.StartsWith(chosen, StringComparison.Ordinal)
                || chosen.StartsWith(current, StringComparison.Ordinal);
        }

        private void DeferredReflectDialogueLine(object lineVal)
        {
            _dialogueLineDeferredDone = true;
            try
            {
                Type lineType = lineVal.GetType();
                const BindingFlags instAll =
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                _dlSpeakerField = lineType.GetField(DlSpeakerFieldName, instAll);
                _dlTextObjField = lineType.GetField(DlTextObjFieldName, instAll);
                _dlSoundField   = lineType.GetField(DlSoundFieldName,   instAll);

                if (_dlTextObjField != null)
                {
                    Type textType = _dlTextObjField.FieldType;
                    foreach (MethodInfo m in textType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 && m.ReturnType == typeof(string))
                        { _dlGetTextMethod = m; break; }
                    }
                }

                if (_dlSpeakerField != null)
                {
                    Type charType = _dlSpeakerField.FieldType;
                    foreach (MethodInfo m in charType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 &&
                            m.ReturnType == typeof(string) &&
                            m.Name != "ToString")
                        { _dlGetDisplayName = m; break; }
                    }
                }

                if (_dlSoundField != null)
                {
                    Type soundMaybeType = _dlSoundField.FieldType;
                    foreach (MethodInfo m in soundMaybeType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool))
                        { _dlSoundHasValue = m; break; }
                    }
                }

                Log($"[DialogueSub] reflected DialogueLine={lineType.Name} " +
                    $"speaker={_dlSpeakerField != null} text={_dlTextObjField != null} " +
                    $"getText={_dlGetTextMethod != null} getName={_dlGetDisplayName != null} " +
                    $"sound={_dlSoundField != null} soundHas={_dlSoundHasValue != null}");
            }
            catch (Exception ex)
            {
                Log($"[DialogueSub] DeferredReflectDialogueLine error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
