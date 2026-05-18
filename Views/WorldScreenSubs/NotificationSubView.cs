using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.WorldScreenSubs
{
    /// <summary>
    /// Announces in-game phone notifications (incoming mail, therapy-session
    /// updates) anchored to the buzz moment, not the timer-start.
    ///
    /// Source:
    /// - <c>WorldScreen.#=qzY9KTcekY7cB9h8RuKFjKw==</c> → PhoneState
    /// - <c>PhoneState.#=qeOiz_yr1fidK5lpLDxNCVQnNOarDeTGq78GN1GUIgs4=</c>
    ///   → <c>Maybe&lt;float&gt;</c> countdown timer. From PhoneState.cs:190-203:
    ///   timer is set to a random small value, counts down to zero, then
    ///   <c>newEmails.Clear()</c> + buzz fire together. We anchor speech to
    ///   the HasValue → Empty transition (the buzz moment).
    ///
    /// Label resolution priority (verified from save-dialog phase 2):
    ///   1. <c>PhoneState.#=qbGcuhhAwDd_FceaChl9DHg==</c> HashSet&lt;ScriptEmail&gt;
    ///      non-empty → "New mail."
    ///   2. <c>PhoneState.#=q_jj7ZK0kGqgOw$34jWXLPA==</c>
    ///      Maybe&lt;ScriptTransparency&gt; HasValue → "Message from Eliza."
    ///      (therapy session active context; followed by "Update received.")
    ///   3. Fallback → "Notification."
    /// Label is resolved at timer-START (rising edge) so the state reflects
    /// the cause; speech fires at timer-CLEAR (falling edge) so the screen-
    /// reader audio lands on the same beat as the in-game buzz.
    ///
    /// First-notification Tab/Y hint: the first actionable notification in a
    /// given WorldScreen session also speaks "Press Tab or Y button to open
    /// phone." (verified at WorldScreen.cs:2708 Key.TAB + VirtualInput.cs:148
    /// action 6 → button 3 = SDL_CONTROLLER_BUTTON_Y).
    /// </summary>
    internal sealed class NotificationSubView : SubView
    {
        // ── Obfuscated names (stable across builds) ────────────────────
        private const string PhoneStateFieldName     = "#=qzY9KTcekY7cB9h8RuKFjKw==";
        private const string NotifTimerFieldName     = "#=qeOiz_yr1fidK5lpLDxNCVQnNOarDeTGq78GN1GUIgs4=";
        private const string NewEmailsFieldName      = "#=qbGcuhhAwDd_FceaChl9DHg==";
        private const string TherapySessionFieldName = "#=q_jj7ZK0kGqgOw$34jWXLPA==";

        // ── WorldScreen-level reflection (OnInit) ──────────────────────
        private FieldInfo? _phoneStateField;

        // ── PhoneState-level reflection (deferred to first live instance) ──
        private bool        _phoneStateReflectedDone;
        private FieldInfo?  _notifTimerField;
        private MethodInfo? _maybeFloatHasValue;
        private FieldInfo?  _newEmailsField;
        private FieldInfo?  _therapySessionField;
        private MethodInfo? _maybeSessionHasValue;

        // ── Per-activation state ───────────────────────────────────────
        private bool    _timerWasActive;
        private int     _cooldownPolls;
        private string? _pendingLabel;
        private bool    _timingLogged;
        private bool    _phoneHintGiven;

        public override void OnInit()
        {
            Type tWS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _phoneStateField = tWS.GetField(PhoneStateFieldName, inst);
            if (_phoneStateField == null)
                Log("[NotifSub] phoneStateField NOT FOUND — sub disabled");
        }

        public override void OnLeave()
        {
            // Reset edge-tracker + first-notification hint on WorldScreen
            // unfocus / removal. Matches V2AutoObserver behavior pre-migration.
            _timerWasActive  = false;
            _cooldownPolls   = 0;
            _pendingLabel    = null;
            _timingLogged    = false;
            _phoneHintGiven  = false;
        }

        public override void OnUpdate()
        {
            if (_phoneStateField == null) return;

            try
            {
                object? phoneState = _phoneStateField.GetValue(Parent);
                if (phoneState == null) return;

                if (!_phoneStateReflectedDone)
                    DeferredReflectPhoneState(phoneState);

                if (_notifTimerField == null || _maybeFloatHasValue == null) return;

                object? timerMaybe = _notifTimerField.GetValue(phoneState);
                if (timerMaybe == null) return;

                bool timerActive = (bool)_maybeFloatHasValue.Invoke(timerMaybe, null)!;

                if (_cooldownPolls > 0) _cooldownPolls--;

                if (timerActive && !_timerWasActive)
                {
                    _timerWasActive = true;

                    if (!_timingLogged)
                    {
                        _timingLogged = true;
                        Log("[NotifSub] timing source: PhoneState countdown timer " +
                            "(PhoneState.cs:190-203). Speech anchored to HasValue→Empty (buzz moment).");
                    }

                    if (_cooldownPolls > 0)
                    {
                        Log("[NotifSub] suppressed: cooldown active");
                    }
                    else
                    {
                        _pendingLabel = ResolveNotificationLabel(phoneState);
                        Log($"[NotifSub] label resolved at timer-start: \"{_pendingLabel}\"");
                    }
                }
                else if (!timerActive && _timerWasActive)
                {
                    _timerWasActive = false;

                    if (_pendingLabel != null)
                    {
                        _cooldownPolls = 10;
                        string label = _pendingLabel;
                        _pendingLabel = null;
                        Speech.Speak(label);
                        Log($"[NotifSub] spoken: \"{label}\" (aligned to buzz moment)");

                        if (!_phoneHintGiven)
                        {
                            _phoneHintGiven = true;
                            Speech.Speak("Press Tab or Y button to open phone.");
                            Log("[NotifSub] phone hint spoken: first actionable notification");
                        }

                        if (label == "Message from Eliza.")
                        {
                            Speech.Speak("Update received.");
                            Log("[NotifSub] fallback context spoken: \"Update received.\"");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[NotifSub] OnUpdate error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── PhoneState reflection (deferred to first live instance) ────

        private void DeferredReflectPhoneState(object phoneState)
        {
            _phoneStateReflectedDone = true;
            try
            {
                Type tPS = phoneState.GetType();
                const BindingFlags instAll =
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                _notifTimerField     = tPS.GetField(NotifTimerFieldName,     instAll);
                _newEmailsField      = tPS.GetField(NewEmailsFieldName,      instAll);
                _therapySessionField = tPS.GetField(TherapySessionFieldName, instAll);

                if (_notifTimerField != null)
                {
                    foreach (MethodInfo m in _notifTimerField.FieldType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool))
                        { _maybeFloatHasValue = m; break; }
                    }
                }

                if (_therapySessionField != null)
                {
                    foreach (MethodInfo m in _therapySessionField.FieldType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length == 0 && m.ReturnType == typeof(bool))
                        { _maybeSessionHasValue = m; break; }
                    }
                }

                Log($"[NotifSub] reflected PhoneState={tPS.Name} " +
                    $"timer={_notifTimerField != null} hasFloat={_maybeFloatHasValue != null} " +
                    $"emails={_newEmailsField != null} session={_therapySessionField != null} " +
                    $"hasSession={_maybeSessionHasValue != null}");
            }
            catch (Exception ex)
            {
                Log($"[NotifSub] DeferredReflectPhoneState error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Label resolution ───────────────────────────────────────────

        private string ResolveNotificationLabel(object phoneState)
        {
            try
            {
                if (_newEmailsField != null)
                {
                    object? hashSet = _newEmailsField.GetValue(phoneState);
                    if (hashSet != null)
                    {
                        PropertyInfo? countProp = hashSet.GetType().GetProperty("Count");
                        if (countProp != null && (int)countProp.GetValue(hashSet)! > 0)
                            return "New mail.";
                    }
                }
            }
            catch { }

            try
            {
                if (_therapySessionField != null && _maybeSessionHasValue != null)
                {
                    object? maybe = _therapySessionField.GetValue(phoneState);
                    if (maybe != null && (bool)_maybeSessionHasValue.Invoke(maybe, null)!)
                    {
                        Log("[NotifSub] therapy session active at notification");
                        return "Message from Eliza.";
                    }
                }
            }
            catch { }

            Log("[NotifSub] meaning unresolved — generic fallback used");
            return "Notification.";
        }
    }
}
