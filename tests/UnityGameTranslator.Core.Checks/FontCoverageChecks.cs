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
            // The language is named by a notice only for its own letters (2026-10-03: "missing Arabic
            // characters" said for a bullet and two Japanese letters of a bad line).
            check(FontCoverage.AnyWrittenIn(new[] { 0x25CF, 0xFE8D }, "Arab"),
                "an Arabic letter in its contextual form is an Arabic character", "U+FE8D is ا as drawn alone");
            check(!FontCoverage.AnyWrittenIn(new[] { 0x25CF, 0x30AE, 0x30EA }, "Arab"),
                "a bullet and katakana are not", "the notice then says 'characters of this translation'");
            check(!FontCoverage.AnyWrittenIn(new[] { 0x05D0 }, "Arab") && FontCoverage.AnyWrittenIn(new[] { 0x05D0 }, "Hebr"),
                "a Hebrew letter is Hebrew, not Arabic", "by Unicode's script of the character, not a range");

            // The three decorations TMP draws with the "_" of the component's own font (user, 2026-10-03):
            // the message is due only when the game's text carries one — the source, what the developer
            // wanted shown (2026-10-04); FontManager.NoteTextDrawn passes it.
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

            // Per line (2026-10-04): a line corrected in the editor takes back what it alone brought,
            // without a restart; a character another line still brings stays needed.
            var l = new FontCoverage();
            Func<int, bool?> latinOnly = cp => cp < 0x250;
            l.Record("Dlg", "الギリ", "Greek");
            l.Record("Dlg", "يونانيギ", "Greek letters");
            check(string.Join(",", l.Missing("Dlg", latinOnly)).Contains(((int)'リ').ToString()), "a bad line's characters are missing", "");
            int v = l.Version;
            check(l.Record("Dlg", "اليونانية", "Greek") && l.Version > v, "a corrected line changes the account", "its リ went with it");
            var after = l.Missing("Dlg", cp => cp < 0x250 || (cp >= 0x600 && cp <= 0x6FF));
            check(after.Count == 1 && after[0] == 'ギ', "what the corrected line alone brought is no longer missing; what another line brings is",
                string.Join(",", after));
            var bringing = l.LinesBringing("Dlg", new HashSet<int>(after));
            check(bringing.Count == 1 && bringing[0].Source == "Greek letters" && bringing[0].Text == "يونانيギ",
                "the lines bringing a missing character are named by source and text", bringing.Count.ToString());
            l.Record("Dlg", "الギリ", "Greek");
            check(l.Missing("Dlg", latinOnly).Contains('リ'), "the old text coming back is read again", "the correction undone");
            var latest = l.LinesBringing("Dlg", new HashSet<int> { 'ギ' });
            check(latest.Count == 2 && latest[0].Source == "Greek", "the latest line drawn comes first", latest.Count > 0 ? latest[0].Source : "");
            l.Record("Dlg", "يوناني", "Greek letters");
            l.Record("Dlg", "يونانية", "Greek");
            check(l.Missing("Dlg", latinOnly).TrueForAll(cp => cp >= 0x600 && cp <= 0x6FF) && l.LinesBringing("Dlg", new HashSet<int> { 'ギ' }).Count == 0,
                "every line corrected: no foreign character left", "");

            // The row shows the line around its broken characters, on a budget the broken ones come
            // first in (user, 2026-10-04).
            var bad = new HashSet<int> { 'X', 'Y' };
            string longLine = new string('a', 30) + " bbbb XY cccc " + new string('d', 30);
            string around = FontCoverage.Excerpt(longLine, bad, 20);
            check(around.StartsWith("...") && around.EndsWith("...") && around.Contains("XY") && around.Length <= 26,
                "a long line is cut around its broken characters", around);
            check(FontCoverage.Excerpt("short XY line", bad, 20) == "short XY line", "a short line is shown whole", "");
            string allBad = FontCoverage.Excerpt("ok " + new string('X', 30) + " ok", bad, 10);
            check(allBad == "..." + new string('X', 10) + "...", "broken characters past the budget leave no room for others", allBad);
            check(FontCoverage.Excerpt(new string('a', 30) + "XY", bad, 10) == "...aaaaaaaaXY", "the room unused on one side goes to the other",
                FontCoverage.Excerpt(new string('a', 30) + "XY", bad, 10));
            string markedBefore = FontCoverage.Excerpt("aaaaبّXYaaaa", bad, 4);   // the cut falls on the shadda
            string markedAfter = FontCoverage.Excerpt("aaaaXYبّaaaa", bad, 3);
            check(markedBefore == "...بّXYa..." && markedAfter == "...XYبّ...", "never cut between a letter and its mark",
                markedBefore + " | " + markedAfter);
            check(FontCoverage.Excerpt("a\U0001F600" + new string('b', 20), new HashSet<int> { 0x1F600 }, 4).Contains("\U0001F600"),
                "a character above U+FFFF is kept whole", "");

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
