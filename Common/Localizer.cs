using System;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Single-source localization chain for the game's int → string lookup.
    ///
    /// Replaces five separate per-observer copies of the decode-key + localize
    /// resolution dance (Title, Escape, Settings, Chapter, Phone).
    ///
    /// The game's chain is:
    ///   decodeKey(int)                    → string strKey  (obfuscated key)
    ///   localize(string strKey, string)   → LocString
    ///   LocString.ToString()              → current-language string
    ///
    /// Both static methods are resolved on first access — preferred by known
    /// obfuscated names from Eazfuscator 3.3, with signature-based fallback so
    /// the binding survives a game patch that re-randomizes hashes.
    /// </summary>
    internal static class Localizer
    {
        // Stable across the shipped build.
        private const string DecodeKeyClassName    = "#=qZdRtwylTaejMMCD3bjmW2xhXYFGcLzdSpS10AIbb5eg=";
        private const string DecodeKeyMethodName   = "#=q3riQgK833hoS2y2rKnqikg==";
        private const string LocalizerClassName    = "#=q$q_5RIWNSfdbsTA5NIdJHA==";
        private const string LocalizerMethodName   = "#=qjChrmrpZYENaTOkSMVghpQ==";

        // A well-known live key used to validate signature-based fallback.
        // 407423898 → "Continue" (TitleScreen Continue button).
        private const int ProbeKey = 407423898;

        private static MethodInfo? _decodeKey;   // (int) → string
        private static MethodInfo? _localize;    // (string strKey, string fallback) → LocString
        private static bool _initTried;
        private static readonly object _gate = new object();

        public static bool IsReady => _decodeKey != null && _localize != null;

        /// <summary>
        /// Idempotent resolution of the localization chain. Returns
        /// <see cref="IsReady"/> after attempting.
        /// </summary>
        public static bool TryInit()
        {
            if (_initTried) return IsReady;
            lock (_gate)
            {
                if (_initTried) return IsReady;
                ResolveDecodeKey();
                ResolveLocalize();
                _initTried = true;
                return IsReady;
            }
        }

        /// <summary>
        /// Return the localized string for <paramref name="key"/>, or
        /// <paramref name="fallback"/> if the chain is unavailable or fails.
        /// </summary>
        public static string Localize(int key, string fallback = "")
        {
            string? result = TryLocalize(key);
            return result ?? fallback;
        }

        /// <summary>
        /// Return the localized string for <paramref name="key"/>, or null
        /// if the chain is unavailable or any step fails.
        /// </summary>
        public static string? TryLocalize(int key)
        {
            if (!TryInit()) return null;
            try
            {
                object? strKeyObj = _decodeKey!.Invoke(null, new object[] { key });
                if (strKeyObj is not string strKey || strKey.Length == 0) return null;
                object? locStr = _localize!.Invoke(null, new object[] { strKey, "" });
                return locStr?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Look up a string-keyed entry directly — for engine strings
        /// like "Set phase" or "Sentiment analyzer: {0} [{1}]" where
        /// the English literal IS the dictionary key (no int-decode
        /// step). The <paramref name="comment"/> disambiguates strings
        /// that share an English form but mean different things in
        /// context (engine uses it for "pos" / "neg" etc.); pass ""
        /// for entries with no comment. Returns null if the chain
        /// isn't ready or the lookup throws.
        /// </summary>
        public static string? TryLocalizeStringKey(string englishKey, string comment = "")
        {
            if (string.IsNullOrEmpty(englishKey)) return null;
            if (!TryInit()) return null;
            try
            {
                object? locStr = _localize!.Invoke(null, new object[] { englishKey, comment });
                return locStr?.ToString();
            }
            catch { return null; }
        }

        // ── Resolution ─────────────────────────────────────────────────────

        private static void ResolveDecodeKey()
        {
            // Preferred: exact class + method by known names. The decoder
            // is internal, so NonPublic is required; with Public alone this
            // never matched and every launch fell through to the scan below.
            Type? tDecode = Nagoya.FindType(DecodeKeyClassName);
            if (tDecode != null)
            {
                _decodeKey = tDecode.GetMethod(
                    DecodeKeyMethodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(int) }, null);
                if (_decodeKey != null) return;
            }

            // Fallback: scan every type for a static (int) → string method that
            // decodes our probe key. Several game methods map an int to some
            // string, and the scan order follows the obfuscated type order,
            // which changes between game builds — the 2026-11 build put a
            // method first that just formats the number, so every label read
            // as digits. Accept only a result that isn't the number itself.
            foreach (Type t in Nagoya.Types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                foreach (MethodInfo m in methods)
                {
                    ParameterInfo[] parms = m.GetParameters();
                    if (parms.Length != 1 || parms[0].ParameterType != typeof(int) ||
                        m.ReturnType != typeof(string)) continue;
                    try
                    {
                        if (m.Invoke(null, new object[] { ProbeKey }) is string s && s.Length > 0
                            && !IsAllDigits(s))
                        {
                            _decodeKey = m;
                            return;
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool IsAllDigits(string s)
        {
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static void ResolveLocalize()
        {
            if (_decodeKey == null) return;

            // Preferred: exact class + method by known names.
            Type? tLoc = Nagoya.FindType(LocalizerClassName);
            if (tLoc != null)
            {
                foreach (MethodInfo m in tLoc.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != LocalizerMethodName) continue;
                    ParameterInfo[] parms = m.GetParameters();
                    if (parms.Length == 2 &&
                        parms[0].ParameterType == typeof(string) &&
                        parms[1].ParameterType == typeof(string))
                    {
                        _localize = m;
                        return;
                    }
                }
            }

            // Fallback: signature scan. Find a (string, string) static method
            // whose return value's ToString() resolves the probe key correctly.
            string? probeStrKey = _decodeKey.Invoke(null, new object[] { ProbeKey }) as string;
            if (probeStrKey == null) return;

            foreach (Type t in Nagoya.Types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                foreach (MethodInfo m in methods)
                {
                    ParameterInfo[] parms = m.GetParameters();
                    if (parms.Length != 2 ||
                        parms[0].ParameterType != typeof(string) ||
                        parms[1].ParameterType != typeof(string)) continue;
                    try
                    {
                        object? result = m.Invoke(null, new object[] { probeStrKey, "" });
                        if (result != null && result.ToString() is { Length: > 0 })
                        {
                            _localize = m;
                            return;
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
