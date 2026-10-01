using System;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The sampling size "Auto" draws a font at (AtlasSampling). Decided by the user on 2026-10-01
    /// (« si le résultat est un pâté illisible ou tout pixelisé, il vaut mieux monter "auto" et
    /// laisser le choix à l'utilisateur de réduire ») : a font with many glyphs is no longer held
    /// at 48 px/em when an 8192 atlas lets it reach 96, and no font gets LESS than before.
    /// </summary>
    internal static class AtlasSamplingChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            const int gpu = 16384;
            float Auto(int glyphs, int maxTexture = gpu) => AtlasSampling.ChooseRenderSize(glyphs, maxTexture, 0);
            float Chosen(int glyphs, int budget) => AtlasSampling.ChooseRenderSize(glyphs, gpu, budget);

            // Noto Sans Devanagari: 1 121 glyphs once its unmapped ones have codes.
            check(Auto(1121) == 96f, "a font with many glyphs: Auto reaches 96 px/em", Auto(1121).ToString());
            check(Chosen(1121, 4096) == 48f, "the same font with 4096 chosen: 48 px/em (the player's way to spend less)", Chosen(1121, 4096).ToString());
            check(Chosen(1121, 8192) == 96f && Chosen(1121, 16384) > 96f, "a chosen budget gives the largest size it fits",
                  Chosen(1121, 8192) + " / " + Chosen(1121, 16384));

            // No font gets less than the 4096 rule gave; one it gave 96 or more keeps exactly that
            // (its memory stays what it was); one it held under 96 is lifted to 96.
            foreach (int glyphs in new[] { 60, 100, 250, 400, 700, 1121 })
            {
                float before = Chosen(glyphs, 4096), now = Auto(glyphs);
                bool right = before >= 96f ? now == before : now == 96f;
                check(right, $"{glyphs} glyphs: the 4096 rule gave {before}, Auto gives {(before >= 96f ? "the same" : "96")}", now.ToString());
            }

            // A charset no 8192 atlas holds at 64 px/em (CJK): the floor, as before.
            check(Auto(20000) == AtlasSampling.FloorSize, "a huge charset stays at the floor", Auto(20000).ToString());

            // A GPU that refuses 8192: nothing raised beyond what it can hold.
            check(Auto(1121, 4096) == 48f, "a GPU limited to 4096: the large font stays at 48 px/em", Auto(1121, 4096).ToString());
        }
    }
}
