namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// How a text in a script that needs shaping (Devanagari, Khmer, Thai…) reaches the screen, decided
    /// in ONE place from what the component is and what font it draws with. RtlPresenter.PresentSyllabic
    /// asks it; ShapingCoverageChecks walks every case of it, so the list of what is NOT fully shaped is
    /// the code's own answer, never a document that drifts from it (analyse/ecritures-complexes-etat-reel.md,
    /// lot 6). PURE by contract — linked into Core.Checks.
    /// </summary>
    internal static class ShapingRoute
    {
        internal enum Route
        {
            /// <summary>The font's OpenType tables, through a TMP font asset of ours (FontShaping).</summary>
            OurTmpAsset,
            /// <summary>The font's OpenType tables, through its derived copy — a fonts/ or an installed font (DerivedFonts).</summary>
            DerivedFont,
            /// <summary>The engine shapes by itself (UI Toolkit's Advanced Text Generator): the logical text goes as is.</summary>
            Native,
            /// <summary>Only the codepoint moves a font we cannot read allows (IndicReorderer): conjuncts stay apart.</summary>
            ReorderOnly,
        }

        /// <param name="isTmp">a TextMesh Pro component (modern or TMProOld)</param>
        /// <param name="hasOurTmpAsset">its font is replaced by a TMP asset our rasterizer built from a font file (fonts/, or an installed font that needs shaping)</param>
        /// <param name="drawsLegacyFont">uGUI Text, or UI Toolkit on its standard generator — re-fonted with a UnityEngine.Font</param>
        /// <param name="hasDerivedFont">that Font comes from a font with a derived copy shown to the engine (fonts/ or installed)</param>
        /// <param name="engineShapes">UI Toolkit rendering through its Advanced Text Generator</param>
        internal static Route Decide(bool isTmp, bool hasOurTmpAsset, bool drawsLegacyFont, bool hasDerivedFont, bool engineShapes)
        {
            if (engineShapes) return Route.Native;
            if (isTmp) return hasOurTmpAsset ? Route.OurTmpAsset : Route.ReorderOnly;
            if (drawsLegacyFont && hasDerivedFont) return Route.DerivedFont;
            return Route.ReorderOnly;
        }
    }
}
