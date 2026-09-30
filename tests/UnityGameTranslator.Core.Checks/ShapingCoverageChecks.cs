using System;
using System.Collections.Generic;
using UnityGameTranslator.Core.TextShaping;
using Route = UnityGameTranslator.Core.TextShaping.ShapingRoute.Route;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Where the mod stands on scripts that need shaping, cell by cell (engine × origin of the font),
    /// asked of the SAME decision the mod uses (ShapingRoute.Decide), fed what each case really is.
    /// A cell is either fully shaped (by the mod, or natively by the engine) or listed below as an open
    /// gap WITH its reason and the lot that closes it. Red when a cell degrades without being listed —
    /// a regression — or when a listed gap no longer degrades (close it here). The list is the user's
    /// answer to "is everything covered?" (2026-09-30: "je ne saurais pas le voir à l'œil").
    /// </summary>
    internal static class ShapingCoverageChecks
    {
        private enum Engine { Tmp, TmpOld, UiText, UiToolkit, UiToolkitAtg, TextMesh, Tk2d, Ngui }
        private enum FontOrigin { FontsFolderTtf, FontsFolderCff, System, Game }

        // The open gaps: known, said, planned — never silent (analyse/ecritures-complexes-etat-reel.md).
        private static readonly Dictionary<string, string> OpenGaps = new Dictionary<string, string>
        {
            ["Tmp/System"] = "an installed font is built by Unity (CreateFontAsset), not by our rasterizer — its tables are not read; a proprietary font is not derived",
            ["Tmp/Game"] = "the game's font has no file to read — a fonts/ font must be chosen",
            ["TmpOld/System"] = "as Tmp/System",
            ["TmpOld/Game"] = "as Tmp/Game",
            ["UiText/FontsFolderCff"] = "CFF outlines cannot take composite glyphs — decision pending (merge the outlines, or refuse and say it)",
            ["UiText/System"] = "a proprietary installed font is not derived — a fonts/ font must be chosen",
            ["UiText/Game"] = "the game's font has no file — a fonts/ font must be chosen",
            ["UiToolkit/FontsFolderCff"] = "as UiText/FontsFolderCff",
            ["UiToolkit/System"] = "as UiText/System",
            ["UiToolkit/Game"] = "as UiText/Game",
            ["TextMesh/FontsFolderTtf"] = "the mod does not re-font TextMesh at all (parity gap; the engine draws a derived copy — probe 1)",
            ["TextMesh/FontsFolderCff"] = "as TextMesh/FontsFolderTtf",
            ["TextMesh/System"] = "as TextMesh/FontsFolderTtf",
            ["TextMesh/Game"] = "as TextMesh/FontsFolderTtf",
            ["Tk2d/FontsFolderTtf"] = "tk2d draws a bitmap font of its own: to be studied",
            ["Tk2d/FontsFolderCff"] = "as Tk2d/FontsFolderTtf",
            ["Tk2d/System"] = "as Tk2d/FontsFolderTtf",
            ["Tk2d/Game"] = "as Tk2d/FontsFolderTtf",
            ["Ngui/FontsFolderTtf"] = "NGUI draws its own fonts: to be studied",
            ["Ngui/FontsFolderCff"] = "as Ngui/FontsFolderTtf",
            ["Ngui/System"] = "as Ngui/FontsFolderTtf",
            ["Ngui/Game"] = "as Ngui/FontsFolderTtf",
        };

        public static void Run(Action<bool, string, string> check)
        {
            int full = 0, gaps = 0;
            foreach (Engine engine in Enum.GetValues(typeof(Engine)))
                foreach (FontOrigin origin in Enum.GetValues(typeof(FontOrigin)))
                {
                    var route = Decide(engine, origin);
                    string cell = engine + "/" + origin;
                    bool degraded = route == Route.ReorderOnly;
                    bool listed = OpenGaps.TryGetValue(cell, out string why);
                    if (degraded && listed) { gaps++; continue; }
                    if (!degraded && !listed) { full++; continue; }
                    check(false, $"{cell}: {route}",
                        degraded ? "degraded and not listed as an open gap — a regression, or a gap to write down with its reason"
                                 : "listed as a gap but now " + route + " — remove it from OpenGaps");
                }
            check(true, $"{full} cells fully shaped, {gaps} open gaps, each with its reason", "");
            foreach (var kv in OpenGaps) Console.WriteLine($"          gap  {kv.Key}: {kv.Value}");
        }

        /// <summary>What each case IS, fed to the mod's own decision.</summary>
        private static Route Decide(Engine engine, FontOrigin origin)
        {
            bool isTmp = engine == Engine.Tmp || engine == Engine.TmpOld;
            // Our rasterizer builds a TMP asset from any fonts/ file (TrueType or CFF: CffParser).
            bool ourAsset = isTmp && (origin == FontOrigin.FontsFolderTtf || origin == FontOrigin.FontsFolderCff);
            // Re-fonted with a UnityEngine.Font by the mod: uGUI Text and UI Toolkit only.
            bool legacy = engine == Engine.UiText || engine == Engine.UiToolkit;
            // A derived copy exists for a fonts/ TrueType font only (DerivedFontWriter refuses CFF).
            bool derived = legacy && origin == FontOrigin.FontsFolderTtf;
            return ShapingRoute.Decide(isTmp, ourAsset, legacy, derived, engineShapes: engine == Engine.UiToolkitAtg);
        }
    }
}
