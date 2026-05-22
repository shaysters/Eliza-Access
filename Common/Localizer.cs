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
        private const string DecodeKeyClassName    = "#=q_9PuZzcJ_nq6MSqjSWnROZsVu6ji3l9QYNSPCJND$Xo=";
        private const string DecodeKeyMethodName   = "#=qkAu3PJ8V40C$xoElphddWg==";
        private const string LocalizerClassName    = "#=qvPVxzUse4V5ZHJvWow5MkA==";
        private const string LocalizerMethodName   = "#=qlH_leo7ESGaR6Lc4hZ87NQ==";

        // A well-known live key used to validate signature-based fallback.
        // 1497494027 → "Continue" (TitleScreen Continue button).
        private const int ProbeKey = 1497494027;

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
            // Preferred: exact class + method by known names.
            Type? tDecode = Nagoya.FindType(DecodeKeyClassName);
            if (tDecode != null)
            {
                _decodeKey = tDecode.GetMethod(
                    DecodeKeyMethodName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(int) }, null);
                if (_decodeKey != null) return;
            }

            // Fallback: scan every type for a static (int) → string method that
            // returns a non-empty value for our probe key.
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
                        if (m.Invoke(null, new object[] { ProbeKey }) is string s && s.Length > 0)
                        {
                            _decodeKey = m;
                            return;
                        }
                    }
                    catch { }
                }
            }
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
