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
    }
}
