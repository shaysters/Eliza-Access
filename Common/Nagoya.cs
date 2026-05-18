using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Single-source assembly + type lookup for the Nagoya game assembly.
    ///
    /// Replaces nine separate per-observer copies of FindNagoya() / GetAllTypes()
    /// and per-observer Array.Find(all, t =&gt; t.Name == "...") scans.
    ///
    /// First access scans AppDomain.CurrentDomain.GetAssemblies() for the
    /// assembly named "Nagoya"; after that the result is cached. Type[] is
    /// resolved once on first request, with ReflectionTypeLoadException
    /// tolerated. FindType(name) results are cached per name.
    /// </summary>
    internal static class Nagoya
    {
        private static Assembly? _assembly;
        private static Type[]? _types;
        private static readonly Dictionary<string, Type?> _typeCache =
            new Dictionary<string, Type?>(StringComparer.Ordinal);
        private static readonly object _gate = new object();

        /// <summary>
        /// The Nagoya assembly, or null if it has not yet loaded into the AppDomain.
        /// Result is cached on first successful resolution.
        /// </summary>
        public static Assembly? Assembly
        {
            get
            {
                if (_assembly != null) return _assembly;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (string.Equals(a.GetName().Name, "Nagoya",
                                StringComparison.OrdinalIgnoreCase))
                            return _assembly = a;
                    }
                    catch { }
                }
                return null;
            }
        }

        /// <summary>True iff the Nagoya assembly has been resolved.</summary>
        public static bool IsLoaded => Assembly != null;

        /// <summary>
        /// All types defined in the Nagoya assembly. Empty array if the assembly
        /// is not yet loaded. Tolerates ReflectionTypeLoadException (returns the
        /// successfully loaded subset).
        /// </summary>
        public static Type[] Types
        {
            get
            {
                if (_types != null) return _types;
                Assembly? asm = Assembly;
                if (asm == null) return Array.Empty<Type>();

                lock (_gate)
                {
                    if (_types != null) return _types;
                    try { _types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex)
                    {
                        var list = new List<Type>();
                        if (ex.Types != null)
                            foreach (Type? t in ex.Types)
                                if (t != null) list.Add(t);
                        _types = list.ToArray();
                    }
                    return _types;
                }
            }
        }

        /// <summary>
        /// Find a type in the Nagoya assembly by its short Type.Name. Lookups are
        /// cached (including misses); null is returned if the type is not found
        /// or the assembly is not loaded.
        /// </summary>
        public static Type? FindType(string name)
        {
            lock (_gate)
            {
                if (_typeCache.TryGetValue(name, out Type? cached)) return cached;
                Type? found = null;
                foreach (Type t in Types)
                {
                    if (t.Name == name) { found = t; break; }
                }
                _typeCache[name] = found;
                return found;
            }
        }

        /// <summary>
        /// Block until the Nagoya assembly appears in the AppDomain or the
        /// timeout elapses. Returns true if the assembly is loaded.
        /// </summary>
        public static bool WaitForLoad(int timeoutMs)
        {
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                if (IsLoaded) return true;
                Thread.Sleep(500);
                elapsed += 500;
            }
            return IsLoaded;
        }
    }
}
