using System;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Kerning pairs read against their font (KerningUnits). The values are the ones measured on
    /// Unity 6000.3.6's TMP with LiberationSans (2048 units per em, sampled at 86): zayin–he kerned
    /// by −41 placement and −41 advance, kept as −41 and −1.72168; A–V by −152 advance, kept as
    /// −6.382813 (bench tmpdeco-4/-5, 2026-10-03).
    /// </summary>
    internal static class KerningUnitsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            const float scale = 86f / 2048f;
            // first: xPlacement, yPlacement, xAdvance, yAdvance; then the second glyph's.
            float[] zayinHeFont = { -41, 0, -41, 0, 0, 0, 0, 0 };
            float[] zayinHeKept = { -41, 0, -41 * scale, 0, 0, 0, 0, 0 };
            float[] avFont = { 0, 0, -152, 0, 0, 0, 0, 0 };
            float[] avKept = { 0, 0, -152 * scale, 0, 0, 0, 0, 0 };

            check(KerningUnits.ConvertedAdvance(scale, zayinHeFont, zayinHeKept), "the asset converted the first advance", "the evidence it converts on storing");
            check(!KerningUnits.ConvertedAdvance(scale, new float[] { -41, 0, 0, 0, 0, 0, 0, 0 }, new float[] { -41, 0, 0, 0, 0, 0, 0, 0 }),
                "no advance, no evidence", "a placement-only pair says nothing of the asset");
            check(!KerningUnits.ConvertedAdvance(1f, avFont, avFont), "at scale one nothing can be read", "");

            var fixedPair = KerningUnits.Corrected(scale, zayinHeFont, zayinHeKept, true);
            check(fixedPair != null && fixedPair[0] == -41 * scale && fixedPair[2] == -41 * scale,
                "a placement left in font units is converted, the advance kept", fixedPair == null ? "null" : fixedPair[0] + " / " + fixedPair[2]);

            check(KerningUnits.Corrected(scale, avFont, avKept, true) == null, "a pair kerned by advance alone is right as it is", "most Latin pairs");

            float[] allConverted = { -41 * scale, 0, -41 * scale, 0, 0, 0, 0, 0 };
            check(KerningUnits.Corrected(scale, zayinHeFont, allConverted, true) == null,
                "an engine that converts everything is left alone", "nothing equal to the font's is left");

            float[] secondFont = { 0, 0, -20, 0, 12, 0, 30, 0 };
            float[] secondKept = { 0, 0, -20 * scale, 0, 12, 0, 30, 0 };
            var second = KerningUnits.Corrected(scale, secondFont, secondKept, true);
            check(second != null && second[4] == 12 * scale && second[6] == 30 * scale, "the second glyph's values are converted too", "");

            check(KerningUnits.Corrected(scale, zayinHeFont, zayinHeKept, false) == null, "an asset not known to convert is not touched", "no evidence, no change");
            check(KerningUnits.Corrected(1f, zayinHeFont, zayinHeFont, true) == null, "at scale one there is nothing to convert", "");
        }
    }
}
