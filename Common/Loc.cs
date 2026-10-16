using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Localization for the mod's own spoken strings. Tables ship as
    /// embedded JSON resources (<c>Localization/&lt;Language&gt;/ui.json</c>),
    /// one per game language, keyed by flat dotted keys.
    ///
    /// Lookup order for <see cref="Get"/>:
    ///   current-language table → English table → the key itself.
    /// Tables are sparse: a key absent from a non-English table falls
    /// back to English, so a partial translation degrades gracefully
    /// rather than showing blanks.
    ///
    /// The current language is read from the engine
    /// (GameLogic → settings → the <c>Language</c> Container) via the
    /// same reflection chain SettingsView uses; the enum value's name
    /// ("English", "German", "Japanese", …) selects the table.
    /// <see cref="RefreshLanguage"/> is cheap and is polled so a
    /// mid-session language change re-loads the right table.
    /// </summary>
    internal static class Loc
    {
        // Reflection chain to the current Language enum value (names
        // shared with SettingsView — stable across builds).
        private const string SettingsFieldName    = "#=qcfPtknXZ5IEbjuOI2Z5gkg==";
        private const string LanguageFieldName     = "#=qdiitE$xHgYx$UAbxCq7WdA==";
        private const string ContainerGetValueName = "#=qUrHgFy5gmcMr7GPeyD2HtQ==";

        private const string DefaultLanguage = "English";
        private const string ResourcePrefix  = "ElizaAccess.Localization.";
        private const string ResourceSuffix   = ".ui.json";
        private const string ImagesResourceSuffix = ".images.json";

        private static Action<string> _log = _ => { };

        private static Dictionary<string, string> _english = new Dictionary<string, string>();
        private static Dictionary<string, string> _current = new Dictionary<string, string>();
        private static string _currentLanguage = "";   // unset until first refresh
        private static bool   _loaded;

        // Reflection handles for current-language detection.
        private static bool        _reflectTried;
        private static FieldInfo?  _glSingleton;
        private static FieldInfo?  _settingsField;
        private static FieldInfo?  _languageField;
        private static MethodInfo? _langContainerGet;
        private static Type?       _languageEnumType;

        /// <summary>Load the English table (always) and the current
        /// language's table. Call once at startup.</summary>
        public static void Init(Action<string> log)
        {
            _log = log ?? (_ => { });
            _english = LoadTable(DefaultLanguage) ?? new Dictionary<string, string>();
            _log($"[Loc] English table: {_english.Count} keys");
            RefreshLanguage();
        }

        /// <summary>Re-detect the current language and reload its table
        /// if it changed. Cheap no-op when unchanged; safe to poll.</summary>
        public static void RefreshLanguage()
        {
            string lang = DetectLanguage() ?? DefaultLanguage;
            if (_loaded && lang == _currentLanguage) return;
            _currentLanguage = lang;
            _loaded = true;
            _current = (lang == DefaultLanguage)
                ? _english
                : (LoadTable(lang) ?? new Dictionary<string, string>());
            _log($"[Loc] language → {lang} ({_current.Count} keys, English fallback {_english.Count})");
        }

        /// <summary>The language the currently loaded table belongs to
        /// ("English" until the first successful detection). Views that
        /// bake Loc strings into registered elements compare against
        /// this — not the engine's language field directly — so a
        /// rebuild can never race ahead of the table reload.</summary>
        public static string CurrentLanguage => _currentLanguage;

        /// <summary>Localized string for <paramref name="key"/>:
        /// current language → English → the key verbatim (so a missing
        /// key surfaces visibly rather than as an empty utterance).</summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (_current.TryGetValue(key, out string? v) && v != null) return v;
            if (_english.TryGetValue(key, out string? e) && e != null) return e;
            return key;
        }

        /// <summary>Like <see cref="Get"/> but null when the key is in
        /// neither table — for optional content such as image
        /// descriptions, where a missing entry means "skip".</summary>
        public static string? TryGet(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_current.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v)) return v;
            if (_english.TryGetValue(key, out string? e) && !string.IsNullOrEmpty(e)) return e;
            return null;
        }

        /// <summary><see cref="Get"/> + <see cref="string.Format"/>.
        /// Templates carry {0}, {1}, … placeholders.</summary>
        public static string Fmt(string key, params object[] args)
        {
            string fmt = Get(key);
            try { return string.Format(fmt, args); }
            catch (FormatException)
            {
                // A malformed/over-translated template (placeholder
                // dropped or mangled) shouldn't crash speech — fall
                // back to the raw template.
                return fmt;
            }
        }

        // ── Table loading (embedded resources) ─────────────────────────

        // A language folder holds ui.json (mod UI strings) and optionally
        // images.json (scene / character / avatar descriptions, keyed
        // image.*). Both merge into one table so lookups and English
        // fallback work the same for either.
        private static Dictionary<string, string>? LoadTable(string language)
        {
            Dictionary<string, string>? table = LoadResource(language, ResourceSuffix, required: true);
            if (table == null) return null;
            Dictionary<string, string>? images = LoadResource(language, ImagesResourceSuffix, required: false);
            if (images != null)
                foreach (var kv in images) table[kv.Key] = kv.Value;
            return table;
        }

        private static Dictionary<string, string>? LoadResource(string language, string suffix, bool required)
        {
            string resource = ResourcePrefix + language + suffix;
            try
            {
                Assembly asm = typeof(Loc).Assembly;
                using Stream? s = asm.GetManifestResourceStream(resource);
                if (s == null)
                {
                    if (required) _log($"[Loc] resource not found: {resource}");
                    return null;
                }
                using var reader = new StreamReader(s);
                string text = reader.ReadToEnd();
                return JsonMini.ParseFlat(text);
            }
            catch (Exception ex)
            {
                _log($"[Loc] load {language} failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // ── Current-language detection ─────────────────────────────────

        private static string? DetectLanguage()
        {
            EnsureReflect();
            if (_glSingleton == null || _settingsField == null
                || _languageField == null || _langContainerGet == null
                || _languageEnumType == null) return null;
            try
            {
                object? gl = _glSingleton.GetValue(null);
                if (gl == null) return null;
                object? settings = _settingsField.GetValue(gl);
                if (settings == null) return null;
                object? container = _languageField.GetValue(settings);
                if (container == null) return null;
                object? value = _langContainerGet.Invoke(container, null);
                if (value == null) return null;
                return Enum.GetName(_languageEnumType, value);
            }
            catch { return null; }
        }

        private static void EnsureReflect()
        {
            if (_reflectTried) return;
            _reflectTried = true;
            try
            {
                Type? tGL = Nagoya.FindType("GameLogic");
                if (tGL == null) { _log("[Loc] GameLogic not found"); return; }

                foreach (FieldInfo sf in tGL.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (sf.FieldType == tGL) { _glSingleton = sf; break; }
                }

                _settingsField = tGL.GetField(SettingsFieldName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (_settingsField == null) { _log("[Loc] settings field not found"); return; }

                _languageField = _settingsField.FieldType.GetField(LanguageFieldName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (_languageField == null) { _log("[Loc] language field not found"); return; }

                _langContainerGet = _languageField.FieldType.GetMethod(
                    ContainerGetValueName, BindingFlags.Public | BindingFlags.Instance);
                if (_languageField.FieldType.IsGenericType)
                {
                    foreach (Type ga in _languageField.FieldType.GetGenericArguments())
                    {
                        if (ga.IsEnum) { _languageEnumType = ga; break; }
                    }
                }
                _log($"[Loc] language reflection: singleton={_glSingleton != null} " +
                     $"settings={_settingsField != null} langField={_languageField != null} " +
                     $"get={_langContainerGet != null} enum={_languageEnumType?.Name ?? "<null>"}");
            }
            catch (Exception ex)
            {
                _log($"[Loc] reflect failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
