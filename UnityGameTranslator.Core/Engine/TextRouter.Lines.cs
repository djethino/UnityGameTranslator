using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === TEXTS MADE OF LINES ===
        //
        // 🔴 One rule, decided with the user on 2026-09-26: **a line that is already a translation
        // never goes to the model again.** A game composing a text from lines — an event log it
        // adds to, a tooltip it rebuilds from parts it read back — hands us blocks where some
        // lines are ours and some are its own. Sent whole, every block was a new line for the
        // model: a log re-sent at every event (keys of 4 123 characters), tooltips half French
        // half Chinese stored in the file. Recorded in a game, analyse/banc-routage-texte.md.
        //
        // ⚠ The unit stays the BLOCK wherever nothing says otherwise: a sentence the game broke
        // over two lines is one block, and is translated as one. A block is cut into lines only
        // when one of its lines is already a translation — then its other lines are what is left
        // to translate, and they go one by one.

        /// <summary>
        /// A text looked up as one unit and, when it is not known whole and holds a line that is
        /// already a translation, line by line: that line kept, the others translated (and sent
        /// when unknown, unless <paramref name="skipQueueing"/>). What a part of an assembly goes
        /// through — a concat delta, an appended block, a part put back when an answer arrives.
        /// </summary>
        private string TranslateUnit(string text, object comp, bool isOwnUI, bool skipQueueing)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('\n') >= 0)
            {
                string whole = Translate(text, comp, isOwnUI, skipTypewriting: true, skipQueueing: true);
                if (whole != text) return whole;

                // Cut into lines when one of them is ours — or when the file already knows one of
                // them on its own: the block was learnt line by line before (cut the first time it
                // held a line of ours), and looked up whole it stayed in the source language at
                // every showing, with every one of its lines translated (2026-09-26).
                var lines = LineParts(text);
                if (lines.Count > 1 && (HasTranslatedLine(lines, isOwnUI) || HasKnownLine(lines, isOwnUI)))
                    return TranslateLines(lines, comp, isOwnUI, skipQueueing);
            }
            return Translate(text, comp, isOwnUI, skipTypewriting: true, skipQueueing: skipQueueing);
        }

        private string TranslateLines(List<string> lines, object comp, bool isOwnUI, bool skipQueueing)
        {
            var sb = new StringBuilder();
            foreach (string line in lines)
            {
                SplitNewlines(line, out string lead, out string core, out string trail);
                // A line without a letter (a score, a count) is the same in every language: kept.
                string done = string.IsNullOrEmpty(core) ? ""
                    : !HasLetter(core) || IsOurs(core, isOwnUI) ? core
                    : KeepBreaks(core, Translate(core, comp, isOwnUI, skipTypewriting: true, skipQueueing: skipQueueing));
                sb.Append(lead).Append(done).Append(trail);
            }
            return sb.ToString();
        }

        /// <summary>Does one of these lines already read as a translation (ours, handed back)?</summary>
        private bool HasTranslatedLine(List<string> lines, bool isOwnUI)
        {
            foreach (string line in lines)
            {
                string core = line.Trim('\r', '\n');
                if (core.Length > 0 && IsOurs(core, isOwnUI)) return true;
            }
            return false;
        }

        /// <summary>Does the file hold one of these lines on its own? A lookup, nothing sent.</summary>
        private bool HasKnownLine(List<string> lines, bool isOwnUI)
        {
            var store = isOwnUI ? _host.OwnUiStore : _host.GameStore;
            foreach (string line in lines)
            {
                string core = line.Trim('\r', '\n');
                if (core.Length == 0) continue;
                var look = TextGate.Lookup(core, isOwnUI, store, _host.NormalizeNumbers, _host.Variables, _host.MatchPattern);
                if (look.Outcome == GateOutcome.Hit) return true;
            }
            return false;
        }

        /// <summary>
        /// A line that is a translation of ours: one whole, or one line of one of several lines —
        /// the game rebuilds a text from lines it read back, one at a time.
        /// </summary>
        /// ⚠ A line without a single letter (numbers, stars, a percentage) reads the same in every
        /// language: it says nothing about whose it is, and taking it for ours cut whole character
        /// sheets into lines sent one by one (107 sends instead of 39 on a recorded session).
        private bool IsOurs(string line, bool isOwnUI)
            => HasLetter(line)
               && (_host.Readback.IsAlreadyTarget(line, NormalizeForCacheLookup(line).TrimEnd(), isOwnUI)
                   || _host.Readback.IsLineOfOurs(line, isOwnUI));

        /// <summary>A letter of any script, markup set aside.</summary>
        private static bool HasLetter(string line)
        {
            bool inTag = false;
            foreach (char c in line)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag && char.IsLetter(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// The text cut at its line breaks, each part keeping the break that ends it — never
        /// inside markup that opens on one line and closes on a later one (the lines it spans
        /// stay one part). Joined back, the parts are the text.
        /// </summary>
        internal static List<string> LineParts(string text)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(text)) return parts;
            int start = 0;
            ScanMarkup(text, breakAt =>
            {
                parts.Add(text.Substring(start, breakAt + 1 - start));
                start = breakAt + 1;
            }, out _, out _);
            if (start < text.Length) parts.Add(text.Substring(start));
            return parts;
        }

        // 🔴 The markup of every engine this router serves, because a line cut inside a styled
        // span breaks the style: Unity/TMP `<color=…>…</color>`, Godot BBCode `[b]…[/b]`, Unreal
        // rich text `<Style>…</>` (closed without its name). Only the tag NAMES are matched as
        // ASCII — engines define them that way, whatever the language of the text between them;
        // the text itself is never read by these patterns.
        private static readonly Regex AngleTag = new Regex(@"<(/?)([A-Za-z][A-Za-z0-9_.-]*)?[^<>]{0,256}>", RegexOptions.Compiled);
        private static readonly Regex BracketTag = new Regex(@"\[(/?)([A-Za-z][A-Za-z0-9_-]*)(?:[= ][^\[\]]{0,256})?\]", RegexOptions.Compiled);

        /// <summary>
        /// Walks the markup of a text: <paramref name="lineBreak"/> is called at each line break
        /// outside any open span. A span counts only when this text closes it (a sprite, a line
        /// break tag or a bracketed label never closed is not a span). <paramref name="depth"/>
        /// is what is still open at the end; <paramref name="stray"/>, closings with no opening.
        /// </summary>
        private static void ScanMarkup(string text, Action<int> lineBreak, out int depth, out int stray)
        {
            // Which openings this text closes, per syntax: named closings, and Unreal's bare `</>`.
            var angleClosed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bracketClosed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool bareClose = false;
            foreach (Match m in AngleTag.Matches(text))
                if (m.Groups[1].Value == "/")
                {
                    if (m.Groups[2].Success) angleClosed.Add(m.Groups[2].Value);
                    else bareClose = true;
                }
            foreach (Match m in BracketTag.Matches(text))
                if (m.Groups[1].Value == "/") bracketClosed.Add(m.Groups[2].Value);

            var stack = new List<string>();   // "<name" or "[name"
            stray = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<' || c == '[')
                {
                    var m = (c == '<' ? AngleTag : BracketTag).Match(text, i);
                    if (m.Success && m.Index == i && (c == '[' || m.Groups[2].Success || m.Groups[1].Value == "/"))
                    {
                        bool closing = m.Groups[1].Value == "/";
                        string name = m.Groups[2].Success ? m.Groups[2].Value : null;
                        string key = c + (name ?? "");
                        if (!closing)
                        {
                            bool spans = c == '<' ? (angleClosed.Contains(name) || bareClose) : bracketClosed.Contains(name);
                            if (spans) stack.Add(key);
                        }
                        else
                        {
                            // `</>` closes the last angle span; a named closing, its own name.
                            int at = name == null
                                ? stack.FindLastIndex(k => k[0] == '<')
                                : stack.FindLastIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                            if (at >= 0) stack.RemoveAt(at); else stray++;
                        }
                        i += m.Length - 1;
                        continue;
                    }
                }
                if (c == '\n' && stack.Count == 0) lineBreak?.Invoke(i);
            }
            depth = stack.Count;
        }

        /// <summary>
        /// A text that grew by whole lines: what was there before is intact, and a block of whole
        /// lines came at its head or at its end, in one write. What was there is <paramref name="kept"/>
        /// (and its translation when known), the block is <paramref name="added"/>.
        ///
        /// ⚠ Not a reveal: a text growing a few characters at a time is a typewriter's, and a
        /// component still being revealed is left to the reveal. Not an assembly in one frame
        /// either: that is the concat branch's, already asked before this.
        /// </summary>
        private bool GrowsByLines(ComponentTextState state, string text,
                                  out string kept, out string keptTranslation, out string added, out bool atHead)
        {
            kept = keptTranslation = added = null;
            atHead = false;
            // ⚠ Even while a reveal holds the text: entries arriving faster than it settles
            // (several events at a change of month) kept the whole log held, then sent it whole.
            // A reveal grows by a few characters (TryGrowth refuses that), never by whole lines.

            // What the component showed before: the game's own text (with its translation when
            // the pair is known), or our translation, read back and added to.
            string pairTranslation = state.ReadBackSource != null && state.ReadBackSource == state.LastRaw
                ? state.ReadBackTranslated : null;
            if (TryGrowth(state.LastRaw, text, out added, out atHead))
            {
                kept = state.LastRaw;
                keptTranslation = pairTranslation;
                return true;
            }
            if (state.LastTranslated != null && TryGrowth(state.LastTranslated, text, out added, out atHead))
            {
                kept = state.LastTranslated;
                keptTranslation = state.LastTranslated;
                return true;
            }
            return false;
        }

        private static bool TryGrowth(string before, string now, out string added, out bool atHead)
        {
            added = null;
            atHead = false;
            if (string.IsNullOrEmpty(before) || now == null || now.Length <= before.Length) return false;
            if (TextRelations.LooksLikeTypewriterGrowth(before, now)) return false;

            if (now.EndsWith(before, StringComparison.Ordinal))
            {
                string head = now.Substring(0, now.Length - before.Length);
                if (head.EndsWith("\n", StringComparison.Ordinal) && HasContent(head) && BreaksAt(now, head.Length - 1))
                {
                    added = head;
                    atHead = true;
                    return true;
                }
            }
            if (now.StartsWith(before, StringComparison.Ordinal))
            {
                string tail = now.Substring(before.Length);
                if ((tail.StartsWith("\n", StringComparison.Ordinal) || tail.StartsWith("\r\n", StringComparison.Ordinal))
                    && HasContent(tail) && BreaksAt(now, before.Length + (tail[0] == '\r' ? 1 : 0)))
                {
                    added = tail;
                    return true;
                }
            }
            return false;
        }

        private static bool HasContent(string s)
        {
            foreach (char c in s) if (!char.IsWhiteSpace(c)) return true;
            return false;
        }

        /// <summary>Every tag this block closes, it opened, and the other way round.</summary>
        /// <summary>
        /// Is the line break at <paramref name="newlineAt"/> outside every styled span of the whole
        /// text? Asked of the WHOLE, not of the block: a span opened in the block may close in
        /// what was there, and the junction would then cut through it.
        /// </summary>
        private static bool BreaksAt(string text, int newlineAt)
        {
            bool found = false;
            ScanMarkup(text, at => { if (at == newlineAt) found = true; }, out _, out _);
            return found;
        }

        /// <summary>
        /// The text of a component that grew by a block of lines: what was there keeps its
        /// translation (or is looked up, never sent again — it already was), the block is
        /// translated as one unit and sent when unknown. Both are this component's parts, so an
        /// answer arriving later is put back into the whole (ReassembleConcat).
        /// </summary>
        private string AssembleGrowth(ComponentTextState state, object comp, long compId, bool isOwnUI,
                                      string source, string kept, string keptTranslation, string added, bool atHead)
        {
            string keptDone = keptTranslation ?? TranslateUnit(kept, comp, isOwnUI, skipQueueing: true);
            SplitNewlines(added, out string lead, out string core, out string trail);
            string addedDone = lead + (string.IsNullOrEmpty(core) ? "" : KeepBreaks(core, TranslateUnit(core, comp, isOwnUI, skipQueueing: false))) + trail;
            string assembled = atHead ? addedDone + keptDone : keptDone + addedDone;

            ForgetTypewriting(state);
            _typewritingPending.Remove(compId);
            state.Deltas = atHead ? new List<string> { added, kept } : new List<string> { kept, added };
            state.LastTranslated = assembled;
            TrackTranslation(compId, source, assembled);
            _concatTranslatedValues.Add(assembled);
            if (_host.DebugMode)
                _host.LogDebug($"[LINES] comp={compId} {(atHead ? "a block at the head" : "a block at the end")} ({added.Length}c) — translated alone, the rest kept");
            return assembled;
        }

        /// <summary>
        /// A text about to be looked up or sent whole — written by the game, or held by a reveal
        /// that has just settled — holding a line that is already a translation: its other lines
        /// are what is left to translate. Assembled line by line, its parts recorded for answers
        /// to come back into; null when this is not such a text (nothing is done then).
        /// </summary>
        /// <param name="skipQueueing">
        /// At a write: nothing is sent — the text may still be revealing, and its unfinished lines
        /// would go out one state after another; the reveal holds it, and what is still unknown
        /// is sent when it has settled (the stabiliser comes back here with this false).
        /// </param>
        private string AssembleLines(long compId, object target, string text, bool isOwnUI, bool skipQueueing)
        {
            // A translation of ours whole is not a text made of pieces: it is left to the lookup,
            // which knows it — cut, a name it kept as it was would have gone out on its own.
            if (_host.Readback.IsAlreadyTarget(text, NormalizeForCacheLookup(text).TrimEnd(), isOwnUI)) return null;
            var lines = LineParts(text);
            if (lines.Count < 2 || !HasTranslatedLine(lines, isOwnUI)) return null;
            var state = StateFor(compId);
            string assembled = TranslateLines(lines, target, isOwnUI, skipQueueing);
            state.Deltas = lines;
            state.LastRaw = text;
            state.LastTranslated = assembled;
            TrackTranslation(compId, text, assembled);
            _concatTranslatedValues.Add(assembled);
            return assembled;
        }
    }
}
