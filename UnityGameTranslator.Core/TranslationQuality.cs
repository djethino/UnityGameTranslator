namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How good a translation is, judged from its HVAS tags.
    ///
    /// One definition for the whole mod: the local file (StatusCard), the community list
    /// (TranslationList) and anything added later must rank a translation identically, and
    /// identically to the website — which computes the same formula server-side. It lives
    /// outside the UI namespace because a score is not a widget.
    /// </summary>
    public static class TranslationQuality
    {
        /// <summary>Maximum score, reached when every translated line is human-written.</summary>
        public const float MaxScore = 3f;

        /// <summary>
        /// Score on a 0-3 scale: H=3, V=2, A=1, divided by the number of TRANSLATED lines.
        ///
        /// Captures (H with no value), S (marked as not to translate) and M (mod UI) are all
        /// outside the formula. S is a deliberate omission worth noting: a score that went up by
        /// marking lines as untranslatable would be trivial to inflate, so the care it represents
        /// is reported on its own instead (QualityBar.SkippedLabel).
        /// </summary>
        public static float ComputeScore(int human, int validated, int ai)
        {
            int translated = human + validated + ai;
            if (translated == 0) return 0f;

            float weighted = (human * 3) + (validated * 2) + (ai * 1);
            return weighted / translated;
        }

        /// <summary>
        /// The word for a 0-3 score. Same thresholds and same words as the website documentation,
        /// so a player reading "Good" in the browser reads "Good" in the game.
        /// </summary>
        public static string LabelFor(float score)
        {
            if (score >= 2.5f) return "Excellent";
            if (score >= 2.0f) return "Good";
            if (score >= 1.5f) return "Fair";
            if (score >= 1.0f) return "Basic";
            return "Raw AI";
        }
    }
}
