namespace ElizaAccess.Common
{
    /// <summary>
    /// Global "repeat last announcement" handler. On R-key rising edge,
    /// re-speak <see cref="SpeechEngine.LastSpoken"/> with interrupt=true
    /// so the user hears it from the start regardless of what (if
    /// anything) is currently playing.
    ///
    /// Must be polled AFTER <see cref="InputInterrupter"/>: that runs
    /// first and stops in-flight speech on any key press (R included);
    /// we then queue the replay into the now-empty queue. If our poll
    /// ran first, the interrupter would kill the replay we just
    /// queued.
    ///
    /// Works across every screen because LastSpoken is updated by
    /// every Speak call regardless of context — dialogue, focus
    /// announcements, pile readouts, fortune descriptions, etc.
    /// </summary>
    internal sealed class SpeechRepeater
    {
        private readonly SpeechEngine _speech;
        private readonly KeyEdge      _kbR = new KeyEdge(Keyboard.VK_R);

        public SpeechRepeater(SpeechEngine speech) { _speech = speech; }

        public void Poll()
        {
            if (!_kbR.Pressed()) return;
            string? text = _speech.LastSpoken;
            if (string.IsNullOrEmpty(text)) return;
            _speech.Speak(text!, interrupt: true);
        }
    }
}
