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
        // An installed font is a .ttf/.otf or one face of a collection (.ttc) — the same two kinds once taken out.
        private enum FontOrigin { FontsFolderTtf, FontsFolderCff, SystemTtf, SystemCff, Game }

        // The open gaps: known, said, planned — never silent (analyse/ecritures-complexes-etat-reel.md).
        private static readonly Dictionary<string, string> OpenGaps = new Dictionary<string, string>
        {
            ["Tmp/Game"] = "the game's font has no file to read — said on the Fonts tab: a fonts/ or installed font must be chosen",
            ["TmpOld/Game"] = "as Tmp/Game",
            ["UiText/Game"] = "the game's font has no file — said on the Fonts tab: a fonts/ or installed font must be chosen",
            ["UiToolkit/Game"] = "as UiText/Game",
            ["TextMesh/Game"] = "as UiText/Game",
            ["Tk2d/FontsFolderTtf"] = "the mod replaces no tk2d font (tk2dFontData, bitmap): studied, not written — analyse/ecritures-complexes-etat-reel.md",
            ["Tk2d/FontsFolderCff"] = "as Tk2d/FontsFolderTtf",
            ["Tk2d/SystemTtf"] = "as Tk2d/FontsFolderTtf",
            ["Tk2d/SystemCff"] = "as Tk2d/FontsFolderTtf",
            ["Tk2d/Game"] = "as Tk2d/FontsFolderTtf",
            ["Ngui/FontsFolderTtf"] = "the mod replaces no NGUI font (UILabel.trueTypeFont or UIFont): studied, not written — same analysis",
            ["Ngui/FontsFolderCff"] = "as Ngui/FontsFolderTtf",
            ["Ngui/SystemTtf"] = "as Ngui/FontsFolderTtf",
            ["Ngui/SystemCff"] = "as Ngui/FontsFolderTtf",
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
            bool hasFile = origin != FontOrigin.Game;
            // Our rasterizer builds a TMP asset from any font file (TrueType or CFF: CffParser) — an
            // installed one too when it needs shaping (FontManager.CreateFallbackFromSystem).
            bool ourAsset = isTmp && hasFile;
            // Re-fonted with a UnityEngine.Font by the mod: uGUI Text, TextMesh (its renderer's material
            // following the font) and UI Toolkit.
            bool legacy = engine == Engine.UiText || engine == Engine.UiToolkit || engine == Engine.TextMesh;
            // A derived copy exists for any font file, fonts/ or installed — CFF outlines are merged into
            // TrueType ones first (DerivedFontWriter.WithTrueTypeOutlines).
            bool derived = legacy && hasFile;
            return ShapingRoute.Decide(isTmp, ourAsset, legacy, derived, engineShapes: engine == Engine.UiToolkitAtg);
        }
    }
}
