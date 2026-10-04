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

        /// <summary>
        /// One game font's problem — the corner's sentence, said the same under the font's row and
        /// under the texts the inspector found. A font whose fallback the translator chose is no
        /// longer asked to get one: what that fallback lacks is said instead (FallbackLacks).
        /// </summary>
        internal static string ForFont(FontManager.FontProblem problem)
        {
            if (problem.Missing > 0 && !problem.HasFallback)
                return $"Font \"{problem.Font}\" is missing {(problem.MissingLetters && !string.IsNullOrEmpty(Language) ? Language + " characters" : "characters of this translation")}. Set a fallback font for it.";
            if (problem.Unshaped)
                return $"Font \"{problem.Font}\" draws {What} incorrectly. Set a fallback font for it.";
            return FallbackLacks(problem);
        }

        /// <summary>
        /// What a chosen fallback still lacks (user, 2026-10-04). The language's own letters, said as
        /// such; otherwise characters a few lines use — how many lines, and the last one seen, by its
        /// number in the translation file and its text, to find it in the editor. Never the list of
        /// lines: twenty would not fit under a row.
        /// </summary>
        internal static string FallbackLacks(FontManager.FontProblem problem)
        {
            if (problem.Missing == 0) return null;
            if (problem.MissingLetters && !string.IsNullOrEmpty(Language))
                return $"This fallback font is missing {Language} letters.";
            if (problem.MissingLines == 0)
                return "This fallback font is missing characters of this translation.";
            string last = (problem.LastLineIndex.HasValue ? "#" + problem.LastLineIndex.Value + " " : "")
                + "\"" + (problem.LastLineText ?? "") + "\"";
            return problem.MissingLines == 1
                ? $"1 line of the translation uses characters this font does not have: {last}"
                : $"{problem.MissingLines} lines of the translation use characters this font does not have. Last one: {last}";
        }

        /// <summary>Several game fonts at once — the Fonts tab names each, on its own row.</summary>
        internal static string ForFonts(int count) => $"{count} fonts draw {What} incorrectly. Set a fallback font for each.";

        /// <summary>
        /// A fallback without the character TextMesh Pro draws underlines, strikethroughs and
        /// highlights with, for a game text that asks for them (FontManager.DecorationProblems). The
        /// fact, then why it matters here; the box's buttons are the way out (Fonts, Ignore).
        /// </summary>
        internal static string DecorationsFor(FontManager.DecorationProblem problem) =>
            $"Fallback font \"{problem.Fallback}\" cannot draw underline, strikethrough or highlight. The game's text uses them.";

        /// <summary>Several fallbacks at once — the Fonts tab says it on each row.</summary>
        internal static string DecorationsForFonts(int count) =>
            $"{count} fallback fonts cannot draw underline, strikethrough or highlight. The game's text uses them.";

        /// <summary>The same fact on the font's own row of the Fonts tab, where the fallback is chosen.</summary>
        internal static string DecorationsOnRow() =>
            "This fallback cannot draw underline, strikethrough or highlight. The game's text uses them.";

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
