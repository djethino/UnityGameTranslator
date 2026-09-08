using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a text looks like once the decoration is set aside.
    ///
    /// ⚠ **The stake is a cache key and a contract with a model.** Every one of these answers ends
    /// up either as the key a translation is stored under — two spellings of the same sentence are
    /// two entries, and the second is paid for again — or as something a model is asked to give
    /// back untouched. A placeholder that comes back broken is a line the game refuses; a number
    /// pulled out of a hex colour is a colour the game no longer understands.
    ///
    /// ⚠ These answers are written from the rule as stated, not read back from the code. The code
    /// moved out of TranslatorCore on 2026-09-08 and moving it is exactly when a silent change
    /// would slip in.
    /// </summary>
    internal static class TextNormalizationChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            LineEndings(check);
            Numbers(check);
            Markup(check);
            Letters(check);
            ReadbackForm(check);
        }

        private static void LineEndings(Action<bool, string, string> check)
        {
            check(TextNormalization.NormalizeLineEndings("a\r\nb") == "a\nb",
                "a Windows line ending becomes one newline",
                "the same sentence written on two systems has to be one key, not two");

            check(TextNormalization.NormalizeLineEndings("a\rb") == "a\nb",
                "and so does an old Mac one", "same reason, one platform further back");

            // 🔴 The order inside the method is the whole rule: \r first would leave \n\n behind.
            check(TextNormalization.NormalizeLineEndings("a\r\n\r\nb") == "a\n\nb",
                "two Windows breaks become two, never four",
                "replacing \\r before \\r\\n doubles every paragraph break in the file");

            check(TextNormalization.NormalizeLineEndings("") == ""
                  && TextNormalization.NormalizeLineEndings(null) == null,
                "nothing in, nothing out", "a text that is not there is not an error");
        }

        private static void Numbers(Action<bool, string, string> check)
        {
            string one = TextNormalization.ExtractNumbersToPlaceholders("You have 3 apples", out var got);
            check(one == "You have [!v*0] apples" && got.Count == 1 && got[0] == "3",
                "a number leaves a slot behind",
                "the model never sees it, so it cannot change it — and one sentence with any number is one cache entry");

            check(TextNormalization.RestoreNumbersFromPlaceholders(one, got) == "You have 3 apples",
                "and the slot takes it back", "the round trip is the point; either half alone loses the line");

            string decimals = TextNormalization.ExtractNumbersToPlaceholders("3.5 and 3,5 and 50%", out var many);
            check(decimals == "[!v*0] and [!v*1] and [!v*2]" && many.Count == 3
                  && many[0] == "3.5" && many[1] == "3,5" && many[2] == "50%",
                "a decimal point, a decimal comma and a percent stay with their number",
                "splitting one of them makes two slots out of one value, and the sentence comes back wrong");

            // 🔴 The case a naive number pattern gets wrong every time.
            string colour = TextNormalization.ExtractNumbersToPlaceholders("<color=#FF0000>red</color>", out var none);
            check(colour == "<color=#FF0000>red</color>" && none.Count == 0,
                "the digits of a hex colour are not a number",
                "pulled out, the colour comes back as a slot and the game renders nothing");

            string already = TextNormalization.ExtractNumbersToPlaceholders("a [!v*0] b", out var again);
            check(already == "a [!v*0] b" && again.Count == 0,
                "the index of a slot is not a number either",
                "a second pass would nest slots and neither could be put back");

            // ⚠ The cast is not decoration: with two overloads, a bare null names neither, and the
            // compiler says so. Anything calling this with a null it does not type will not build.
            check(TextNormalization.RestoreNumbersFromPlaceholders("nothing here", new List<string>()) == "nothing here"
                  && TextNormalization.RestoreNumbersFromPlaceholders("nothing here", (List<string>)null) == "nothing here",
                "restoring nothing changes nothing", "a text with no slot is already finished");

            var byIndex = new Dictionary<int, string> { { 1, "seven" } };
            check(TextNormalization.RestoreNumbersFromPlaceholders("[!v*0] [!v*1]", byIndex) == "[!v*0] seven",
                "the dictionary form fills the slot it names and leaves the others",
                "it is fed by live values read one at a time, so it is normal for it to hold a gap");
        }

        private static void Markup(Action<bool, string, string> check)
        {
            check(TextNormalization.StripMarkupTags("<b>Play</b>") == "Play",
                "markup comes off for a comparison",
                "a game wrapping the typed value in colour tags must still match what was typed");

            string lifted = TextNormalization.ExtractMarkupTags("<b>Play</b> now", out var tags);
            check(lifted == "[!t*0]Play[!t*1] now" && tags.Count == 2
                  && tags[0] == "<b>" && tags[1] == "</b>",
                "a tag leaves a slot of its own",
                "the model reorders words freely and would carry a tag to the wrong one — the slot is what pins it");

            check(TextNormalization.RestoreMarkupTags(lifted, tags) == "<b>Play</b> now",
                "and the slots take the tags back", "same round trip, same stake");

            check(TextNormalization.RestoreMarkupTags("[!t*0]x", null) == "[!t*0]x",
                "with nothing to put back, the text is left alone",
                "inventing a tag would be worse than leaving the slot visible");

            // ⚠ Two families of slot, two spellings. They travel together and must not collide.
            string both = TextNormalization.ExtractMarkupTags("<b>3</b>", out var t2);
            both = TextNormalization.ExtractNumbersToPlaceholders(both, out var n2);
            check(both == "[!t*0][!v*0][!t*1]" && t2.Count == 2 && n2.Count == 1,
                "a number inside a tag gets its own kind of slot",
                "one spelling for both would make the two restorations fight over the same index");
        }

        private static void Letters(Action<bool, string, string> check)
        {
            check(TextNormalization.IsNumericOrSymbol("123") && TextNormalization.IsNumericOrSymbol(" 3.5 % "),
                "digits and symbols carry no language",
                "asking a model to translate them spends a call to get the same thing back");

            check(!TextNormalization.IsNumericOrSymbol("Play"),
                "a Latin word does", "the ordinary case, and the one everything else is measured against");

            // 🔴 The ranges are spelled out because char.IsLetter has been seen answering wrongly
            // for these on some IL2CPP runtimes — and a wrong "no letters here" is a game left
            // untranslated with nothing said.
            check(!TextNormalization.IsNumericOrSymbol("遊ぶ") && !TextNormalization.IsNumericOrSymbol("한국"),
                "and so do Japanese and Korean", "the games that most need translating are these ones");

            check(!TextNormalization.IsNumericOrSymbol("Привет") && !TextNormalization.IsNumericOrSymbol("مرحبا")
                  && !TextNormalization.IsNumericOrSymbol("नमस्ते") && !TextNormalization.IsNumericOrSymbol("สวัสดี"),
                "Cyrillic, Arabic, Devanagari and Thai too",
                "each is a range written out on purpose, and each was a language the mod would have skipped");

            check(TextNormalization.IsNaturalIdentity("[!v*0] / [!v*1]"),
                "a line of nothing but slots is the same in every language",
                "translated to itself it looks like a failed call; it is not one, and must not be retried");

            check(TextNormalization.IsNaturalIdentity("<b>100%</b>"),
                "and so is one of nothing but markup and digits", "the tags are stripped before the question is asked");

            check(!TextNormalization.IsNaturalIdentity("Level [!v*0]"),
                "one word is enough to make it a sentence",
                "otherwise a real line would be dropped for carrying a number");

            // 🔴 Where the two answers to "is there a letter" actually diverge, frozen so the
            // divergence is a fact rather than an impression. A private-use codepoint is one of
            // OUR shaped glyphs: NormalizeForReadbackMatch counts it as a letter (that is what
            // lets a shaped word be recognised coming back), and this one does not — it is in no
            // range listed and char.IsLetter says no. A word rendered ENTIRELY as ligatures would
            // therefore be refused at every door into translation, before the readback path ever
            // sees it. Undecided on purpose: changing it changes what four guards let through.
            check(TextNormalization.IsNumericOrSymbol(""),
                "a text of nothing but shaped glyphs reads as symbols here",
                "while the readback form counts the same codepoints as letters — the one place the two disagree");

            check(TextNormalization.NormalizeForReadbackMatch("") != null,
                "and the readback form gives it one",
                "a conjunct IS letters, which is what makes a shaped word recognisable when the game hands it back");
        }

        private static void ReadbackForm(Action<bool, string, string> check)
        {
            // What the form is FOR: recognising a text the game has just shown us back, after it
            // has been decorated, renumbered or re-wrapped on the way.
            check(TextNormalization.NormalizeForReadbackMatch("<b>Hello</b>") == "hello",
                "markup drops out of the readback form",
                "the game hands back its own decoration and the text underneath has to be recognisable");

            check(TextNormalization.NormalizeForReadbackMatch("Level 3") == "level #"
                  && TextNormalization.NormalizeForReadbackMatch("Level [!v*0]") == "level #",
                "a number and the slot it came from collapse onto the same token",
                "the game re-injects the value, so the two forms are one sentence — that is the whole reason the form exists");

            check(TextNormalization.NormalizeForReadbackMatch("Hello   world") == "hello world",
                "runs of whitespace become one", "a re-wrapped line is the same line");

            check(TextNormalization.NormalizeForReadbackMatch("{Hello} [world]") == "hello world",
                "braces and brackets are decoration", "games add and remove them around the same words");

            check(TextNormalization.NormalizeForReadbackMatch("3") == null
                  && TextNormalization.NormalizeForReadbackMatch("x2") == null,
                "a short numeric or symbolic text has no readback form",
                "they all collapse onto each other, and matching on that would swap two unrelated lines");

            // 🔴 What IsLetter alone gets wrong, and what it cost: a vowel sign is not a letter to
            // it, so कि and บ้าน counted one letter, fell under the threshold, were never indexed,
            // went to the AI, and their own translation came back in as a Hindi→Hindi key.
            check(TextNormalization.NormalizeForReadbackMatch("कि से") != null,
                "a combining vowel sign counts as part of its word",
                "counted out, every short Indic or Thai text falls below the threshold and is never recognised");

            check(TextNormalization.NormalizeForReadbackMatch("บ้าน") != null,
                "and so does a Thai tone mark", "same rule, same family of loss");
        }
    }
}
