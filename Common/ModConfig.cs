using System;
using System.Collections.Generic;
using System.IO;

namespace ElizaAccess.Common
{
    /// <summary>
    /// User-facing mod settings persisted to
    /// <c>{LocalAppData}\ElizaAccess\config.json</c>
    /// (a sibling of the Logs/ directory created by <see cref="Entry"/>).
    ///
    /// Sections are nested one level deep so related settings group
    /// naturally on disk. Add new settings as static properties on a
    /// nested class; <see cref="Load"/> and <see cref="Save"/> round-
    /// trip everything declared here, leaving anything they don't
    /// recognise untouched on disk (forward-compat with future
    /// versions that drop a setting).
    ///
    /// Defaults match the mod's behaviour before the setting existed,
    /// so a missing file or missing key means "unchanged behaviour."
    /// </summary>
    internal static class ModConfig
    {
        // ── Sections ────────────────────────────────────────────────────

        /// <summary>Settings that apply across the whole game (the F5
        /// menu's top level).</summary>
        public static class General
        {
            /// <summary>
            /// Whether to speak dialogue lines that have a voice clip.
            /// Off by default: the game plays the recording, and TTS over
            /// it would talk across the actor. On for braille users, or
            /// anyone who wants the text read in the game's language
            /// (the voice acting is English only).
            /// </summary>
            public static bool ReadVoicedDialogue = false;

            /// <summary>
            /// Whether to check GitHub for a newer release at launch and
            /// announce it on the title screen (<see cref="UpdateChecker"/>).
            /// </summary>
            public static bool CheckForUpdates = true;
        }

        /// <summary>Eliza-therapy-session behaviour knobs.</summary>
        public static class ElizaSessions
        {
            /// <summary>
            /// Whether to speak the AR bootup text the engine writes
            /// at the start of each session ("Eliza AR headset
            /// bootloader", "Boot ROM: …", "Firmware version: …",
            /// "Connecting to Skandha Cloud Services…", etc. — a
            /// fixed 10-line sequence from BeginTherapy). Default true.
            /// </summary>
            public static bool ReadBootupText = true;

            /// <summary>
            /// Whether to speak the "Sentiment analyzer: …" emotion-
            /// meter stream during a session (e.g. "Sentiment
            /// analyzer: hollow [neg]"). Locale-aware — the prefix
            /// is resolved via the engine's own localization table
            /// at startup. Default true. Also flipped by the F7 /
            /// R3 runtime hotkey (which saves the change), so the
            /// in-game toggle and config stay in sync.
            /// </summary>
            public static bool ReadSentimentAnalyzer = true;

            /// <summary>
            /// Whether to speak the "Multimodal sense: …" line the
            /// engine emits at scripted moments mid-session (e.g.
            /// "Multimodal sense: increased agitation"). Locale-aware
            /// — the prefix is resolved via the engine's own
            /// localization table at startup. Default true.
            /// </summary>
            public static bool ReadMultimodalSense = true;

            /// <summary>
            /// Whether to speak the "Set phase: …" lines the engine
            /// emits when the therapy session moves between phases
            /// (CONSIDERATION → INTERVENTION → CONCLUSION etc.).
            /// Locale-aware — the prefix is resolved via the
            /// engine's localization table. Default true.
            /// </summary>
            public static bool ReadSetPhase = true;

            /// <summary>
            /// Whether to speak session narration that doesn't match
            /// any of the recognised category prefixes — character
            /// enter/exit events, wait phrases, engine warnings
            /// ("[Warning] Proxy script deviation"), and perf
            /// instrumentation ("Load handler time: 87ms"). Exposed
            /// in the F7 menu as "Read miscellaneous notifications
            /// and system events" — the JSON key stays read_misc
            /// for backwards-compat. Default true.
            /// </summary>
            public static bool ReadMisc = true;
        }

        // ── Persistence ────────────────────────────────────────────────

        private static string? _configPath;
        // Captured-but-unrecognised on-disk content. Preserved on Save
        // so a hand-edit of a section/key the current build doesn't
        // know about survives a save cycle.
        private static Dictionary<string, Dictionary<string, object?>>? _extras;
        private static Action<string>? _log;

        /// <summary>Initialise the config path and load values from
        /// disk. Call once during startup, before any setting is
        /// read.</summary>
        public static void Init(string accessibilityDir, Action<string> log)
        {
            _log = log;
            try { Directory.CreateDirectory(accessibilityDir); } catch { }
            _configPath = Path.Combine(accessibilityDir, "config.json");
            Load();
        }

        private static void Load()
        {
            if (_configPath == null) return;
            if (!File.Exists(_configPath))
            {
                // First-run: write the file with current (default)
                // values so the player can find and hand-edit it
                // without needing to know the schema. Save uses the
                // SerialiseFields path, which is the single source of
                // truth for the schema, so the on-disk template stays
                // in sync with whatever the build supports.
                _log?.Invoke("[ModConfig] no config file — writing defaults");
                Save();
                return;
            }
            string text;
            try { text = File.ReadAllText(_configPath); }
            catch (Exception ex)
            {
                _log?.Invoke($"[ModConfig] read failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            Dictionary<string, Dictionary<string, object?>> data;
            try { data = JsonMini.Parse(text); }
            catch (Exception ex)
            {
                _log?.Invoke($"[ModConfig] parse failed: {ex.GetType().Name}: {ex.Message} — using defaults");
                return;
            }

            ApplyToFields(data, out var extras);
            _extras = extras;
            _log?.Invoke($"[ModConfig] loaded from {_configPath} " +
                         $"(eliza_sessions.read_bootup_text={ElizaSessions.ReadBootupText})");
        }

        /// <summary>Write current values back to disk. Call after any
        /// mutation that should persist.</summary>
        public static void Save()
        {
            if (_configPath == null) return;
            var data = SerialiseFields();
            // Re-merge captured extras so unknown sections/keys
            // survive a save round-trip.
            if (_extras != null)
            {
                foreach (var section in _extras)
                {
                    if (!data.TryGetValue(section.Key, out var inner))
                    {
                        inner = new Dictionary<string, object?>(StringComparer.Ordinal);
                        data[section.Key] = inner;
                    }
                    foreach (var kv in section.Value)
                    {
                        if (!inner.ContainsKey(kv.Key)) inner[kv.Key] = kv.Value;
                    }
                }
            }
            try
            {
                File.WriteAllText(_configPath, JsonMini.Write(data));
                _log?.Invoke($"[ModConfig] saved to {_configPath}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[ModConfig] save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Schema (single source of truth for sections + keys) ────────

        // The schema is captured here so Load and Save share one wiring
        // and a new setting only needs the declaration above plus one
        // line in each map.
        //
        // section_name  →  key_name  →  (read default-typed value, write current value)
        // Adding a setting:
        //   1. Declare static field on the nested section class
        //   2. Add it to ApplyToFields with a TryGetBool / TryGetInt / etc.
        //   3. Add it to SerialiseFields with the current value

        private static void ApplyToFields(
            Dictionary<string, Dictionary<string, object?>> data,
            out Dictionary<string, Dictionary<string, object?>> extras)
        {
            extras = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

            foreach (var section in data)
            {
                if (section.Key == "general")
                {
                    var remaining = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var kv in section.Value)
                    {
                        switch (kv.Key)
                        {
                            case "read_voiced_dialogue":
                                if (TryAsBool(kv.Value, out bool g1)) General.ReadVoicedDialogue = g1;
                                break;
                            case "check_for_updates":
                                if (TryAsBool(kv.Value, out bool g2)) General.CheckForUpdates = g2;
                                break;
                            default:
                                remaining[kv.Key] = kv.Value;
                                break;
                        }
                    }
                    if (remaining.Count > 0) extras[section.Key] = remaining;
                }
                else if (section.Key == "eliza_sessions")
                {
                    var remaining = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var kv in section.Value)
                    {
                        switch (kv.Key)
                        {
                            case "read_bootup_text":
                                if (TryAsBool(kv.Value, out bool b1)) ElizaSessions.ReadBootupText = b1;
                                break;
                            case "read_sentiment_analyzer":
                                if (TryAsBool(kv.Value, out bool b2)) ElizaSessions.ReadSentimentAnalyzer = b2;
                                break;
                            case "read_multimodal_sense":
                                if (TryAsBool(kv.Value, out bool b3)) ElizaSessions.ReadMultimodalSense = b3;
                                break;
                            case "read_set_phase":
                                if (TryAsBool(kv.Value, out bool b4)) ElizaSessions.ReadSetPhase = b4;
                                break;
                            case "read_misc":
                                if (TryAsBool(kv.Value, out bool b5)) ElizaSessions.ReadMisc = b5;
                                break;
                            default:
                                remaining[kv.Key] = kv.Value;
                                break;
                        }
                    }
                    if (remaining.Count > 0) extras[section.Key] = remaining;
                }
                else
                {
                    extras[section.Key] = section.Value;
                }
            }
        }

        private static Dictionary<string, Dictionary<string, object?>> SerialiseFields()
        {
            return new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
            {
                {
                    "general",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        { "read_voiced_dialogue", General.ReadVoicedDialogue },
                        { "check_for_updates",    General.CheckForUpdates    },
                    }
                },
                {
                    "eliza_sessions",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        { "read_bootup_text",        ElizaSessions.ReadBootupText        },
                        { "read_sentiment_analyzer", ElizaSessions.ReadSentimentAnalyzer },
                        { "read_multimodal_sense",   ElizaSessions.ReadMultimodalSense   },
                        { "read_set_phase",          ElizaSessions.ReadSetPhase          },
                        { "read_misc",               ElizaSessions.ReadMisc              },
                    }
                },
            };
        }

        private static bool TryAsBool(object? v, out bool result)
        {
            if (v is bool b) { result = b; return true; }
            result = false;
            return false;
        }
    }
}
