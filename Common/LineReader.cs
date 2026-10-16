using System;
using System.Collections.Generic;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Line-by-line reading of a scrollable phone document (an email, a
    /// Skandha message, a text conversation). Up/Down (arrow keys or
    /// controller) step a virtual line cursor, speak that line, and scroll
    /// the engine's view so the line sits at the top of the viewport.
    /// Home/End jump to the first/last line. Moving past either end does
    /// nothing — silence is the boundary cue.
    ///
    /// While active, the vertical axis is suppressed via
    /// <see cref="InputSuppressor"/> so the engine's own hold-to-scroll
    /// can't fight the cursor. Owners call <see cref="Open"/> when the
    /// document appears, <see cref="Poll"/> every tick, and
    /// <see cref="Close"/> when it goes away (including OnLeave, so the
    /// axis is always released).
    ///
    /// Transparency Mode: the inline phone shares the screen with the
    /// agree/disagree question hotspots, reached with the same arrows, and
    /// the engine only scrolls the open app while the cursor is on the
    /// phone. The reader follows the same rule — it only claims Up/Down
    /// while the cursor is on the phone's hotspot.
    ///
    /// Line positions come from the owner, computed from game state with
    /// <see cref="TextLayout"/> — see <see cref="EmailLines"/> and
    /// ChatAppSubView's conversation builder.
    /// </summary>
    internal sealed class LineReader
    {
        public readonly struct Line
        {
            public readonly string Speech;
            /// <summary>Distance from the top of the scroller content to
            /// the top of this line, in engine units.</summary>
            public readonly float Top;
            public Line(string speech, float top) { Speech = speech; Top = top; }
        }

        // Gap kept above the current line when scrolling to it, so it
        // isn't flush against the viewport edge.
        private const float ScrollMargin = 30f;

        private readonly SpeechEngine _speech;
        private readonly string _logTag;
        private readonly Action<string> _log;
        private readonly KeyEdge _kbUp   = new KeyEdge(Keyboard.VK_UP);
        private readonly KeyEdge _kbDown = new KeyEdge(Keyboard.VK_DOWN);
        private readonly KeyEdge _kbHome = new KeyEdge(Keyboard.VK_HOME);
        private readonly KeyEdge _kbEnd  = new KeyEdge(Keyboard.VK_END);

        private List<Line> _lines = new List<Line>();
        private Func<object?> _scroller = () => null;
        private int  _cursor = -1;
        private bool _open;
        private bool _axisClaimed;

        public LineReader(SpeechEngine speech, Action<string> log, string logTag)
        {
            _speech = speech;
            _log    = log;
            _logTag = logTag;
        }

        public bool IsOpen => _open;

        /// <summary>Start reading. <paramref name="scroller"/> returns the
        /// engine's GClass45 for this document. Nothing is spoken here —
        /// the owner decides what to auto-read on open.</summary>
        public void Open(List<Line> lines, int cursor, Func<object?> scroller)
        {
            _lines    = lines;
            _scroller = scroller;
            _cursor   = Clamp(cursor);
            _open     = true;
            _kbUp.Reset();
            _kbDown.Reset();
            _kbHome.Reset();
            _kbEnd.Reset();
            _log($"[{_logTag}] reader open: {lines.Count} lines, cursor={_cursor}");
        }

        /// <summary>Replace the lines (e.g. new chat messages arrived) and
        /// move the cursor.</summary>
        public void Update(List<Line> lines, int cursor)
        {
            _lines  = lines;
            _cursor = Clamp(cursor);
        }

        public void Close()
        {
            if (!_open) return;
            SetAxisClaim(false);
            _open   = false;
            _lines  = new List<Line>();
            _cursor = -1;
            _log($"[{_logTag}] reader closed");
        }

        public void Poll()
        {
            if (!_open) return;

            bool enabled = !TransparencyState.IsActive || CursorFocus.IsCursorOnPhoneHotspot();
            SetAxisClaim(enabled);
            bool up   = _kbUp.Pressed();
            bool down = _kbDown.Pressed();
            bool home = _kbHome.Pressed();
            bool end  = _kbEnd.Pressed();
            if (!enabled) return;      // arrows belong to the question buttons
            if (_lines.Count == 0) return;

            int next;
            if      (home)        next = 0;
            else if (end)         next = _lines.Count - 1;
            else if (up && !down) next = _cursor - 1;
            else if (down && !up) next = _cursor + 1;
            else return;
            // Past either end: stay put, silently — hearing nothing is
            // the boundary cue.
            if (next < 0 || next >= _lines.Count) return;
            _cursor = next;
            Line line = _lines[_cursor];
            TextLayout.SetScroll(_scroller(), line.Top - ScrollMargin);
            _speech.Speak(line.Speech);
        }

        private void SetAxisClaim(bool claim)
        {
            if (claim == _axisClaimed) return;
            if (claim) InputSuppressor.SuppressAxis(InputSuppressor.AxisVertical);
            else       InputSuppressor.ReleaseAxis(InputSuppressor.AxisVertical);
            _axisClaimed = claim;
        }

        private int Clamp(int cursor)
            => _lines.Count == 0 ? -1 : Math.Max(0, Math.Min(cursor, _lines.Count - 1));
    }
}
