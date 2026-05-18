using System;
using HarmonyLib;
using ElizaAccess.Common.Patches;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Runtime method-patching bootstrap.
    ///
    /// Harmony lets the mod intercept game methods at their call sites — prefix
    /// (run our code before the original, optionally skip it), postfix (run our
    /// code after, optionally rewrite the return), and transpile (rewrite IL).
    /// We use it to replace polling with event-driven observation of game state.
    ///
    /// Patches live in <c>ElizaAccess.Common.Patches</c> and are discovered via
    /// <see cref="Harmony.PatchAll"/> scanning the mod assembly for
    /// <c>[HarmonyPatch]</c>-attributed types.
    ///
    /// Bootstrap is called from <c>Main.Run</c> once the Nagoya assembly has
    /// loaded. Harmony itself does not need Nagoya loaded, but most of our
    /// patches target Nagoya types, so we sequence them after the wait.
    /// </summary>
    internal static class HarmonyBootstrap
    {
        private const string HarmonyId = "com.elizaaccess.mod";

        public static bool IsApplied { get; private set; }

        /// <summary>
        /// Apply every <c>[HarmonyPatch]</c>-attributed class in the mod
        /// assembly. Idempotent. Returns true on success, false if patching
        /// failed (the mod continues to work in polling mode in that case).
        /// </summary>
        public static bool TryApplyAll(string logPath, Action<string> log)
        {
            if (IsApplied) return true;
            try
            {
                // Wire diagnostic patches' static log-path fields before
                // PatchAll registers them — first dispatch may fire as soon
                // as a patched method is called, which can happen during
                // the seed step below.
                CursorDiag.LogPath = logPath;
                KeyInjectDiag.LogPath = logPath;
                InputFlipGuard.LogPath = logPath;
                HotspotLabelCapture.LogPath = logPath;

                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(HarmonyBootstrap).Assembly);
                IsApplied = true;
                log($"[Harmony] All patches applied with id '{HarmonyId}'");

                // Confirm each patch's Target() actually resolved a method.
                // If a Target() returns null, Harmony silently skips that patch
                // (no exception, no warning) and the postfix never fires —
                // which is exactly the symptom we're chasing with CursorDiag.
                log($"[Harmony] CursorDiag     Target() = {CursorDiag.Target()?.Name     ?? "<NULL — NOT PATCHED>"}");
                log($"[Harmony] KeyInjectDiag  Target() = {KeyInjectDiag.Target()?.Name  ?? "<NULL — NOT PATCHED>"}");
                log($"[Harmony] CtrlModeLock   Target() = {ControllerModeLock.Target()?.Name ?? "<NULL — NOT PATCHED>"}");
                log($"[Harmony] InputFlipGuard Target() = {InputFlipGuard.Target()?.Name ?? "<NULL — NOT PATCHED>"}");
                log($"[Harmony] LabelCapture/TextDraw Target() = {HotspotLabelCapture.TextDrawPatch.Target()?.Name ?? "<NULL>"}");
                log($"[Harmony] LabelCapture/Hotspot  Target() = {HotspotLabelCapture.HotspotRegisterPatch.Target()?.Name ?? "<NULL>"}");
                log($"[Harmony] LabelCapture/Frame    Target() = {HotspotLabelCapture.FrameBoundaryPatch.Target()?.Name ?? "<NULL>"}");

                // Patches alter behaviour going forward, but VirtualInput's
                // static init already ran before Harmony loaded and set
                // IsControllerActive to false. Seed it true once so the
                // game's controller-mode focus logic activates immediately.
                ControllerModeLock.SeedTrue(log);

                return true;
            }
            catch (Exception ex)
            {
                log($"[Harmony] FAILED: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
