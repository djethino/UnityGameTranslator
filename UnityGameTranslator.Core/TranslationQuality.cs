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
        /// How much of what a file has ALREADY MET in game is translated: translated / (translated
        /// + captured). Captured lines are texts the mod ran into and nobody has translated yet —
        /// known, counted, pending work, unlike the rest of the game whose size nobody knows.
        ///
        /// Negative when the file holds nothing at all: an absence of translation rather than a
        /// translation at zero.
        /// </summary>
        public static float Completeness(int human, int validated, int ai, int captured)
        {
            int encountered = human + validated + ai + captured;
            if (encountered == 0) return -1f;

            return (float)(human + validated + ai) / encountered;
        }

        /// <summary>
        /// A file that has MET text in game and translated none of it.
        ///
        /// Not "a translation at zero": no translation has been attempted. The distinction is
        /// what a player needs before downloading — one is work in progress, the other is the
        /// game's own text handed back unchanged. Same rule as the website's
        /// Translation::isCaptureOnly, and the reason it lives here rather than in a panel is
        /// that both the community list and the file's own screens ask the question.
        /// </summary>
        public static bool IsCaptureOnly(int human, int validated, int ai, int captured)
        {
            return human + validated + ai == 0 && captured > 0;
        }

        /// <summary>
        /// Below this, a file is not translated enough for "how well was it read" to mean
        /// anything, and no stage is shown. Same value as the website's TRANSLATION_FLOOR.
        ///
        /// Two lines translated out of thirteen met in game were labelled "Fully reviewed".
        /// Reviewing and translating are two different jobs, and the second has to exist before
        /// the first can be judged.
        /// </summary>
        public const float TranslationFloor = 0.9f;

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
        /// Returns null when there is nothing translated to have reviewed, and when too much of
        /// what the file met in game is still waiting — see TranslationFloor. Pass the captured
        /// count whenever it is known; the overload without it assumes nothing is pending.
        /// </summary>
        public static string ReviewStage(int human, int validated, int ai)
        {
            return ReviewStage(human, validated, ai, 0);
        }

        public static string ReviewStage(int human, int validated, int ai, int captured)
        {
            float completeness = Completeness(human, validated, ai, captured);
            if (completeness >= 0f && completeness < TranslationFloor) return null;

            float coverage = ReviewCoverage(human, validated, ai);
            if (coverage < 0f) return null;

            if (coverage >= 1f) return "Fully reviewed";
            if (coverage >= 0.4f) return "Review well under way";
            if (coverage > 0f) return "Review started";

            return "Machine translation";
        }
    }
}
