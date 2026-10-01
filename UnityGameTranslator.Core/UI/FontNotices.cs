namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The sentences that say a font draws the translation wrongly, and what to do about it — one
    /// place, because the same fact is read in the corner (StatusOverlay) and under the texts the
    /// inspector found (InspectorPanel): an ecosystem says one fact the same way everywhere.
    /// What the player sees on screen, then the way out in the word of the screen that does it
    /// ("Fallback:" in the Fonts tab, the interface font in Options) — user, 2026-10-01.
    /// A dynamic legacy font borrows the letters it lacks from system fonts and Unity counts them
    /// as its own (Font.HasCharacter True for a letter its file does not hold), so "borrowed" cannot
    /// be proved; "draws incorrectly" is true whether the letters are its own or not. Missing
    /// characters are boxes or nothing: said as such.
    /// </summary>
    internal static class FontNotices
    {
        private static string Language => TranslatorCore.EffectiveTargetLanguage;

        private static string What => string.IsNullOrEmpty(Language) ? "this translation" : Language;

        /// <summary>One game font's problem.</summary>
        internal static string ForFont(FontManager.FontProblem problem) => problem.Missing > 0
            ? $"Font \"{problem.Font}\" is missing {(string.IsNullOrEmpty(Language) ? "characters of this translation" : Language + " characters")}. Set a fallback font for it."
            : $"Font \"{problem.Font}\" draws {What} incorrectly. Set a fallback font for it.";

        /// <summary>Several game fonts at once — the Fonts tab names each, on its own row.</summary>
        internal static string ForFonts(int count) => $"{count} fonts draw {What} incorrectly. Set a fallback font for each.";

        /// <summary>
        /// A part of the mod's own window that cannot shape what it shows (FontManager.WindowCannotShape):
        /// its translated labels (the interface font), the game's source text or its translation (their
        /// own fonts — user, 2026-10-01). The source side is never named by its language: it is not
        /// always known ("auto"); the target always is.
        /// </summary>
        internal static string ForWindow(GameTextSide? side)
        {
            switch (side)
            {
                case GameTextSide.Source: return "This window shows the source text incorrectly. Set a source text font in Options.";
                case GameTextSide.Target: return $"This window shows {What} incorrectly. Set a target text font in Options.";
                case GameTextSide.ObjectNames: return "This window shows object names incorrectly. Set an object name font in Options.";
                default: return $"This window's labels draw {What} incorrectly. Set an interface font in Options.";
            }
        }

        /// <summary>One line per part of the window that cannot shape what it shows; empty when none.</summary>
        internal static string ForWindow(System.Collections.Generic.List<GameTextSide?> parts)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var part in parts) lines.Add(ForWindow(part));
            return string.Join("\n", lines);
        }
    }
}
