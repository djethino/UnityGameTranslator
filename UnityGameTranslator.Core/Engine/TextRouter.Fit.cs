using System;
using System.Collections.Generic;
using System.Text;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === THE GAME'S OWN LINE WIDTH, READ FROM ITS LAYOUT ===
        //
        // A game that lays out its text itself (TextRouter.Route, "layout pass") does it with one
        // rule, checked on 118 recorded passes with no exception (2026-09-26): append a word; if
        // the line overflows, rewrite it with a break before that word. So each of its layouts
        // says, about its width limit, two things: every line it kept fits, and every line it
        // refused — the line plus the word it pushed down — does not.
        //
        // That is what a translation arriving AFTER the game laid out the source is wrapped with
        // (decided by the user, 2026-09-26: "the least arbitrary way"): the same greedy rule, the
        // component's own measure, and a width no wider than the widest line the game kept —
        // never more than it accepted. The next time the line shows, the game lays out the
        // translation itself, from the cache.
        //
        // ⚠ Characters are not a measure (a line of 47 kept, one of 44 refused, in the same
        // text): the width is the engine's, of the text as the component draws it.

        /// <summary>
        /// The game has laid out <paramref name="whole"/> on this component as
        /// <paramref name="laidOut"/>: its measure is read from this layout later, at the size it
        /// was made at, and this is what the component shows until something replaces it.
        /// </summary>
        private void NoteLayout(ComponentTextState state, object comp, string whole, string laidOut, bool ours)
        {
            float? size = _host.FontSizeOf(comp);
            state.LastLayout = laidOut;
            state.LastLayoutSize = size;
            state.LastLayoutSource = ours ? null : whole;
            state.ShownWhole = whole;
            state.ShownLaidOut = laidOut;
            state.ShownSize = size;
            state.ShownIsOurs = ours;
        }

        /// <summary>
        /// The font of a component the game lays out itself has been resized (Size % applied):
        /// what it shows was broken into lines for the old size, and the game will not do it
        /// again until the line shows anew. Returns the same text laid out again for the size it
        /// draws at now — by the game's rule, at the width the game used — or null when there is
        /// nothing to redo: not a layout this router knows, the size has not moved, or no width
        /// can be read. Asked by the engine right after it changes a component's size.
        /// </summary>
        public string Relayout(object comp, long compId, string shown)
        {
            if (compId == -1 || shown == null) return null;
            var state = PeekState(compId);
            if (state?.ShownLaidOut == null || state.LastLayout == null || shown != state.ShownLaidOut) return null;

            float? size = _host.FontSizeOf(comp);
            if (size == null || state.ShownSize == null || Math.Abs(size.Value - state.ShownSize.Value) < 0.01f)
                return null;

            float? layoutSize = state.LastLayoutSize;
            if (!TryReadFit(state.LastLayout, line => _host.MeasureLine(comp, line, layoutSize), out LineFit fit, out string whyNot))
            {
                SayFit($"[LAYOUT-FIT] comp={compId} resized, and not laid out again: {whyNot}");
                return null;
            }
            string again = WrapLikeTheGame(state.ShownWhole, fit, line => _host.MeasureLine(comp, line, null));
            if (again == null) return null;

            if (state.ShownIsOurs)
            {
                state.LastTranslated = again;
                _concatTranslatedValues.Add(again);
            }
            else
            {
                // The game's own text, laid out anew: its redraws of either form are its layout.
                if (_layoutResults.TryGetValue(shown, out string source)) _layoutResults[again] = source;
            }
            state.ShownLaidOut = again;
            state.ShownSize = size;
            SayFit($"[LAYOUT-FIT] comp={compId} resized ({layoutSize:F0} → {size:F0}), laid out again at the game's width: '{Clip(again, 60)}'");
            return again;
        }

        private void SayFit(string line)
        {
            if (_layoutFitSaid >= 5) return;
            _layoutFitSaid++;
            _host.Log(line);
        }

        /// <summary>
        /// The translation of <paramref name="source"/>, wrapped to the width the game used when it
        /// last laid out a text on this component — to go up where the game shows its layout of
        /// that source. <paramref name="translation"/> when it is known (a late answer), looked up
        /// otherwise, and never queued from here. Null when there is nothing to put up: no
        /// translation yet, no layout read here, or a width this component cannot give — the
        /// game's layout then stays, as it did before.
        /// </summary>
        private string WrapTranslationOf(object comp, long compId, string source, string translation)
        {
            var state = PeekState(compId);
            if (state?.LastLayout == null || string.IsNullOrEmpty(source)) return null;

            if (translation == null)
            {
                var look = TextGate.Lookup(source, false, _host.GameStore, _host.NormalizeNumbers, _host.Variables, _host.MatchPattern);
                if (look.Outcome != GateOutcome.Hit) return null;
                translation = look.Value;
            }
            if (string.IsNullOrEmpty(translation) || translation == source) return null;

            // The source was one line; breaks a model put in its answer are not the game's.
            if (source.IndexOf('\n') < 0)
                translation = translation.Replace("\r\n", " ").Replace('\n', ' ');

            // The game's widths at the size it laid out at; the translation at the size it will be
            // drawn at. The same size unless the font was resized since (Size % applied).
            float? layoutSize = state.LastLayoutSize;
            if (!TryReadFit(state.LastLayout, line => _host.MeasureLine(comp, line, layoutSize), out LineFit fit, out string whyNot))
            {
                SayFit($"[LAYOUT-FIT] comp={compId} a translation arrived after the game laid out its source, and is not put up: {whyNot}");
                return null;
            }

            string wrapped = WrapLikeTheGame(translation, fit, line => _host.MeasureLine(comp, line, null));
            if (wrapped == null) return null;

            // Ours from now on: its redraws and read-backs are recognised as a translation, and the
            // runtime switch can put the source back.
            state.LastTranslated = wrapped;
            _concatTranslatedValues.Add(wrapped);
            _host.StoreOriginal(comp, source);
            TrackTranslation(compId, source, wrapped);
            state.ShownWhole = translation;
            state.ShownLaidOut = wrapped;
            state.ShownSize = _host.FontSizeOf(comp);
            state.ShownIsOurs = true;
            SayFit($"[LAYOUT-FIT] comp={compId} wrapped to the game's width (kept {fit.Kept:F1}, refused {fit.Refused:F1}): '{Clip(wrapped, 60)}'");
            return wrapped;
        }

        /// <summary>What one layout of a component says about its width limit.</summary>
        internal struct LineFit
        {
            public float Kept;       // the widest line the game kept
            public float Refused;    // the narrowest it refused (line + the word pushed down); +∞ when none
            public string Break;     // the break it writes: "\r\n" or "\n"
            public bool SpaceBeforeBreak;  // it leaves the space before a break in place
        }

        /// <summary>
        /// Reads a layout the game produced: the lines it kept, the lines it refused. False when
        /// the measure cannot say, or says something the rule contradicts — a refused line no
        /// wider than a kept one means this measure is not the game's, and nothing is cut with it.
        /// </summary>
        internal static bool TryReadFit(string laidOut, Func<string, float?> measure, out LineFit fit, out string whyNot)
        {
            fit = new LineFit { Kept = 0f, Refused = float.PositiveInfinity };
            whyNot = null;
            fit.Break = laidOut.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string[] lines = laidOut.Replace("\r\n", "\n").Split('\n');
            fit.SpaceBeforeBreak = lines.Length > 1 && lines[0].EndsWith(" ", StringComparison.Ordinal);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Length == 0) continue;
                float? w = measure(line);
                if (w == null) { whyNot = "the component could not measure its text"; return false; }
                if (w.Value > fit.Kept) fit.Kept = w.Value;

                if (i + 1 < lines.Length)
                {
                    string next = lines[i + 1].TrimStart();
                    int end = next.IndexOf(' ');
                    string pushed = end < 0 ? next.TrimEnd() : next.Substring(0, end);
                    if (pushed.Length == 0) continue;
                    float? r = measure(line + " " + pushed);
                    if (r == null) { whyNot = "the component could not measure its text"; return false; }
                    if (r.Value < fit.Refused) fit.Refused = r.Value;
                }
            }

            if (fit.Kept <= 0f) { whyNot = "nothing measurable was kept"; return false; }
            if (fit.Refused <= fit.Kept)
            {
                whyNot = $"a refused line ({fit.Refused:F1}) is no wider than a kept one ({fit.Kept:F1}): this is not the width the game measures";
                return false;
            }
            return true;
        }

        /// <summary>
        /// A text wrapped the way the game wraps: word after word, a break before the word that
        /// would make the line wider than <see cref="LineFit.Kept"/>. A single word wider than that
        /// stays whole on its own line — cutting inside a word is not something the game does.
        /// Null when the measure cannot say.
        /// </summary>
        internal static string WrapLikeTheGame(string text, LineFit fit, Func<string, float?> measure)
        {
            var units = BreakUnits(text);
            var sb = new StringBuilder();
            string line = "";
            foreach (string unit in units)
            {
                string candidate = line + unit;
                float? w = measure(candidate.TrimEnd());
                if (w == null) return null;
                if (line.Length > 0 && w.Value > fit.Kept)
                {
                    sb.Append(fit.SpaceBeforeBreak ? line : line.TrimEnd());
                    sb.Append(fit.Break);
                    line = unit.TrimStart();
                }
                else line = candidate;
            }
            sb.Append(line);
            return sb.ToString();
        }

        /// <summary>
        /// Where a line may be broken: after a space or a zero-width space (which WordBreaker puts
        /// between the words of scripts written without spaces), and between two characters of
        /// scripts that break anywhere (Han, kana) — except before the punctuation that must not
        /// start a line. Each unit keeps its trailing space.
        /// </summary>
        private static List<string> BreakUnits(string text)
        {
            var units = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool endHere = c == ' ' || c == '​';
                if (!endHere && i + 1 < text.Length && BreaksAnywhere(c) && BreaksAnywhere(text[i + 1])
                    && !NoLineStart(text[i + 1]))
                    endHere = true;
                if (endHere)
                {
                    // Spaces that follow stay with this unit.
                    while (i + 1 < text.Length && text[i + 1] == ' ') i++;
                    units.Add(text.Substring(start, i + 1 - start));
                    start = i + 1;
                }
            }
            if (start < text.Length) units.Add(text.Substring(start));
            return units;
        }

        private static bool BreaksAnywhere(char c) =>
            (c >= '぀' && c <= 'ヿ')    // hiragana, katakana
            || (c >= '㐀' && c <= '鿿') // CJK ideographs
            || (c >= '豈' && c <= '﫿')
            || (c >= '　' && c <= '〿') // CJK punctuation
            || (c >= '＀' && c <= '￯');

        private static bool NoLineStart(char c) => "、。，．！？：；」』）】〉》ー…・".IndexOf(c) >= 0;
    }
}
