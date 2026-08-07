namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How far a translation has been read by a human, judged from its HVA tags.
    ///
    /// One definition for the whole mod: the local file (StatusCard), the community list
    /// (TranslationList) and anything added later must describe a translation identically, and
    /// identically to the website, which computes the same thing server-side. It lives outside
    /// the UI namespace because a measure is not a widget.
    ///
    /// The 0-3 average this class used to compute is gone. It answered "where does each line
    /// come from" when the question is "has anyone read this": untouched machine output scored a
    /// third of the scale, a file reviewed line by line stopped at two thirds unless its author
    /// retyped what the machine had right, and everything crowded into one band.
    /// </summary>
    public static class TranslationQuality
    {
        /// <summary>
        /// How much of a translation a human has actually read: (H+V) / translated lines.
        /// Negative when nothing is translated yet — a captured file has no coverage, not a
        /// coverage of zero, and the difference is what the reader needs.
        /// </summary>
        public static float ReviewCoverage(int human, int validated, int ai)
        {
            int translated = human + validated + ai;
            if (translated == 0) return -1f;

            return (float)(human + validated) / translated;
        }

        /// <summary>
        /// Where a translation stands, as a STEP rather than a mark. Same four steps and same
        /// thresholds as the website, so a player reads the same thing about a file in the
        /// browser and in the game.
        ///
        /// Replaces the 0-3 score wherever someone has to CHOOSE. That score answers "where does
        /// each line come from" when the question is "has anyone read this": unreviewed machine
        /// output scores 1.0 out of 3, and a file reviewed line by line stops at 2.0 unless its
        /// author retyped what the AI already had right. Everything crowded into the middle.
        ///
        /// Steps carry no verdict either. Every translation starts as raw machine output, since
        /// that is how this mod works — naming that "Raw AI" on a scale ending at "Excellent"
        /// tells a newcomer their starting point is worthless.
        ///
        /// Returns null when there is nothing translated to have reviewed.
        /// </summary>
        public static string ReviewStage(int human, int validated, int ai)
        {
            float coverage = ReviewCoverage(human, validated, ai);
            if (coverage < 0f) return null;

            if (coverage >= 1f) return "Fully reviewed";
            if (coverage >= 0.4f) return "Review well under way";
            if (coverage > 0f) return "Review started";

            return "Machine translation";
        }
    }
}
