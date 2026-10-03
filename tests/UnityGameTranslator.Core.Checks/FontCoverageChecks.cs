using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The account behind "this font cannot display the translation" (FontCoverage): the characters
    /// counted are the visible ones each font drew; missing means a font SAID it lacks one — a
    /// source that cannot tell is never counted (a message must be provable).
    /// </summary>
    internal static class FontCoverageChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // The three decorations TMP draws with the "_" of the component's own font (user, 2026-10-03):
            // the message is due only when the translation itself carries one.
            foreach (var text in new[] { "a <u>b</u>", "<s>x</s>", "<mark=#FFD00080>x</mark>", "<U>x</U>", "<u color=#f00>x</u>" })
                check(FontCoverage.DecoratesText(text), "decorates: " + text, "TMP draws it with the font's \"_\"");
            foreach (var text in new[] { "<sprite=1>", "<size=150%>x</size>", "<sub>2</sub>", "<b>x</b>", "a < b", "<style=u>", "plain", "<", "<u" })
                check(!FontCoverage.DecoratesText(text), "does not decorate: " + text, "another tag, or no tag, needs no \"_\"");
            var d = new FontCoverage();
            check(d.NoteDecorated("Title", "<u>x</u>") && !d.NoteDecorated("Title", "<s>y</s>") && d.IsDecorated("Title") && !d.IsDecorated("Body"),
                "a font is marked once, by the first decorated text drawn with it", "per font, like the characters");
            check(!d.NoteDecorated("Body", "no tag here") && !d.IsDecorated("Body"), "a text without decoration marks nothing", "");

            var c = new FontCoverage();
            check(c.Record("Title", "Hé 1!") && !c.Record("Title", "Hé 1!"), "a text is read once per font",
                "a line drawn every frame costs one lookup");
            check(c.Seen("Title", "Hé 1!") && !c.Seen("Body", "Hé 1!"), "the account is per font", "a score font is asked about digits only");

            c.Record("Body", "a\u200Db \uE001\u00A0\tc\uFE0F");   // joiner, space, private glyph, no-break space, tab, variation selector
            var body = c.Missing("Body", cp => false);
            check(string.Join(",", body) == "97,98,99", "spaces, controls, joiners, variation selectors and our private glyphs are not asked of a font",
                string.Join(",", body));

            c.Record("Emoji", "x\U0001F600");
            var emoji = c.Missing("Emoji", cp => cp < 0x10000);
            check(emoji.Count == 1 && emoji[0] == 0x1F600, "a character above U+FFFF is one character, not two halves", string.Join(",", emoji));

            var unknown = c.Missing("Title", cp => cp == 'H' ? true : (bool?)null);
            check(unknown.Count == 0, "a source that cannot tell never makes a character missing", string.Join(",", unknown));
            var some = c.Missing("Title", cp => cp == 'é' ? false : true);
            check(some.Count == 1 && some[0] == 'é', "a character a font says it lacks is missing", string.Join(",", some));

            // Only what a translation brings: a language name the game wrote in its own script, a
            // name kept in Latin, are the source's letters, not the translation's.
            c.Record("Menu", "اللغة: 한국어 Zed", "Language: 한국어 Zed");
            var menu = c.Missing("Menu", cp => cp >= 0x600 && cp <= 0x6FF);   // a font with Arabic and nothing else
            check(menu.Count == 0, "the letters of the line's own source are not asked of the font",
                string.Join(",", menu));
            var brought = c.Missing("Menu", cp => cp > 0x6FF || cp == ' ' || cp == ':');
            check(brought.Count == 4 && brought.Contains(0x627) && brought.Contains(0x644),
                "the letters the translation brings are", string.Join(",", brought));
            c.Record("Menu2", "a\U0001F600", "\U0001F600");
            var pair = c.Missing("Menu2", cp => false);
            check(pair.Count == 1 && pair[0] == 'a', "a source character above U+FFFF is found whole", string.Join(",", pair));
            c.Record("Menu3", "\U0001F601", "\uD83D");
            check(c.Missing("Menu3", cp => false).Count == 1, "half a pair in the source is not the character", "");

            int before = c.Version;
            c.Record("Title", "H");
            check(c.Version == before, "a text with nothing new asks nothing again", "");
            c.Record("Title", "Ж");
            check(c.Version > before, "a new character asks again", "");
            before = c.Version;
            check(c.NoteUnshaped("Dialog") && !c.NoteUnshaped("Dialog") && c.Version == before + 1 && c.IsUnshaped("Dialog"),
                "a font that drew a text without its shaping is noted once", "every character may be there, and the text still wrong");
            check(new List<string>(c.Fonts).Contains("Dialog"), "a font that only failed to shape is among the fonts asked about", "");
            c.ForgetUnshaped();
            check(!c.IsUnshaped("Dialog"), "another font setup learns it again", "a replacement may shape what the last could not");
            c.NoteUnshaped("Dialog");
            c.Clear();
            check(!c.IsUnshaped("Dialog"), "another translation forgets it too", "");
            check(c.Missing("Title", cp => false).Count == 0 && !c.Seen("Title", "Ж"), "another translation starts from nothing", "");
        }
    }
}
