using RTLTMPro;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Stage B1: Arabic contextual shaping through the Unicode presentation-form blocks
    /// (FE70–FEFF, FB50–FDFF), using the vendored RTLTMPro tables — see RTLTMPro/VENDORED.md for
    /// what is borrowed, what is deliberately not, and the license.
    ///
    /// What it does per run, in logical order: pull the tashkeel out, replace every letter by its
    /// positional form (isolated/initial/medial/final, lam-alef ligatures), put the tashkeel
    /// back, collapse shadda combinations. Hebrew needs none of this (no joining) and passes
    /// through unchanged — its work is all in stages C and D.
    ///
    /// ⚠ NOT thread safe, main thread only: the vendored TashkeelFixer/GlyphFixer work on shared
    /// static buffers (pitfall n°5 of the 06/08 analysis). The pipeline's single entry point
    /// enforces this; translation workers must never call it.
    ///
    /// ⚠ Numbers are always preserved as typed (preserveNumbers: true): converting European
    /// digits to Arabic-Indic ones is a per-language presentation choice — the kind of decision
    /// this layer is forbidden to take. If it ever becomes a user option, it belongs to the
    /// caller, not here.
    /// </summary>
    internal sealed class PresentationFormsShaper : ITextShaper
    {
        // Shared with the vendored code's own statics in spirit: one shaper instance, one pair of
        // buffers, main thread only.
        private readonly FastStringBuilder _input = new FastStringBuilder(512);
        private readonly FastStringBuilder _output = new FastStringBuilder(512);

        public string Shape(string run)
        {
            if (string.IsNullOrEmpty(run)) return run;

            // Which yeh spelling this run uses — decided from content, never from a configured
            // language (see RtlText.PrefersFarsiForms and its flagged limitation).
            bool farsi = RtlText.PrefersFarsiForms(run);

            _input.SetValue(run);
            TashkeelFixer.RemoveTashkeel(_input);
            // fixTextTags: false — tag protection is stage C's job (our placeholders and rich
            // text tags are isolated into LTR runs before any shaping happens).
            GlyphFixer.Fix(_input, _output, preserveNumbers: true, farsi: farsi, fixTextTags: false);
            TashkeelFixer.RestoreTashkeel(_output);
            TashkeelFixer.FixShaddaCombinations(_output);

            // HandleSpecialLam marks the swallowed alef with 0xFFFF; RTLTMPro's LigatureFixer
            // dropped it during reordering. Reordering is ours (stage C), so drop it here —
            // shaping alone must already produce a clean string.
            _output.RemoveAll(0xFFFF);

            string shaped = _output.ToString();
            _input.Clear();
            _output.Clear();
            return shaped;
        }

        /// <summary>
        /// The same shaping, and where every character of <paramref name="run"/> ended up — for a
        /// text somebody is EDITING, where the caret addresses the characters typed and the screen
        /// shows the shaped ones (RtlFieldLayout).
        ///
        /// The vendored steps keep one position per character except in two places, and those
        /// are the only merges a map has to follow: a lam-alef ligature takes the lam's slot and
        /// marks the alef's 0xFFFF (dropped at the end), and a shadda followed by a haraka becomes
        /// one combined sign. Both are replayed here on the intermediate buffer rather than
        /// guessed from the result.
        /// </summary>
        /// <param name="map">
        /// For every UTF-16 index of <paramref name="run"/>, the UTF-16 index in the result of the
        /// glyph that shows it; a merged character points at the glyph it merged into. Null when
        /// the intermediate buffer did not keep one slot per character — never seen, but a map
        /// that does not hold would put the caret on the wrong letter, so it is refused instead.
        /// </param>
        public string ShapeWithMap(string run, out int[] map)
        {
            map = null;
            if (string.IsNullOrEmpty(run)) { map = new int[0]; return run; }

            bool farsi = RtlText.PrefersFarsiForms(run);

            // Codepoints of the input, with the UTF-16 index each one starts at.
            var starts = new System.Collections.Generic.List<int>(run.Length);
            for (int i = 0; i < run.Length; i++)
            {
                starts.Add(i);
                if (char.IsHighSurrogate(run[i]) && i + 1 < run.Length && char.IsLowSurrogate(run[i + 1])) i++;
            }

            _input.SetValue(run);
            TashkeelFixer.RemoveTashkeel(_input);
            GlyphFixer.Fix(_input, _output, preserveNumbers: true, farsi: farsi, fixTextTags: false);
            TashkeelFixer.RestoreTashkeel(_output);

            // One slot per input codepoint at this point (the alef swallowed by a ligature is a
            // 0xFFFF placeholder, not a missing slot).
            int count = _output.Length;
            if (count != starts.Count)
            {
                string fallback = Finish();
                return fallback;
            }

            var mid = new int[count];
            for (int i = 0; i < count; i++) mid[i] = _output.Get(i);

            TashkeelFixer.FixShaddaCombinations(_output);
            _output.RemoveAll(0xFFFF);
            string shaped = Finish();

            // Replay FixShaddaCombinations exactly as it runs (left to right, a consumed partner
            // starts nothing), then the removal of the 0xFFFF placeholders: which final codepoint
            // shows each intermediate slot.
            var slotToMerged = new int[count];
            var mergedValues = new System.Collections.Generic.List<int>(count);
            for (int i = 0; i < count;)
            {
                bool pair = mid[i] == (int)TashkeelCharacters.Shadda && i + 1 < count && IsShaddaPartner(mid[i + 1]);
                slotToMerged[i] = mergedValues.Count;
                if (pair) slotToMerged[i + 1] = mergedValues.Count;
                mergedValues.Add(pair ? -1 : mid[i]);
                i += pair ? 2 : 1;
            }

            // The alef of a lam-alef (0xFFFF) is shown by the ligature just before it.
            var mergedToFinal = new int[mergedValues.Count];
            int final = -1;
            for (int m = 0; m < mergedValues.Count; m++)
                mergedToFinal[m] = mergedValues[m] == 0xFFFF ? System.Math.Max(final, 0) : ++final;

            // Final codepoint index → UTF-16 index in the shaped string.
            var outStarts = new System.Collections.Generic.List<int>(shaped.Length);
            for (int i = 0; i < shaped.Length; i++)
            {
                outStarts.Add(i);
                if (char.IsHighSurrogate(shaped[i]) && i + 1 < shaped.Length && char.IsLowSurrogate(shaped[i + 1])) i++;
            }
            if (final + 1 != outStarts.Count) return shaped;   // map refused, see the parameter

            var slotToOut = new int[count];
            for (int i = 0; i < count; i++) slotToOut[i] = mergedToFinal[slotToMerged[i]];

            map = new int[run.Length];
            for (int cp = 0; cp < count; cp++)
            {
                int from = starts[cp];
                int to = cp + 1 < count ? starts[cp + 1] : run.Length;
                for (int u = from; u < to; u++) map[u] = outStarts[slotToOut[cp]];
            }
            return shaped;
        }

        private static bool IsShaddaPartner(int cp) =>
            cp == (int)TashkeelCharacters.Dammatan || cp == (int)TashkeelCharacters.Kasratan
            || cp == (int)TashkeelCharacters.Fatha || cp == (int)TashkeelCharacters.Damma
            || cp == (int)TashkeelCharacters.Kasra || cp == (int)TashkeelCharacters.SuperscriptAlef;

        private string Finish()
        {
            string s = _output.ToString();
            _input.Clear();
            _output.Clear();
            return s;
        }
    }
}
