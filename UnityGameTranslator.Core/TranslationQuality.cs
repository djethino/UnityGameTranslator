using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How far a translation has been read by a human, judged from its HVA tags.
    ///
    /// ⚠ The rules are UnityGameTranslator.Common.Quality, a port of the website's
    /// App\Models\Translation — the website is the reference, because it is what computes these
    /// for every published file. What stays here is the wording, which is the mod's own: the site
    /// says the same thing in nineteen languages.
    ///
    /// ⚠ The counts used to leave SKIPPED out — lines an author deliberately keeps as they are,
    /// proper nouns and brand names, ordinary in a game. The website counts them as read and as
    /// settled, so it was always the more generous, and one file could read "fully reviewed" in a
    /// browser and "review well under way" in the game it came from. The skipped count was
    /// available here all along; it simply never reached the calculation.
    ///
    /// Negative rather than null on the two rates, because callers here have always tested for it
    /// — a captured file has no coverage, which is not a coverage of zero.
    /// </summary>
    public static class TranslationQuality
    {
        /// <summary>
        /// How much of a translation a human has settled: (H+V+S) / (H+V+S+A).
        /// Negative when nothing is translated yet.
        /// </summary>
        public static float ReviewCoverage(int human, int validated, int skipped, int ai)
        {
            double? coverage = Quality.ReviewCoverage(human, validated, skipped, ai);
            return coverage.HasValue ? (float)coverage.Value : -1f;
        }

        /// <summary>
        /// How much of what a file has ALREADY MET in game is settled: settled / (settled +
        /// captured). Captured lines are texts the mod ran into and nobody has dealt with yet —
        /// known, counted, pending work, unlike the rest of the game whose size nobody knows.
        ///
        /// Negative when the file holds nothing at all.
        /// </summary>
        public static float Completeness(int human, int validated, int skipped, int ai, int captured)
        {
            double? completeness = Quality.Completeness(human, validated, skipped, ai, captured);
            return completeness.HasValue ? (float)completeness.Value : -1f;
        }

        /// <summary>
        /// A file that has MET text in game and settled none of it.
        ///
        /// Not "a translation at zero": no translation has been attempted. The distinction is what
        /// a player needs before downloading — one is work in progress, the other is the game's
        /// own text handed back unchanged.
        /// </summary>
        public static bool IsCaptureOnly(int human, int validated, int skipped, int ai, int captured) =>
            Quality.IsCaptureOnly(human, validated, skipped, ai, captured);

        /// <summary>
        /// Below this, a file is not translated enough for "how well was it read" to mean
        /// anything, and no stage is shown.
        /// </summary>
        public const float TranslationFloor = (float)Quality.TranslationFloor;

        /// <summary>
        /// Where a translation stands, as a STEP rather than a mark, in the mod's own words.
        ///
        /// Steps carry no verdict. Every translation starts as raw machine output, since that is
        /// how this mod works — naming that "Raw AI" on a scale ending at "Excellent" tells a
        /// newcomer their starting point is worthless.
        ///
        /// Null when there is nothing translated to have reviewed, and when too much of what the
        /// file met in game is still waiting — see TranslationFloor.
        /// </summary>
        public static string ReviewStage(int human, int validated, int skipped, int ai, int captured)
        {
            ReviewStage? stage = Quality.Stage(human, validated, skipped, ai, captured);

            // ⚠ The words come from the socle. They used to be written out here AND, identically,
            // in the manager's QualityBar — the rule that produces the stage was already shared, so
            // only the wording was free to drift, and a file called "fully reviewed" in one product
            // and something else in the other is the exact failure the socle exists to prevent.
            return stage.HasValue ? Quality.StageName(stage.Value) : null;
        }
    }
}
