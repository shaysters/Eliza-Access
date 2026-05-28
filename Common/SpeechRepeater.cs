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
    /// <see cref="SpeechEngine.LastSpoken"/> is the narrative-content
    /// replay target — set only by repeatable Speak calls and
    /// RecordSilent (Evelyn's thoughts / look-around / locked-app text
    /// via FakeAppScreen, and conversation dialogue). UI / navigation
    /// announcements (menu options, focus labels, the Eliza-app AR
    /// log) don't update it, so R re-reads the last story content even
    /// after the player has been navigating menus.
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
