using System;
using System.Collections;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Single-source binding for the game's ScreenStack and Maybe&lt;IScreen&gt;.
    ///
    /// Replaces ten separate per-observer copies of:
    ///   - resolving ScreenStack's static top-screen method
    ///   - resolving Maybe&lt;IScreen&gt;.HasValue() / GetValue()
    ///   - the Maybe-unwrap dance to test "is this screen on top?"
    ///
    /// Resolution strategy: known obfuscated names first; signature scan as
    /// fallback so the binding survives a game patch that re-randomizes hashes.
    /// </summary>
    internal static class ScreenStackBinding
    {
        // Stable across builds of Eazfuscator 3.3 — seen everywhere in the
        // decompile. Used as the preferred match; signature scan kicks in if
        // a future patch reroutes these.
        private const string TopScreenMethodName    = "#=qkap7xaC_N$VSJ7PEQqI5a45r9coJ4ApeZ_LtXKShlgE=";
        private const string MaybeHasValueMethodName = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValueMethodName = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        private static MethodInfo? _topScreenMethod;
        private static MethodInfo? _maybeHasValue;
        private static MethodInfo? _maybeGetValue;
        private static bool _initTried;
        private static readonly object _gate = new object();

        /// <summary>
        /// True when all three reflection handles are resolved.
        /// </summary>
        public static bool IsReady =>
            _topScreenMethod != null && _maybeHasValue != null && _maybeGetValue != null;

        /// <summary>
        /// The closed generic <c>Maybe&lt;IScreen&gt;</c> type used by the
        /// top-screen method's return signature, or null if not resolved.
        /// Callers can <c>.GetGenericTypeDefinition().MakeGenericType(typeof(U))</c>
        /// to construct <c>Maybe&lt;U&gt;</c> for other element types.
        /// </summary>
        public static Type? MaybeScreenType => _topScreenMethod?.ReturnType;

        /// <summary>
        /// Resolve all required reflection handles. Idempotent — subsequent calls
        /// return cached results. Returns <see cref="IsReady"/> after attempting.
        /// </summary>
        public static bool TryInit()
        {
            if (_initTried) return IsReady;
            lock (_gate)
            {
                if (_initTried) return IsReady;
                ResolveTopScreenMethod();
                ResolveMaybeMethods();
                _initTried = true;
                return IsReady;
            }
        }

        /// <summary>
        /// Return the current top screen, or null if none / binding not ready /
        /// the Maybe is empty.
        /// </summary>
        public static object? GetTop()
        {
            if (!TryInit()) return null;
            try
            {
                object? maybe = _topScreenMethod!.Invoke(null, null);
                if (maybe == null) return null;
                bool has = (bool)_maybeHasValue!.Invoke(maybe, null)!;
                if (!has) return null;
                return _maybeGetValue!.Invoke(maybe, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// True if the top screen's <see cref="Type.Name"/> equals <paramref name="typeName"/>.
        /// </summary>
        public static bool IsTop(string typeName) =>
            IsTop(typeName, out _);

        /// <summary>
        /// True if the top screen's <see cref="Type.Name"/> equals <paramref name="typeName"/>,
        /// with the matched screen instance in <paramref name="screen"/>.
        /// </summary>
        public static bool IsTop(string typeName, out object? screen)
        {
            screen = GetTop();
            return screen != null && screen.GetType().Name == typeName;
        }

        // ScreenStack.smethod_2() → List<IScreen>, bottom first. The known
        // name has drifted in at least one build, so a signature scan
        // (no-arg static returning List<T : IScreen>) backs it up.
        private const string StackListMethodName = "#=qaiNQEzpL$O8U_lFatuh9CQ==";
        private static MethodInfo? _stackListMethod;
        private static bool _stackListResolved;

        /// <summary>Name of the resolved stack-list method, for logging.</summary>
        public static string StackListMethodResolved => _stackListMethod?.Name ?? "<not resolved>";

        /// <summary>The game's screen stack, bottom first, or null.</summary>
        public static System.Collections.IList? GetStack()
        {
            if (!_stackListResolved)
            {
                _stackListResolved = true;
                Type? tStack = Nagoya.FindType("ScreenStack");
                Type? tIScreen = Nagoya.FindType("IScreen");
                if (tStack != null && tIScreen != null)
                {
                    const BindingFlags stat = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    foreach (MethodInfo m in tStack.GetMethods(stat))
                    {
                        if (m.Name == StackListMethodName && m.GetParameters().Length == 0)
                        { _stackListMethod = m; break; }
                    }
                    if (_stackListMethod == null)
                    {
                        foreach (MethodInfo m in tStack.GetMethods(stat))
                        {
                            if (m.GetParameters().Length != 0) continue;
                            Type rt = m.ReturnType;
                            if (!rt.IsGenericType || rt.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) continue;
                            if (!tIScreen.IsAssignableFrom(rt.GetGenericArguments()[0])) continue;
                            _stackListMethod = m;
                            break;
                        }
                    }
                }
            }
            try { return _stackListMethod?.Invoke(null, null) as System.Collections.IList; }
            catch { return null; }
        }

        // ── Resolution ─────────────────────────────────────────────────────

        private static void ResolveTopScreenMethod()
        {
            Type? tScreenStack = Nagoya.FindType("ScreenStack");
            Type? tIScreen     = Nagoya.FindType("IScreen");
            if (tScreenStack == null || tIScreen == null) return;

            // Preferred: exact name match.
            foreach (MethodInfo m in tScreenStack.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name == TopScreenMethodName && m.GetParameters().Length == 0)
                { _topScreenMethod = m; return; }
            }

            // Fallback: signature scan. Looking for a no-arg static method whose
            // return type is a single-generic-argument wrapper (Maybe<T>) over
            // IScreen. Exclude IEnumerable returns (the visible-list slice).
            foreach (MethodInfo m in tScreenStack.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0) continue;
                Type rt = m.ReturnType;
                if (!rt.IsGenericType) continue;
                if (typeof(IEnumerable).IsAssignableFrom(rt)) continue;
                Type[] args = rt.GetGenericArguments();
                if (args.Length == 1 && tIScreen.IsAssignableFrom(args[0]))
                { _topScreenMethod = m; return; }
            }
        }

        private static void ResolveMaybeMethods()
        {
            if (_topScreenMethod == null) return;
            Type maybeType = _topScreenMethod.ReturnType;

            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            _maybeHasValue = maybeType.GetMethod(MaybeHasValueMethodName, bf, null, Type.EmptyTypes, null);
            _maybeGetValue = maybeType.GetMethod(MaybeGetValueMethodName, bf, null, Type.EmptyTypes, null);

            // Signature fallback if names changed.
            if (_maybeHasValue == null)
            {
                foreach (MethodInfo mi in maybeType.GetMethods(bf))
                {
                    if (mi.GetParameters().Length == 0 && mi.ReturnType == typeof(bool))
                    { _maybeHasValue = mi; break; }
                }
            }
            if (_maybeGetValue == null)
            {
                Type tArg = maybeType.GetGenericArguments()[0];
                foreach (MethodInfo mi in maybeType.GetMethods(bf))
                {
                    if (mi.GetParameters().Length == 0 && tArg.IsAssignableFrom(mi.ReturnType))
                    { _maybeGetValue = mi; break; }
                }
            }
        }
    }
}
