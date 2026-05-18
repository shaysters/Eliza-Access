using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views.PhoneScreenSubs
{
    /// <summary>
    /// Announces the FortuneApp (cookie-fortune teller) inside the phone.
    ///
    /// Activation: <c>IsActive ⇔ PhoneState.activeApp</c> has value AND
    /// the active app's runtime type is <c>FortuneApp</c>.
    ///
    /// Behaviour: speak "Fortune. Number {N+1} of 4." once on entry.
    /// FortuneApp renders one of four pre-baked image textures per
    /// language (FortuneApp.cs:89-155) — there's no readable text body
    /// to surface, so the announcement is purely "which fortune you
    /// got" (1-4). The number comes from
    /// <c>PhoneState.extrasUnlocked.GetValue()</c>, a
    /// <c>Maybe&lt;int&gt;</c> whose presence also gates the fortune-
    /// teller tile on the home grid (FortuneApp.cs:43-57). The texture
    /// dictionary is language-keyed but the integer is universal.
    ///
    /// Migrated from <c>V2PhoneObserver</c>'s Fortune section.
    /// </summary>
    internal sealed class FortuneAppSubView : SubView
    {
        // ── Obfuscated names ───────────────────────────────────────────
        private const string PhoneStateFieldName    = "#=qqzJofoxKCCizQVMcCRLhAA==";
        private const string ActiveAppFieldName     = "#=qS3nVDip0NyI_pzkywVHdyQ==";
        private const string FortuneAppTypeName     = "FortuneApp";
        // PhoneState.extrasUnlocked — Maybe<int>; the int is the
        // fortune number (0-3).
        private const string ExtrasUnlockedField    = "#=q9$zL1VcseCl_TCWvb5A1MJEamMGM6lrO_HZIzAOsDj0=";

        // ── Reflection (OnInit) ────────────────────────────────────────
        private FieldInfo?  _phoneStateField;
        private FieldInfo?  _activeAppField;
        private MethodInfo? _maybeAppHasValue;
        private MethodInfo? _maybeAppGetValue;
        private Type?       _tFortuneApp;
        private FieldInfo?  _extrasUnlockedField;
        private MethodInfo? _extrasGetValue;

        public override void OnInit()
        {
            Type tPS = Parent.GetType();
            const BindingFlags inst =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            _phoneStateField = tPS.GetField(PhoneStateFieldName, inst);
            if (_phoneStateField != null)
            {
                Type tPhoneState = _phoneStateField.FieldType;
                _activeAppField  = tPhoneState.GetField(ActiveAppFieldName, inst);
                if (_activeAppField != null)
                {
                    _maybeAppHasValue = ResolveMaybeMethod(_activeAppField.FieldType, typeof(bool));
                    _maybeAppGetValue = ResolveMaybeGetValue(_activeAppField.FieldType);
                }
                _extrasUnlockedField = tPhoneState.GetField(ExtrasUnlockedField, inst);
                if (_extrasUnlockedField != null)
                    _extrasGetValue = ResolveMaybeGetValue(_extrasUnlockedField.FieldType);
            }

            _tFortuneApp = Nagoya.FindType(FortuneAppTypeName);

            Log($"[FortuneAppSub] init: phoneState={(_phoneStateField != null)} " +
                $"activeApp={(_activeAppField != null)} " +
                $"fortuneApp={(_tFortuneApp != null)} " +
                $"extras={(_extrasUnlockedField != null)} " +
                $"extrasGet={(_extrasGetValue != null)}");
        }

        public override bool IsActive
        {
            get
            {
                object? app = TryGetActiveApp();
                return app != null && _tFortuneApp != null && app.GetType() == _tFortuneApp;
            }
        }

        public override void OnEnter()
        {
            int fortuneNumber = ReadFortuneNumber();
            if (fortuneNumber < 0)
            {
                Speech.Speak("Fortune.");
                Log("[FortuneAppSub] entered. (number unavailable)");
                return;
            }

            int oneBased = fortuneNumber + 1;
            string announcement = $"Fortune. Number {oneBased} of 4.";
            string logSuffix = "";

            if (FortuneDescriptions.TryGet(oneBased, out var entry))
            {
                if (entry.Theme.Length > 0)
                {
                    announcement += $" {entry.Theme}.";
                    logSuffix    += $" theme={entry.Theme}";
                }
                if (entry.Description.Length > 0)
                {
                    announcement += $" {entry.Description}";
                    logSuffix    += " described";
                }
            }

            Speech.Speak(announcement);
            Log($"[FortuneAppSub] entered. Number {oneBased}.{logSuffix}");
        }

        public override void OnLeave()
        {
            Log("[FortuneAppSub] left.");
        }

        public override void OnUpdate()
        {
            // Nothing to poll — FortuneApp has no internal state the
            // player can navigate through. Image-only content; the
            // OnEnter announcement is the whole readout.
        }

        private int ReadFortuneNumber()
        {
            if (_extrasUnlockedField == null || _extrasGetValue == null) return -1;
            object? phoneState = TryGetPhoneState();
            if (phoneState == null) return -1;
            try
            {
                object? extras = _extrasUnlockedField.GetValue(phoneState);
                if (extras == null) return -1;
                return (int)_extrasGetValue.Invoke(extras, null)!;
            }
            catch (Exception ex)
            {
                Log($"[FortuneAppSub] ReadFortuneNumber error: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
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
