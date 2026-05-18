using System;
using System.Reflection;
using ElizaAccess.Common;

namespace ElizaAccess.Views
{
    /// <summary>
    /// Screen-reader view for the in-game Settings screen. Replaces the
    /// ~1800-line V2SettingsObserver with the standard cursor-on-hotspot
    /// pattern plus a small OnUpdate state machine for the three sliders
    /// and the language selection.
    ///
    /// Most of the old observer's bulk was reflection setup that the
    /// shared infrastructure now handles:
    /// - Hotspot labels (Fullscreen / Windowed / High (4K) / Low (2K) /
    ///   Off / Normal / Slower / English / 中文 / etc., plus the slider
    ///   row labels and the "Display Mode:" / "Display Quality:" /
    ///   "Auto-Advance:" / "Language:" section prefixes) come from
    ///   <see cref="Patches.HotspotLabelCapture"/>'s text-draw pairing.
    /// - Keyboard navigation is the Harmony emulation path.
    /// - Hardcoded count-17 / count-20 fallback tables are no longer
    ///   needed — capture works for any layout.
    ///
    /// What this view still observes via reflection (not coverable by
    /// HotspotLabelCapture, because the values aren't labels):
    /// - Three Container&lt;float&gt; slider values (Effect / Voice / Music
    ///   volumes). Announced as "Effect Volume: 95 percent" when the
    ///   player adjusts them.
    /// - Container&lt;Language&gt; current language. Announced when the
    ///   selection changes (e.g., clicking 中文 from English).
    ///
    /// What's deliberately NOT migrated:
    /// - "Display mode" announcements off OUzr4.../9CnWdnB4... — those
    ///   fields were misidentified (they track Auto-Advance, not Display
    ///   Mode), so the legacy observer's announcement was always wrong.
    ///   We dropped the speech in the previous Settings fix commit;
    ///   nothing to migrate.
    /// - "Fullscreen On/Off" off p95Mai6 — same story, tracks Display
    ///   Quality not fullscreen, speech disabled.
    /// </summary>
    internal sealed class SettingsView : ScreenView
    {
        public override string ScreenTypeName => "SettingsScreen";

        // ── Reflection cache (resolved in OnAdded) ─────────────────────
        private FieldInfo?  _gameLogicSingleton;
        private FieldInfo?  _settingsField;
        private FieldInfo?  _slider1Field;
        private FieldInfo?  _slider2Field;
        private FieldInfo?  _slider3Field;
        private MethodInfo? _floatContainerGet;
        private FieldInfo?  _languageField;
        private MethodInfo? _langContainerGet;
        private Type?       _languageEnumType;

        // Slider labels resolved via Localizer at OnAdded; fall back to
        // English defaults if localization is unavailable for any reason.
        // Int keys are stable across game versions, source-confirmed.
        private const int SliderKeyEffect = 1497425061;
        private const int SliderKeyVoice  = 1497425033;
        private const int SliderKeyMusic  = 1497425052;
        private string _slider1Label = "Effect Volume";
        private string _slider2Label = "Voice Volume";
        private string _slider3Label = "Music Volume";

        // ── Per-activation state ───────────────────────────────────────
        // Slider value baselines. -1f sentinel means "haven't read yet";
        // first successful read primes silently so we don't announce a
        // fake "0% → real value" change on entry.
        private float _lastSlider1 = -1f;
        private float _lastSlider2 = -1f;
        private float _lastSlider3 = -1f;
        // Language baseline. -1 sentinel same purpose.
        private int   _lastLanguage = -1;

        // ── Obfuscated names (stable across builds, source-confirmed) ──
        private const string GameLogicSettingsFieldName = "#=qcfPtknXZ5IEbjuOI2Z5gkg==";
        private const string Slider1FieldName           = "#=q4xHdzlejwhe8f$eszO6QHA==";
        private const string Slider2FieldName           = "#=qg$Z5mft8MMGgLwSLackT7g==";
        private const string Slider3FieldName           = "#=qqbawg6zSsxldbaEW4G21kA==";
        private const string LanguageFieldName          = "#=qdiitE$xHgYx$UAbxCq7WdA==";
        private const string ContainerGetValueName      = "#=qUrHgFy5gmcMr7GPeyD2HtQ==";

        protected override void OnAdded(object screen)
        {
            ResolveReflection();
            ResolveSliderLabels();
        }

        protected override void OnFocus()
        {
            Speech.Speak("Settings");

            // Prime the slider/language baselines so the first OnUpdate
            // after entry doesn't announce stale values as "changes". -1
            // sentinel for unread fields keeps AnnounceIfChanged silent
            // until we have a successful read to compare against.
            _lastSlider1  = TryReadFloatContainer(_slider1Field);
            _lastSlider2  = TryReadFloatContainer(_slider2Field);
            _lastSlider3  = TryReadFloatContainer(_slider3Field);
            _lastLanguage = TryReadLanguage();
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();

        protected override void OnUpdate()
        {
            AnnounceSliderIfChanged(_slider1Label, ref _lastSlider1, TryReadFloatContainer(_slider1Field));
            AnnounceSliderIfChanged(_slider2Label, ref _lastSlider2, TryReadFloatContainer(_slider2Field));
            AnnounceSliderIfChanged(_slider3Label, ref _lastSlider3, TryReadFloatContainer(_slider3Field));

            int lang = TryReadLanguage();
            if (lang >= 0 && _lastLanguage >= 0 && lang != _lastLanguage)
            {
                string name = LanguageValueToName(lang);
                Speech.Speak($"{name}, selected");
            }
            if (lang >= 0) _lastLanguage = lang;
        }

        // ── Reflection resolution ──────────────────────────────────────

        private void ResolveReflection()
        {
            Type? tGL = Nagoya.FindType("GameLogic");
            if (tGL == null) { Log("[SettingsView] GameLogic type not found"); return; }

            // Singleton: a public static field of GameLogic type on GameLogic.
            foreach (FieldInfo sf in tGL.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (sf.FieldType == tGL) { _gameLogicSingleton = sf; break; }
            }

            _settingsField = tGL.GetField(GameLogicSettingsFieldName,
                BindingFlags.Public | BindingFlags.Instance);

            if (_settingsField == null)
            {
                Log("[SettingsView] settings field on GameLogic not found");
                return;
            }

            Type tSettings = _settingsField.FieldType;
            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;

            _slider1Field = tSettings.GetField(Slider1FieldName, pubInst);
            _slider2Field = tSettings.GetField(Slider2FieldName, pubInst);
            _slider3Field = tSettings.GetField(Slider3FieldName, pubInst);

            if (_slider1Field != null)
            {
                _floatContainerGet = _slider1Field.FieldType.GetMethod(
                    ContainerGetValueName, BindingFlags.Public | BindingFlags.Instance);
            }

            _languageField = tSettings.GetField(LanguageFieldName, pubInst);
            if (_languageField != null)
            {
                _langContainerGet = _languageField.FieldType.GetMethod(
                    ContainerGetValueName, BindingFlags.Public | BindingFlags.Instance);
                if (_languageField.FieldType.IsGenericType)
                {
                    foreach (Type ga in _languageField.FieldType.GetGenericArguments())
                    {
                        if (ga.IsEnum) { _languageEnumType = ga; break; }
                    }
                }
            }

            Log($"[SettingsView] resolved: singleton={_gameLogicSingleton != null} " +
                $"settings={_settingsField != null} " +
                $"sliders={_slider1Field != null}/{_slider2Field != null}/{_slider3Field != null} " +
                $"floatGet={_floatContainerGet != null} " +
                $"lang={_languageField != null} langGet={_langContainerGet != null} " +
                $"langEnum={_languageEnumType?.Name ?? "<null>"}");
        }

        private void ResolveSliderLabels()
        {
            Localizer.TryInit();
            string? l1 = Localizer.TryLocalize(SliderKeyEffect);
            string? l2 = Localizer.TryLocalize(SliderKeyVoice);
            string? l3 = Localizer.TryLocalize(SliderKeyMusic);
            if (!string.IsNullOrEmpty(l1)) _slider1Label = l1!;
            if (!string.IsNullOrEmpty(l2)) _slider2Label = l2!;
            if (!string.IsNullOrEmpty(l3)) _slider3Label = l3!;
            Log($"[SettingsView] slider labels: \"{_slider1Label}\" \"{_slider2Label}\" \"{_slider3Label}\"");
        }

        // ── Per-poll reads ─────────────────────────────────────────────

        private float TryReadFloatContainer(FieldInfo? field)
        {
            if (field == null || _floatContainerGet == null
                || _gameLogicSingleton == null || _settingsField == null)
                return -1f;
            try
            {
                object? gl = _gameLogicSingleton.GetValue(null);
                if (gl == null) return -1f;
                object? settings = _settingsField.GetValue(gl);
                if (settings == null) return -1f;
                object? container = field.GetValue(settings);
                if (container == null) return -1f;
                return (float)_floatContainerGet.Invoke(container, null)!;
            }
            catch { return -1f; }
        }

        private int TryReadLanguage()
        {
            if (_languageField == null || _langContainerGet == null
                || _gameLogicSingleton == null || _settingsField == null)
                return -1;
            try
            {
                object? gl = _gameLogicSingleton.GetValue(null);
                if (gl == null) return -1;
                object? settings = _settingsField.GetValue(gl);
                if (settings == null) return -1;
                object? container = _languageField.GetValue(settings);
                if (container == null) return -1;
                object? val = _langContainerGet.Invoke(container, null);
                if (val == null) return -1;
                return (int)val;
            }
            catch { return -1; }
        }

        private string LanguageValueToName(int val)
        {
            if (_languageEnumType != null)
            {
                try
                {
                    string? n = Enum.GetName(_languageEnumType, val);
                    if (!string.IsNullOrEmpty(n)) return n!;
                }
                catch { }
            }
            return $"Language{val}";
        }

        // ── Announcement helpers ───────────────────────────────────────

        private void AnnounceSliderIfChanged(string label, ref float last, float current)
        {
            if (current < 0f) return;          // read failed
            if (last < 0f) { last = current; return; }   // baseline prime
            int lastPct    = (int)Math.Round(last * 100f);
            int currentPct = (int)Math.Round(current * 100f);
            if (lastPct == currentPct) return;
            last = current;
            Speech.Speak($"{label}: {currentPct} percent");
        }
    }
}
