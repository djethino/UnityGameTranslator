using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === A LAYOUT PASS WHOSE LINES GO TO OTHER COMPONENTS ===
        //
        // The layout pass of TextRouter.Route, in its second shape (recorded 2026-09-26, the
        // character sheets of a game): in ONE frame the game writes the whole text on a measuring
        // component, rebuilds it word by word — and when a line overflows, instead of writing a
        // break, it empties the component and starts again from the word that did not fit. Each
        // finished line is written, in the same frame, on a component of its own: those are what
        // the screen shows.
        //
        // Followed as one pass (FollowLayoutPass accepts a step that starts again inside the one
        // before), nothing of it is translated or sent on its own: the whole is, once. The
        // components that receive its lines are bound to it; when the whole has a translation,
        // it is wrapped the way the game wraps (TextRouter.Fit) and each line component gets its
        // line. When the whole was already translated as it was written, the game lays out the
        // translation itself and its lines are recognised as ours.
        //
        // ⚠ Bounded to what the pass proves: a line component is bound only in the frame the pass
        // ran, and only for a text that IS one of its lines. More lines than the game made go to
        // the last one; fewer leave the rest empty. It cannot make components.

        /// <summary>One whole laid out on a measuring component, its lines shown elsewhere.</summary>
        private sealed class SpreadLayout
        {
            public string Held;              // the whole as the game wrote it (what is queued)
            public List<string> Lines;       // the game's lines, as it cut them (trailing space kept)
            public bool Ours;                // it laid out our translation
            public long MeasureId;           // the measuring component
            public int Frame;                // the frame the pass ran in
            public string LaidOut;           // the lines joined by breaks: what its widths are read from
            public string WrappedFor;        // the translation the lines below were cut from
            public List<string> Wrapped;     // that translation, one entry per line component
        }

        // Keyed by a line, breaks set aside and trailing spaces trimmed: what a line component shows.
        private readonly Dictionary<string, SpreadLayout> _spreadLines = new Dictionary<string, SpreadLayout>();

        private static string LineKey(string text) => StripBreaks(text ?? "").TrimEnd();

        /// <summary>
        /// Whether <paramref name="step"/> goes on with the pass: the text from where its current
        /// line began, or — the second shape — a new line starting inside the step before, at
        /// the word it pushed out. Moves the pass's line start when it is the latter.
        /// </summary>
        private static bool ContinuesLayout(ComponentTextState state, string step)
        {
            string whole = state.LayoutWhole;
            int at = state.LayoutOffset;
            if (at + step.Length <= whole.Length && string.CompareOrdinal(whole, at, step, 0, step.Length) == 0)
                return true;

            string before = state.LayoutLastStep;
            if (string.IsNullOrEmpty(before) || step.Length == 0) return false;
            // The last start inside the step before: the word pushed out is the last one.
            for (int p = at + before.Length - 1; p > at; p--)
            {
                if (p + step.Length <= whole.Length && string.CompareOrdinal(whole, p, step, 0, step.Length) == 0)
                {
                    state.LayoutOffset = p;
                    state.LayoutStarts.Add(p);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The pass reached the end of the whole on a line that is not its first. Reached again
        /// after one more line started (the last word overflowed), it replaces what it noted.
        /// </summary>
        private void NoteSpread(ComponentTextState state, object comp, long compId)
        {
            var starts = state.LayoutStarts;
            var before = state.LayoutSpread;
            if (before != null)
            {
                if (before.Lines.Count == starts.Count) return;   // the same lines, drawn again
                foreach (string line in before.Lines)
                {
                    string key = LineKey(line);
                    if (_spreadLines.TryGetValue(key, out SpreadLayout noted) && noted == before)
                        _spreadLines.Remove(key);
                }
            }

            var lines = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : state.LayoutWhole.Length;
                lines.Add(state.LayoutWhole.Substring(starts[i], end - starts[i]));
            }

            var spread = new SpreadLayout
            {
                Held = state.LayoutHeld,
                Lines = lines,
                Ours = state.LayoutOurs,
                MeasureId = compId,
                Frame = _host.Frame,
                LaidOut = string.Join("\n", lines),
            };
            state.LayoutSpread = spread;
            foreach (string line in lines)
            {
                string key = LineKey(line);
                if (key.Length > 0) _spreadLines[key] = spread;
            }
            if (_host.DebugMode)
                _host.LogDebug($"[LAYOUT-SPREAD] comp={compId} the game laid out {(spread.Ours ? "our translation" : "its own text")} in {lines.Count} lines for other components: '{Clip(spread.Held, 60)}'");
        }

        /// <summary>
        /// A component showing a line of a spread layout: left alone, given its line of the
        /// translation, or bound to the whole so that line goes up when the translation arrives.
        /// False when the text is not such a line — routed as any other.
        /// </summary>
        private bool FollowSpreadLine(object comp, long compId, ref string textValue)
        {
            if (_spreadLines.Count == 0) return false;
            string key = LineKey(textValue);
            if (!_spreadLines.TryGetValue(key, out SpreadLayout spread)) return false;

            // The measuring component drawing one of its steps again: nothing to do.
            if (compId == spread.MeasureId) return true;

            var state = StateFor(compId);
            if (state.SpreadOf != spread && spread.Frame != _host.Frame) return false;

            int index = spread.Lines.FindIndex(line => LineKey(line) == key);
            state.SpreadOf = spread;
            state.SpreadIndex = index;
            if (spread.Ours) return true;

            var look = TextGate.Lookup(spread.Held, false, _host.GameStore, _host.NormalizeNumbers, _host.Variables, _host.MatchPattern);
            if (look.Outcome == GateOutcome.Hit && !string.IsNullOrEmpty(look.Value) && look.Value != spread.Held)
            {
                string line = SpreadLine(spread, index, look.Value, comp, compId);
                if (line != null) textValue = line;
                return true;
            }

            // Sent whole, once: this component is one more that waits for it.
            _host.Queue(spread.Held, comp, false);
            return true;
        }

        /// <summary>
        /// Line <paramref name="index"/> of <paramref name="translation"/>, wrapped the way the
        /// game wraps at the width its lines were cut to, measured on this line component; ""
        /// for a line the translation does not reach. Null when the width cannot be read.
        /// </summary>
        private string SpreadLine(SpreadLayout spread, int index, string translation, object comp, long compId)
        {
            if (index < 0 || index >= spread.Lines.Count) return null;

            if (spread.WrappedFor != translation)
            {
                if (!TryReadFit(spread.LaidOut, line => _host.MeasureLine(comp, line, null), out LineFit fit, out string whyNot))
                {
                    SayFit($"[LAYOUT-FIT] comp={compId} a line of a spread layout is not put up: {whyNot}");
                    return null;
                }
                string oneLine = translation.Replace("\r\n", " ").Replace('\n', ' ');
                string wrapped = WrapLikeTheGame(oneLine, fit, line => _host.MeasureLine(comp, line, null));
                if (wrapped == null) return null;

                var parts = new List<string>(wrapped.Split(new[] { fit.Break }, StringSplitOptions.None));
                int count = spread.Lines.Count;
                while (parts.Count > count)
                {
                    // More lines than the game made: the rest goes on its last one.
                    parts[count - 1] = parts[count - 1].TrimEnd() + " " + parts[count];
                    parts.RemoveAt(count);
                }
                while (parts.Count < count) parts.Add("");
                spread.Wrapped = parts;
                spread.WrappedFor = translation;
                foreach (string part in parts)
                    if (part.Length > 0) _concatTranslatedValues.Add(part);
            }

            string result = spread.Wrapped[index];
            _host.StoreOriginal(comp, spread.Lines[index]);
            TrackTranslation(compId, spread.Lines[index], result);
            return result;
        }

        /// <summary>
        /// A late translation for a component bound to a spread layout: its line, when it still
        /// shows the game's line; nothing otherwise. False when the component is not bound to the
        /// whole this translation is for.
        /// </summary>
        private bool LateSpread(long compId, object component, string current, string original, string translation, out string toWrite)
        {
            toWrite = null;
            var state = PeekState(compId);
            if (state?.SpreadOf == null || state.SpreadOf.Held != original) return false;
            if (state.SpreadIndex >= 0 && state.SpreadIndex < state.SpreadOf.Lines.Count
                && LineKey(state.SpreadOf.Lines[state.SpreadIndex]) == LineKey(current))
                toWrite = SpreadLine(state.SpreadOf, state.SpreadIndex, translation, component, compId);
            return true;
        }

        /// <summary>Whether a text read back is one line of a spread layout (the scene sweep).</summary>
        private bool IsSpreadLine(string text) => _spreadLines.Count > 0 && _spreadLines.ContainsKey(LineKey(text));
    }
}
