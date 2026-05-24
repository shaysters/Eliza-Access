using System;
using System.Reflection;
using ElizaAccess.Common;
using ElizaAccess.UI.Elements;

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
        // Display Mode + Auto-Advance state, identified from a live
        // DumpContainerFields run (logged 2026-05-19). Display Quality
        // remains unidentified — needs a High↔Low toggle to pin which
        // Container<bool> it backs.
        private FieldInfo?  _displayModeField;        // Container<bool>: true = Fullscreen
        private FieldInfo?  _displayQualityLowField;  // Container<bool>: true = Low (2K), false = High (4K)
        private FieldInfo?  _autoAdvanceEnabledField; // Container<bool>: true = Normal/Slower, false = Off
        private FieldInfo?  _autoAdvanceModeField;    // Container<int>:  0 = Normal, 1 = Slower
        // Window Size: current selection lives in two Container<int>
        // on Settings (width × height), available options live in a
        // static Index2[] on GameLogic. Index2 has width as its first
        // int field and height as the second (clean decompile:
        // Index2.int_0 / int_1).
        private FieldInfo?  _windowWidthField;
        private FieldInfo?  _windowHeightField;
        private FieldInfo?  _resolutionsArrayField;   // static Index2[] on GameLogic
        private FieldInfo?  _index2WidthField;
        private FieldInfo?  _index2HeightField;
        private MethodInfo? _boolContainerGet;
        private MethodInfo? _intContainerGet;

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
        // Sliders and language radios now live in the Elements
        // registry and own their own baselines via
        // PollReactiveChange — there's nothing to track here.

        // ── Obfuscated names (stable across builds, source-confirmed) ──
        private const string GameLogicSettingsFieldName = "#=qcfPtknXZ5IEbjuOI2Z5gkg==";
        private const string Slider1FieldName           = "#=q4xHdzlejwhe8f$eszO6QHA==";
        private const string Slider2FieldName           = "#=qg$Z5mft8MMGgLwSLackT7g==";
        private const string Slider3FieldName           = "#=qqbawg6zSsxldbaEW4G21kA==";
        private const string LanguageFieldName          = "#=qdiitE$xHgYx$UAbxCq7WdA==";
        private const string ContainerGetValueName      = "#=qUrHgFy5gmcMr7GPeyD2HtQ==";
        // Identified from DumpContainerFields output 2026-05-19 —
        // values flipped in lockstep with the corresponding UI
        // toggles. DisplayQualityLow is the "Low (2K)" boolean
        // (true ⇔ Low selected) — the V2 observer mis-labeled this
        // as "Fullscreen On/Off" which is why the legacy speech for
        // it was always wrong.
        private const string DisplayModeFieldName        = "#=qX860a9WMvosyts7FKM7SzcFPsAo9cV5NZhgZjmiRh4Q=";
        private const string DisplayQualityLowFieldName  = "#=qp95Mai6PxZvKLgcbhs1732U$dX8fBNO$qnbvs79Xqr8=";
        private const string AutoAdvanceEnabledFieldName = "#=qOUzr4VuNW2sB6dR0$g7BaB433U4BkIRFN6YsLVRguj0=";
        private const string AutoAdvanceModeFieldName    = "#=q9CnWdnB4Rqi5fZcdfyKAXnTD6Gxb7OqI203gJNqBf_w=";
        // Window Size width/height (Container<int>) — confirmed from
        // DumpContainerFields output: 3840/2160 in fullscreen,
        // 3200/1800 in windowed.
        private const string WindowWidthFieldName        = "#=qTRh7GI6bJni3uI$8LHzuzw==";
        private const string WindowHeightFieldName       = "#=qvb1pN2Rp3o_9MTHGqX$Qkw==";

        protected override void OnAdded(object screen)
        {
            ResolveReflection();
            ResolveSliderLabels();
            RegisterElements();
        }

        // Register UI elements with the screen's ElementRegistry. When
        // the cursor lands on a hotspot whose captured label matches a
        // registered key, ScreenView.ReadFocusFromCursor returns the
        // element's composed focus text instead of the bare label —
        // sliders now speak as "{label}. Slider. {percent}." and
        // re-announce just the new value when the player adjusts them.
        //
        // HotspotLabelCapture suppresses the section-heading prefix
        // when the button text matches its own heading (sliders are
        // labelled the same as their heading row), so the captured
        // label is bare "{slider label}" — registering under the
        // already-resolved Localizer text is what the cursor path
        // looks up.
        //
        // Display Mode / Display Quality / Auto-Advance / Language are
        // intentionally left as bare-label hotspots for now: we don't
        // have correct reflection for their current values yet, and a
        // RadioButtonElement that always says "not selected" would be
        // worse than the current "Fullscreen" bare-label announcement.
        // Wire them as a follow-up once the right backing fields are
        // identified.
        private void RegisterElements()
        {
            Elements.RegisterByKey(_slider1Label, new SliderElement(
                _slider1Label,
                getValue: () => TryReadFloatContainer(_slider1Field),
                format:   FormatVolumePercent));
            Elements.RegisterByKey(_slider2Label, new SliderElement(
                _slider2Label,
                getValue: () => TryReadFloatContainer(_slider2Field),
                format:   FormatVolumePercent));
            Elements.RegisterByKey(_slider3Label, new SliderElement(
                _slider3Label,
                getValue: () => TryReadFloatContainer(_slider3Field),
                format:   FormatVolumePercent));

            // Mutually-exclusive option groups, each wrapped in a
            // RowContainer so the group label ("Display Mode",
            // "Auto-Advance", ...) is announced once when focus
            // enters the row — then subsequent moves between
            // siblings inside speak only the radio. Radios are
            // registered under the section-prefixed keys
            // HotspotLabelCapture produces ("Display Mode:
            // Fullscreen", ...) so the cursor-to-element lookup
            // still resolves; the container path provides the
            // group context separately at announcement time.

            // Display Mode: Fullscreen ⇔ displayMode == true.
            var displayModeRow = new RowContainer("Display Mode");
            AddRadio(displayModeRow, "Display Mode: Fullscreen", "Fullscreen",
                () => TryReadBoolContainer(_displayModeField) == true);
            AddRadio(displayModeRow, "Display Mode: Windowed", "Windowed",
                () => TryReadBoolContainer(_displayModeField) == false);

            // Display Quality: backing field is the "Low" boolean
            // (true ⇔ Low selected). High is just !Low.
            var displayQualityRow = new RowContainer("Display Quality");
            AddRadio(displayQualityRow, "Display Quality: High (4K)", "High (4K)",
                () => TryReadBoolContainer(_displayQualityLowField) == false);
            AddRadio(displayQualityRow, "Display Quality: Low (2K)", "Low (2K)",
                () => TryReadBoolContainer(_displayQualityLowField) == true);

            // Window Size: dynamic — enumerate GameLogic.index2_0
            // at registration time and add one radio per
            // resolution to the container.
            var windowSizeRow = new RowContainer("Window Size");
            RegisterWindowSizeRadios(windowSizeRow);

            // Auto-Advance: combined predicate over enabled (bool)
            // and mode (int). Off when enabled == false; Normal /
            // Slower when enabled == true and mode picks the row.
            var autoAdvanceRow = new RowContainer("Auto-Advance");
            AddRadio(autoAdvanceRow, "Auto-Advance: Off", "Off",
                () => TryReadBoolContainer(_autoAdvanceEnabledField) == false);
            AddRadio(autoAdvanceRow, "Auto-Advance: Normal", "Normal",
                () => TryReadBoolContainer(_autoAdvanceEnabledField) == true
                   && TryReadIntContainer(_autoAdvanceModeField) == 0);
            AddRadio(autoAdvanceRow, "Auto-Advance: Slower", "Slower",
                () => TryReadBoolContainer(_autoAdvanceEnabledField) == true
                   && TryReadIntContainer(_autoAdvanceModeField) == 1);

            // Language: state predicate is wired (language
            // reflection already works). Each option's isSelected
            // compares the current language enum value to the value
            // for this button.
            var languageRow = new RowContainer("Language");
            RegisterLanguageRadios(languageRow);

            Log($"[SettingsView] registered {Elements.Count} element(s)");
        }

        // Build a RadioButtonElement, add it to the container, and
        // register it under the section-prefixed HotspotLabelCapture
        // key. Cuts down repetition across the four groups.
        private void AddRadio(RowContainer row, string key, string label, Func<bool> isSelected)
        {
            var radio = new RadioButtonElement(label, isSelected);
            row.Add(radio);
            Elements.RegisterByKey(key, radio);
        }

        // Map from Enum.GetName(_languageEnumType, value) → the
        // localized on-screen text the engine draws via
        // GClass147.dictionary_1[Language.X]. The two diverge for
        // most non-English entries (the on-screen form is the
        // language's own endonym), and the hotspot is registered
        // under the on-screen form via HotspotLabelCapture.
        // Updated alongside SettingsScreen.cs:259-271 whenever
        // Zachtronics adds or renames a language option.
        private static readonly System.Collections.Generic.Dictionary<string, string>
            LanguageEnumNameToLabel = new System.Collections.Generic.Dictionary<string, string>
        {
            { "English",    "English"   },
            { "Chinese",    "中文"      },
            { "Japanese",   "日本語"    },
            { "German",     "Deutsch"   },
            { "Spanish",    "Español"   },
            { "French",     "Français"  },
            { "Italian",    "Italiano"  },
            { "Hungarian",  "Magyar"    },
            { "Portuguese", "Português" },
            { "Russian",    "Русский"   },
        };

        private void RegisterWindowSizeRadios(RowContainer row)
        {
            if (_resolutionsArrayField == null
                || _index2WidthField == null || _index2HeightField == null)
            {
                Log("[SettingsView] window size: reflection incomplete, skipping");
                return;
            }
            Array? arr;
            try { arr = _resolutionsArrayField.GetValue(null) as Array; }
            catch (Exception ex) { Log($"[SettingsView] window size: array read err: {ex.Message}"); return; }
            if (arr == null) { Log("[SettingsView] window size: array is null"); return; }

            int registered = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                object? entry = arr.GetValue(i);
                if (entry == null) continue;
                int w, h;
                try
                {
                    w = (int)_index2WidthField.GetValue(entry)!;
                    h = (int)_index2HeightField.GetValue(entry)!;
                }
                catch { continue; }

                string label = $"{w} x {h}";
                string key   = $"Window Size: {label}";
                int capturedW = w, capturedH = h;
                AddRadio(row, key, label, () =>
                    TryReadIntContainer(_windowWidthField)  == capturedW
                 && TryReadIntContainer(_windowHeightField) == capturedH);
                registered++;
            }
            Log($"[SettingsView] window size: registered {registered} resolution radio(s)");
        }

        private void RegisterLanguageRadios(RowContainer row)
        {
            if (_languageEnumType == null) return;
            foreach (object enumVal in Enum.GetValues(_languageEnumType))
            {
                int    intVal     = Convert.ToInt32(enumVal);
                string enumName   = Enum.GetName(_languageEnumType, enumVal) ?? string.Empty;
                if (!LanguageEnumNameToLabel.TryGetValue(enumName, out string? localized))
                    localized = enumName;
                string key = $"Language: {localized}";
                AddRadio(row, key, localized, () => TryReadLanguage() == intVal);
            }
        }

        private static string FormatVolumePercent(float v)
        {
            if (v < 0f) return string.Empty;
            return Loc.Fmt("format.percent", (int)Math.Round(v * 100f));
        }

        protected override void OnFocus()
        {
            Speech.Speak(Loc.Get("settings.title"));
        }

        protected override (object? key, string? label) ReadCurrentFocus()
            => ReadFocusFromCursor();

        protected override void OnUpdate()
        {
            // Value-change announcements (slider percent, language
            // selection flip) are handled by the registered elements'
            // PollReactiveChange — ScreenView.NotifyUpdate drains
            // them before delegating here. Nothing left to do at
            // this layer.
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

            _displayModeField        = tSettings.GetField(DisplayModeFieldName,        pubInst);
            _displayQualityLowField  = tSettings.GetField(DisplayQualityLowFieldName,  pubInst);
            _autoAdvanceEnabledField = tSettings.GetField(AutoAdvanceEnabledFieldName, pubInst);
            _autoAdvanceModeField    = tSettings.GetField(AutoAdvanceModeFieldName,    pubInst);
            _windowWidthField        = tSettings.GetField(WindowWidthFieldName,        pubInst);
            _windowHeightField       = tSettings.GetField(WindowHeightFieldName,       pubInst);
            if (_displayModeField != null)
            {
                _boolContainerGet = _displayModeField.FieldType.GetMethod(
                    ContainerGetValueName, BindingFlags.Public | BindingFlags.Instance);
            }
            if (_autoAdvanceModeField != null)
            {
                _intContainerGet = _autoAdvanceModeField.FieldType.GetMethod(
                    ContainerGetValueName, BindingFlags.Public | BindingFlags.Instance);
            }

            // Window-size enumeration: static Index2[] on GameLogic
            // holds every (width × height) option the engine renders
            // in SettingsScreen.cs:228-231. Located by signature
            // (only static Index2[] field on GameLogic). Index2 has
            // two public int fields in declaration order — width
            // first, height second.
            Type? tIndex2 = Nagoya.FindType("Index2");
            if (tIndex2 != null)
            {
                foreach (FieldInfo sf in tGL.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (sf.FieldType.IsArray && sf.FieldType.GetElementType() == tIndex2)
                    {
                        _resolutionsArrayField = sf;
                        break;
                    }
                }
                var intFields = new System.Collections.Generic.List<FieldInfo>();
                foreach (FieldInfo f in tIndex2.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(int)) intFields.Add(f);
                }
                if (intFields.Count >= 2)
                {
                    _index2WidthField  = intFields[0];
                    _index2HeightField = intFields[1];
                }
            }

            Log($"[SettingsView] resolved: singleton={_gameLogicSingleton != null} " +
                $"settings={_settingsField != null} " +
                $"sliders={_slider1Field != null}/{_slider2Field != null}/{_slider3Field != null} " +
                $"floatGet={_floatContainerGet != null} " +
                $"lang={_languageField != null} langGet={_langContainerGet != null} " +
                $"langEnum={_languageEnumType?.Name ?? "<null>"} " +
                $"displayMode={_displayModeField != null} " +
                $"displayQualityLow={_displayQualityLowField != null} " +
                $"autoAdv.en={_autoAdvanceEnabledField != null} mode={_autoAdvanceModeField != null} " +
                $"windowW={_windowWidthField != null} windowH={_windowHeightField != null} " +
                $"resolutions={_resolutionsArrayField != null} " +
                $"index2.w={_index2WidthField?.Name ?? "<null>"} h={_index2HeightField?.Name ?? "<null>"} " +
                $"boolGet={_boolContainerGet != null} intGet={_intContainerGet != null}");
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

        // Returns null when the field isn't resolved or the read
        // fails — callers translate that to the "state unknown"
        // RadioButtonElement variant via the nullable predicate.
        private bool? TryReadBoolContainer(FieldInfo? field)
        {
            if (field == null || _boolContainerGet == null
                || _gameLogicSingleton == null || _settingsField == null)
                return null;
            try
            {
                object? gl = _gameLogicSingleton.GetValue(null);
                if (gl == null) return null;
                object? settings = _settingsField.GetValue(gl);
                if (settings == null) return null;
                object? container = field.GetValue(settings);
                if (container == null) return null;
                return (bool)_boolContainerGet.Invoke(container, null)!;
            }
            catch { return null; }
        }

        private int? TryReadIntContainer(FieldInfo? field)
        {
            if (field == null || _intContainerGet == null
                || _gameLogicSingleton == null || _settingsField == null)
                return null;
            try
            {
                object? gl = _gameLogicSingleton.GetValue(null);
                if (gl == null) return null;
                object? settings = _settingsField.GetValue(gl);
                if (settings == null) return null;
                object? container = field.GetValue(settings);
                if (container == null) return null;
                return (int)_intContainerGet.Invoke(container, null)!;
            }
            catch { return null; }
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

    }
}
