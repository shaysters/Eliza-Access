using System;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Single source of speech-queue interruption. Polled once per Main
    /// iteration, BEFORE the screen views run.
    ///
    /// Speech rule we enforce: announcements never cut each other off.
    /// All <c>Speech.Speak</c> callers queue (interrupt=false). The queue
    /// is cleared only when the user provides input — the user's intent
    /// to act invalidates anything we were in the middle of saying.
    ///
    /// Detection: poll Windows-level keyboard state for any key
    /// transition false→true since the previous poll. If detected, call
    /// <see cref="SpeechEngine.Stop"/>. The next view/observer to speak
    /// this same iteration queues fresh into the now-empty queue.
    ///
    /// Notes:
    /// - Keyboard-only by design. The Harmony emulation surface makes
    ///   keyboard presses appear to the game as controller-button
    ///   presses (Enter→A, Escape→B, arrows→D-pad), so detecting
    ///   keyboard edges catches every input path the blind user has.
    ///   Real controllers go through SDL and aren't covered; if that
    ///   becomes a use case, add a smethod_18 sweep here.
    /// - First poll just snapshots state — it doesn't fire an interrupt
    ///   on whatever was held when the mod loaded.
    /// </summary>
    internal sealed class InputInterrupter
    {
        private readonly SpeechEngine _speech;
        private readonly bool[] _wasHeld = new bool[256];
        private bool _primed;

        public InputInterrupter(SpeechEngine speech)
        {
            _speech = speech;
        }

        public void Poll()
        {
            // Skip the sweep entirely when the game window isn't
            // foreground. Keyboard.IsHeld returns false for every key
            // when unfocused; if we ran the sweep we'd reset _wasHeld
            // to false for keys the user is actually still holding,
            // and the next focused tick would re-detect them as
            // rising edges — firing a spurious Stop() that wipes
            // whatever announcement was mid-play. Returning early
            // preserves _wasHeld from the last focused tick so the
            // next focused tick compares against a real baseline.
            if (!Keyboard.IsGameFocused()) return;

            if (!_primed)
            {
                // Snapshot whatever's currently held without firing —
                // user might be holding a key when the mod loads, and we
                // don't want to interrupt on that incidentally.
                for (int vk = 0; vk < 256; vk++)
                    _wasHeld[vk] = KeyboardSnapshot.IsHeld(vk);
                _primed = true;
                return;
            }

            bool risingEdge = false;
            // Skip VK 0-7 (unused/mouse). 0x08 = VK_BACK. 0xFF reserved.
            // Read via the shared snapshot so we don't race with the
            // KeyEdges elsewhere in this tick on GetAsyncKeyState's
            // 0x0001 bit.
            for (int vk = 0x08; vk < 256; vk++)
            {
                bool held = KeyboardSnapshot.IsHeld(vk);
                if (held && !_wasHeld[vk]) risingEdge = true;
                _wasHeld[vk] = held;
            }

            if (risingEdge) _speech.Stop();
        }
    }
}
