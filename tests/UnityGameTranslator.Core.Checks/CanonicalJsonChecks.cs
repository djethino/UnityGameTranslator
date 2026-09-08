using System;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Writing a piece of JSON the same way every time, so the same content fingerprints the same.
    ///
    /// 🔴 **What it decides is whether somebody's work is recognised as theirs.** The fingerprint it
    /// feeds answers "is this still the file I copied", across a change of lineage — which is what
    /// a fork is. Unstable, the answer flips for no reason: somebody who reworked a translation's
    /// fonts and images is told they changed nothing and the one button they came for is greyed
    /// out, or an untouched copy is announced as original work.
    ///
    /// 🔴 **Sorted objects, ordered arrays**, and the asymmetry is the point: a settings section is
    /// rebuilt from dictionaries whose enumeration order nothing promises, while a list of font
    /// rules is applied first-match-wins, so its order IS content.
    /// </summary>
    internal static class CanonicalJsonChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            SameContentSameText(check);
            WhereOrderIsContent(check);
            TheOrdinaryValues(check);
        }

        private static string Of(string json) => CanonicalJson.Of(JToken.Parse(json));

        private static void SameContentSameText(Action<bool, string, string> check)
        {
            // 🔴 The whole reason this exists: two objects with the same content, built in
            // different orders, must not fingerprint differently.
            check(Of(@"{""b"":1,""a"":2}") == Of(@"{""a"":2,""b"":1}"),
                "an object written in two orders comes out once",
                "a section rebuilt from a dictionary comes out in whatever order it feels like, and the fingerprint must not follow");

            check(Of(@"{""b"":1,""a"":2}") == @"{""a"":2,""b"":1}",
                "and it comes out sorted",
                "one order, chosen and stable, so two runs of the same build agree as well as two machines");

            check(Of(@"{""outer"":{""z"":1,""a"":2}}") == @"{""outer"":{""a"":2,""z"":1}}",
                "at every depth",
                "a settings section holds objects inside objects, and an unsorted one at any level moves the whole fingerprint");

            check(Of(@"{""a"":1}") != Of(@"{""a"":2}"),
                "but two different contents do not come out the same",
                "stability is worthless if it also stabilises a real change into invisibility");

            check(Of(@"{""a"":1}") != Of(@"{""b"":1}"),
                "and neither do two different names",
                "renaming a setting IS a change to what the file says");
        }

        private static void WhereOrderIsContent(Action<bool, string, string> check)
        {
            check(Of(@"[1,2,3]") == "[1,2,3]" && Of(@"[3,2,1]") == "[3,2,1]",
                "a list keeps the order it was written in",
                "font rules are applied first-match-wins, so their order is what somebody decided");

            check(Of(@"[1,2,3]") != Of(@"[3,2,1]"),
                "so two orders are two different things",
                "sorting them would call two setups the same and let a reordering pass as no change at all");

            check(Of(@"[{""b"":1,""a"":2}]") == @"[{""a"":2,""b"":1}]",
                "and the objects inside it are still sorted",
                "the list's order is content; the order of an object's fields inside it is not");
        }

        private static void TheOrdinaryValues(Action<bool, string, string> check)
        {
            check(CanonicalJson.Of(null) == "null" && Of("null") == "null",
                "nothing is written as nothing",
                "a section that is absent and one explicitly empty must fingerprint alike, or an emptied setting reads as a change");

            check(Of("{}") == "{}" && Of("[]") == "[]",
                "an empty object and an empty list keep their shape",
                "they are not the same thing: one is a section with no settings, the other a rule list with no rules");

            check(Of(@"{""t"":true,""f"":false}") == @"{""f"":false,""t"":true}",
                "true and false are written out",
                "a boolean is most of what a settings section holds");

            check(Of(@"{""a"":""x"",""b"":""y""}") == @"{""a"":""x"",""b"":""y""}",
                "text is quoted",
                "otherwise a value of 1 and a value of \"1\" fingerprint the same, and they are not the same setting");

            check(Of(@"{""s"":""say \""hi\""""}").Contains("\\\""),
                "and text carrying quotes is escaped",
                "a font name or a comment may hold anything, and an unescaped quote would end the value early and shift everything after it");

            check(Of(@"{""k"":null}") == @"{""k"":null}",
                "a field explicitly empty stays explicitly empty",
                "null in this file means 'asked and left blank', which is not the same as never asked");
        }
    }
}
