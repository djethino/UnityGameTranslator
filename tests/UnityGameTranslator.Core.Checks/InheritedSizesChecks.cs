using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A copy of a template the mod sized, recognised by what it carries (Engine/InheritedSizes) —
    /// the sequence of a game copying a room whose text the mod had made smaller.
    /// </summary>
    internal static class InheritedSizesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var sizes = new InheritedSizes();
            check(!sizes.Any && !sizes.TryOriginal("fontSize", "Font A", "PASSWORD", 24f, out _),
                "nothing written, nothing recognised", "a fresh session holds no size of ours");

            // The template, in the scene: 30 by the game, 24 by a rule (×0.8).
            sizes.Wrote("fontSize", "Font A", "PASSWORD", 30f, 24f);
            check(sizes.TryOriginal("fontSize", "Font A", "PASSWORD", 24f, out float game) && Math.Abs(game - 30f) < 0.01f,
                "a copy carrying the size the mod wrote on its template is sized from the game's 30",
                $"got {game}: taken as its own, 24 is sized again to 19.2 under the same rule, or stays 24 where none applies");

            check(!sizes.TryOriginal("fontSize", "Font A", "PASSWORD", 30f, out _),
                "a component at the game's own size is not taken for a copy", "30 is not a size the mod wrote");
            check(!sizes.TryOriginal("fontSize", "Font B", "PASSWORD", 24f, out _),
                "another font is another template", "a size of ours on Font A says nothing of Font B");
            check(!sizes.TryOriginal("fontSize", "Font A", "TITLE", 24f, out _),
                "another object name is another template", "a copy keeps its children's names");
            check(!sizes.TryOriginal("autoMax", "Font A", "PASSWORD", 24f, out _),
                "a font size is not an auto-size ceiling", "each kind of size is remembered on its own");

            // A float written back by the engine: 24.0000019 is the 24 the mod wrote.
            check(sizes.TryOriginal("fontSize", "Font A", "PASSWORD", 24.0000019f, out _),
                "the size read back from the engine is matched to a tenth", "float sizes come back with noise");

            // Two game sizes reaching the same written one: no answer rather than a wrong one.
            sizes.Wrote("fontSize", "Font A", "LINE", 30f, 24f);   // ×0.8
            sizes.Wrote("fontSize", "Font A", "LINE", 24f, 24f);   // unscaled: nothing to remember
            check(sizes.TryOriginal("fontSize", "Font A", "LINE", 24f, out _),
                "a size left as the game's adds nothing, and takes nothing away", "only a CHANGED size can be inherited");
            sizes.Wrote("fontSize", "Font A", "LINE", 40f, 24f);   // ×0.6 on another LINE
            check(!sizes.TryOriginal("fontSize", "Font A", "LINE", 24f, out _),
                "two game sizes giving the same written size leave the copy as it is",
                "30 or 40: guessing would resize a text the wrong way");

            // A text on the ROOT of what was copied: Unity appends « (Clone) » to that name only.
            sizes.Wrote("fontSize", "Font A", "Row", 20f, 16f);
            check(sizes.TryOriginal("fontSize", "Font A", "Row(Clone)", 16f, out float rowGame) && Math.Abs(rowGame - 20f) < 0.01f,
                "a text on the root of a copy, renamed « (Clone) » by Unity, is recognised",
                $"got {rowGame}: the root is the one object a copy does not keep the name of");
            check(sizes.TryOriginal("fontSize", "Font A", "Row(Clone)(Clone)", 16f, out _),
                "a copy of a copy too", "Unity appends the suffix once per copy");
            sizes.Wrote("fontSize", "Font A", "Cell(Clone)", 18f, 9f);
            check(sizes.TryOriginal("fontSize", "Font A", "Cell", 9f, out _) && sizes.TryOriginal("fontSize", "Font A", "Cell(Clone)", 9f, out _),
                "a template that is itself a copy matches its copies", "the suffix is taken off both sides");
            check(InheritedSizes.CopyName("Security(Clone)001") == "Security(Clone)001" && InheritedSizes.CopyName("(Clone)") == "",
                "only Unity's own suffix is taken off", "a name the game changed after copying keeps what the game made of it");
            check(!sizes.TryOriginal("fontSize", "Font A", "(Clone)", 16f, out _),
                "a name that is nothing but the suffix names nothing", "an empty name would match every other empty one");

            check(!sizes.TryOriginal("fontSize", null, "PASSWORD", 24f, out _) && !sizes.TryOriginal("fontSize", "Font A", "", 24f, out _),
                "without a font or a name, nothing is recognised", "the three together are what make a copy");
        }
    }
}
