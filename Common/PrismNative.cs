using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ElizaAccess.Common
{
    /// <summary>
    /// P/Invoke layer for <c>prism.dll</c>, the native screen-reader
    /// abstraction library from <c>https://github.com/ethindp/prism</c>.
    ///
    /// We can't use the managed Prismatoid wrapper directly — it ships
    /// only as <c>net10.0</c> and the game runs on .NET Framework 4.7.2.
    /// Instead we vendor the native <c>prism.dll</c> from the Prismatoid
    /// NuGet package's <c>runtimes/win-x64/native/</c> folder and bind
    /// it ourselves. The C ABI is defined in
    /// <c>prism/include/prism.h</c> — only the calls we actually use
    /// are wrapped here.
    ///
    /// Strings cross the boundary as UTF-8 (the C side takes
    /// <c>const char*</c>). .NET Framework 4.7.2 doesn't have the
    /// <c>LPUTF8Str</c> marshaller built into the attribute, so we
    /// allocate/copy/free manually with <see cref="Utf8Alloc"/> /
    /// <see cref="Marshal.FreeHGlobal"/>.
    /// </summary>
    internal static class PrismNative
    {
        private const string Dll = "prism";

        // ── Error codes ────────────────────────────────────────────────
        public enum PrismError
        {
            Ok = 0,
            NotInitialized,
            InvalidParam,
            NotImplemented,
            NoVoices,
            VoiceNotFound,
            SpeakFailure,
            MemoryFailure,
            RangeOutOfBounds,
            Internal,
            NotSpeaking,
            NotPaused,
            AlreadyPaused,
            InvalidUtf8,
            InvalidOperation,
            AlreadyInitialized,
            BackendNotAvailable,
            Unknown,
            InvalidAudioFormat,
            InternalBackendLimitExceeded,
            BackendEnteredUndefinedState,
        }

        // ── PrismConfig struct ─────────────────────────────────────────
        // typedef struct { uint8_t version; } PrismConfig;
        // Pack=1 because the C struct has no padding alignment hints —
        // matches the natural layout of a single byte.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct PrismConfig
        {
            public byte Version;
        }

        // ── Imports we actually call ───────────────────────────────────

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_config_init")]
        public static extern PrismConfig prism_config_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_init")]
        public static extern IntPtr prism_init(ref PrismConfig cfg);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_shutdown")]
        public static extern void prism_shutdown(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_registry_acquire_best")]
        public static extern IntPtr prism_registry_acquire_best(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_backend_initialize")]
        public static extern PrismError prism_backend_initialize(IntPtr backend);

        // Note: the `interrupt` arg is a C `bool` (1 byte on Windows
        // x64); marshal as I1 so the size matches.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_backend_speak")]
        public static extern PrismError prism_backend_speak(
            IntPtr backend, IntPtr utf8Text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        // Speech AND braille (screen readers with a braille display);
        // NotImplemented on speech-only backends such as SAPI.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_backend_output")]
        public static extern PrismError prism_backend_output(
            IntPtr backend, IntPtr utf8Text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_backend_stop")]
        public static extern PrismError prism_backend_stop(IntPtr backend);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_backend_name")]
        public static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "prism_error_string")]
        public static extern IntPtr prism_error_string(PrismError error);

        // ── String marshalling helpers ────────────────────────────────

        /// <summary>Allocate an unmanaged UTF-8 buffer containing the
        /// given .NET string and a trailing null terminator. The caller
        /// must free the returned pointer with <see cref="Marshal.FreeHGlobal"/>.</summary>
        public static IntPtr Utf8Alloc(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }

        /// <summary>Read a null-terminated UTF-8 string from an
        /// unmanaged pointer. Returns empty string for null input.
        /// The pointer's storage is owned by Prism; we don't free it.</summary>
        public static string Utf8Decode(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
