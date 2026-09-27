using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === ADDITIONS AFTER OUR TEXT, SETTLED ===
        //
        // A game that reads our translation back and appends to it, in a frame of its own: a book
        // adding a sentence, a dialogue typing on after the line we translated. Whether it is a
        // part or a reveal going on is not known at the write — only what follows says it — and
        // both end the same way: what was added since our text is what is new. So the append
        // branch (TextRouter.Route) shows what is known at once and holds the rest; when the
        // component stops, what was added since the base goes out ALONE, never the page around
        // it, never one step of it.
        //
        // ⚠ The base is the text the additions grew from (AppendBase*), kept across the writes of
        // one reveal. The unit is the addition, not the line: a sentence added to a paragraph sits
        // on the same line as the sentences already translated, and cut by lines it was either
        // sent with them or not sent at all.

        /// <summary>
        /// This settled text is our text with additions after it: what was added goes out alone —
        /// the whole page first, when the file holds it. True when it was (nothing else is to be
        /// done with the text). When the source of the base is unknown (a line of ours the game
        /// read back and built on), false: the text is left to the finalisation by lines
        /// (TextRouter.Lines), which knows which of its lines are ours.
        /// </summary>
        /// <param name="stillShown">
        /// False when the component is being replaced at this very write: nothing is written to it
        /// then — only the request for what was added goes, and a late answer is put back only if
        /// the page is still there (TextRouter.Late). ⚠ True is not enough either: a component
        /// replaced by a text the lookup answered never told the reveal, so what it HOLDS is read
        /// before anything is written — writing the old page over the new text is the harm.
        /// </param>
        private bool SettleAppend(long compId, string text, bool stillShown)
        {
            var state = PeekState(compId);
            string baseShown = state?.AppendBaseShown;
            bool atHead = state != null && state.AppendAtHead;
            if (baseShown == null || text.Length <= baseShown.Length
                || !(atHead ? text.EndsWith(baseShown, StringComparison.Ordinal)
                            : text.StartsWith(baseShown, StringComparison.Ordinal)))
                return false;

            string baseSource = state.AppendBaseSource;
            string baseTranslated = state.AppendBaseTranslated ?? baseShown;
            state.AppendBaseShown = state.AppendBaseTranslated = state.AppendBaseSource = null;
            state.AppendAtHead = false;
            if (baseSource == null) return false;

            string added = atHead ? text.Substring(0, text.Length - baseShown.Length) : text.Substring(baseShown.Length);
            object target = TargetOf(compId);
            string whole = atHead ? added + baseSource : baseSource + added;

            // The page whole first, as at the write: a file holding it as one entry translates it.
            string page;
            bool missing;
            string wholeTranslated = Translate(whole, target, false, skipTypewriting: true, skipQueueing: true);
            if (wholeTranslated != whole)
            {
                page = wholeTranslated;
                missing = false;
            }
            else
            {
                SplitNewlines(added, out string lead, out string core, out string trail);
                bool addedMissing = false;
                string translated = string.IsNullOrEmpty(core) ? ""
                    : KeepBreaks(core, TranslateUnit(core, target, false, skipQueueing: false, out addedMissing));
                string addedDone = lead + translated + trail;
                page = atHead ? addedDone + baseTranslated : baseTranslated + addedDone;
                missing = addedMissing;

                // Its parts, so an answer arriving late is put back into the page (ReassembleConcat).
                state.Deltas = atHead ? new List<string> { added, baseSource } : new List<string> { baseSource, added };
            }

            _host.LogDebug($"[APPEND-SETTLE] comp={compId} added({added.Length}c)='{Head40(added)}' {(missing ? "sent" : "known")}");

            TrackTranslation(compId, whole, page);
            state.LastTranslated = page;
            state.AssemblyMissing = missing;
            RememberAssembledRaw(text, page, missing);
            _concatTranslatedValues.Add(page);
            RememberAssembledSource(whole, page, missing);

            // Known now, and still on screen: the page goes up translated. A part still waiting is
            // put back when its answer comes.
            if (stillShown && !missing && target != null && page != text && _host.GetText(target) == text)
                _host.Write(target, page);
            return true;
        }
    }
}
