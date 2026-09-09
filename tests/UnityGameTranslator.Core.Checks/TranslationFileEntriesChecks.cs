using System;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a translations.json yields when it is read, and what reading it says about the file.
    ///
    /// 🔴 **Every branch here can lose a translation without a word.** A key normalised differently
    /// on two machines becomes two keys and the game shows the untranslated one. A collision
    /// resolved the wrong way replaces somebody's own wording with a model's. An interface line
    /// left in the game's file is counted, hashed, merged and uploaded to everyone as part of the
    /// game.
    ///
    /// 🔴 **The costliest is the collision rule, because it is invisible.** Two keys differing only
    /// in their line endings become one, and only the higher tag survives — a person over a review
    /// over a model. Backwards, a line somebody typed is silently replaced by one nobody read, in a
    /// file that is then shared with everybody.
    ///
    /// ⚠ "The file needs rewriting" is checked as carefully as the entries. Reading can change what
    /// a file should contain, and if nobody writes it back the same repair happens at every launch
    /// while the file keeps travelling as it was.
    /// </summary>
    internal static class TranslationFileEntriesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            WhatALineBecomes(check);
            WhenTwoKeysCollide(check);
            WhereAnInterfaceLineGoes(check);
            TheCaptureOrder(check);
            GivingAnIndexToWhatHasNone(check);
            WhenTheFileMustBeWritten(check);
            WritingThemBackOut(check);
        }

        private static TranslationFileEntries Read(string json)
        {
            return TranslationFileEntries.ReadAll(JObject.Parse(json));
        }

        private static void WhatALineBecomes(Action<bool, string, string> check)
        {
            var read = Read(@"{""Play"":{""v"":""Jouer"",""t"":""H"",""i"":7}}");
            check(read.Entries.Count == 1 && read.Entries["Play"].Value == "Jouer"
                  && read.Entries["Play"].Tag == "H" && read.Entries["Play"].Index == 7,
                "a line is its value, who wrote it, and where it was captured",
                "the tag decides what a collision keeps and what the quality score counts; losing it loses both");

            var legacy = Read(@"{""Play"":""Jouer""}");
            check(legacy.Entries["Play"].Value == "Jouer" && legacy.Entries["Play"].Tag == "A"
                  && legacy.Entries["Play"].Index == null,
                "a bare string is a line a model wrote",
                "that is what the format meant before tags existed, and calling it human would let it win collisions it should lose");

            var noTag = Read(@"{""Play"":{""v"":""Jouer""}}");
            check(noTag.Entries["Play"].Tag == "A",
                "and a line with no tag is one too",
                "the cautious reading: unclaimed work is not somebody's work");

            var empty = Read(@"{""Play"":{""t"":""H""}}");
            check(empty.Entries["Play"].Value == "",
                "a line with no value is empty, not absent",
                "the key was captured; an empty translation is a fact about it, and dropping the key would re-capture it for ever");

            check(Read(@"{""Play"":null,""Quit"":[1,2]}").Entries.Count == 0,
                "and something that is neither is not a line",
                "a hand-edited or truncated file must not turn an array into a translation");

            check(Read(@"{""_uuid"":""abc"",""_local_changes"":3,""Play"":""Jouer""}").Entries.Count == 1,
                "metadata is not lines",
                "an underscore key belongs to the file, not to the game — counting it would inflate every measure taken of the translation");

            check(TranslationFileEntries.ReadAll(null).Entries.Count == 0,
                "and nothing read is nothing yielded",
                "a file that would not parse must produce an empty translation, never a half-read one");
        }

        private static void WhenTwoKeysCollide(Action<bool, string, string> check)
        {
            // 🔴 Two keys that differ only in their line endings are ONE key. Which line survives
            // is the rule, and it is invisible from anywhere else.
            var humanFirst = Read("{\"Line\\r\\nBreak\":{\"v\":\"Par\",\"t\":\"H\"},"
                                  + "\"Line\\nBreak\":{\"v\":\"Auto\",\"t\":\"A\"}}");
            check(humanFirst.Entries.Count == 1 && humanFirst.Entries["Line\nBreak"].Value == "Par",
                "a model's line does not replace a person's",
                "backwards, somebody's own wording is quietly replaced by one nobody read, in a file that is then shared");

            var humanSecond = Read("{\"Line\\nBreak\":{\"v\":\"Auto\",\"t\":\"A\"},"
                                   + "\"Line\\r\\nBreak\":{\"v\":\"Par\",\"t\":\"H\"}}");
            check(humanSecond.Entries["Line\nBreak"].Value == "Par",
                "whichever order they are written in",
                "the file's order is an accident of how it was edited, and it must not decide who wins");

            var reviewed = Read("{\"Line\\nBreak\":{\"v\":\"Auto\",\"t\":\"A\"},"
                                + "\"Line\\r\\nBreak\":{\"v\":\"Relu\",\"t\":\"V\"}}");
            check(reviewed.Entries["Line\nBreak"].Value == "Relu",
                "a reviewed line beats a model's too",
                "H over V over A: somebody looked at it, which is more than nobody having looked");

            var twoHumans = Read("{\"Line\\r\\nBreak\":{\"v\":\"First\",\"t\":\"H\"},"
                                 + "\"Line\\nBreak\":{\"v\":\"Second\",\"t\":\"H\"}}");
            check(twoHumans.Entries["Line\nBreak"].Value == "First",
                "and equals do not displace each other",
                "with nothing to choose between them, the one already read stays — arbitrary either way, but stable");

            check(Read(@"{""Play"":{""v"":""A"",""t"":""H""},""Quit"":{""v"":""B"",""t"":""A""}}")
                      .Entries.Count == 2,
                "two different keys are two lines",
                "the collision rule must apply to a collision and to nothing else");

            // 🔴 **The two tags a second ladder used to get backwards** (found auditing for
            // duplicated rules, 2026-09-09). This file held its own H > V > anything, which
            // disagreed with the socle's in both directions — and the socle's is the one every
            // merge, every screen and the website already read.
            var emptyCapture = Read("{\"Line\\nBreak\":{\"v\":\"Traduit\",\"t\":\"A\"},"
                                    + "\"Line\\r\\nBreak\":{\"v\":\"\",\"t\":\"H\"}}");
            check(emptyCapture.Entries["Line\nBreak"].Value == "Traduit",
                "🔴 a capture with nothing in it does not displace a translation",
                "H at the top of a ladder that never looked at the value: an empty entry won, and the translation was gone");

            var refusal = Read("{\"Line\\nBreak\":{\"v\":\"Auto\",\"t\":\"A\"},"
                               + "\"Line\\r\\nBreak\":{\"v\":\"Line\\r\\nBreak\",\"t\":\"S\"}}");
            check(refusal.Entries["Line\nBreak"].Tag == "S",
                "🔴 and a model's line does not replace a refusal",
                "somebody ruled that line must be left alone; ranked with the machine's own work, the machine won");
        }

        private static void WhereAnInterfaceLineGoes(Action<bool, string, string> check)
        {
            var read = Read(@"{""Play"":{""v"":""Jouer"",""t"":""A""},""Options"":{""v"":""Options"",""t"":""M""}}");

            check(read.Entries.Count == 1 && read.Entries.ContainsKey("Play"),
                "an interface line is not one of the game's",
                "left in, it is counted, hashed, merged and uploaded to everybody as part of the game");

            check(read.StrandedModUi != null && read.StrandedModUi.Count == 1
                  && read.StrandedModUi["Options"].Value == "Options",
                "but it is handed over, not thrown away",
                "it may belong to this install's interface file — deciding that needs what the file cannot say");

            check(Read(@"{""Play"":""Jouer""}").StrandedModUi == null,
                "and a file with none says none",
                "null rather than an empty map, so the migration below can tell 'nothing to move' from 'moved nothing'");
        }

        private static void TheCaptureOrder(Action<bool, string, string> check)
        {
            check(TranslationFileEntries.ReadIndex(JToken.FromObject(1)) == 1
                  && TranslationFileEntries.ReadIndex(JToken.FromObject(TranslationFileEntries.MaxOrderIndex))
                     == TranslationFileEntries.MaxOrderIndex,
                "an index within range is the index",
                "it is the position a line was captured at, and the editors show lines in that order");

            check(TranslationFileEntries.ReadIndex(JToken.FromObject(0)) == null
                  && TranslationFileEntries.ReadIndex(JToken.FromObject(-3)) == null
                  && TranslationFileEntries.ReadIndex(
                         JToken.FromObject(TranslationFileEntries.MaxOrderIndex + 1)) == null,
                "and one outside it is ABSENT, not clamped",
                "clamping puts a line somewhere its author never put it; absent is a state the mod already backfills");

            check(TranslationFileEntries.ReadIndex(null) == null
                  && TranslationFileEntries.ReadIndex(JToken.FromObject("seven")) == null
                  && TranslationFileEntries.ReadIndex(JToken.FromObject(1.5)) == null,
                "anything that is not a whole number has none",
                "a hand-edited file can hold anything, and a fraction is not a position");

            // ⚠ The ceiling is JavaScript's exact-integer limit, not a whim: the website reads this
            // same file in a browser, where anything above it stops being the number it was.
            check(TranslationFileEntries.MaxOrderIndex == 9007199254740991L,
                "the ceiling is what a browser can still count exactly",
                "the site reads this file too; above this an index silently becomes a different one");
        }

        private static void GivingAnIndexToWhatHasNone(Action<bool, string, string> check)
        {
            // 🔴 The requirement is not "an index" but "the SAME index on every machine". Two
            // devices reading one file must agree, or the editors list the same translation in two
            // different orders and a line moves whenever somebody else opens it.
            var read = Read(@"{""Zebra"":""z"",""Apple"":""a"",""Mango"":""m""}");
            long next = read.AssignMissingIndices();

            check(read.Entries["Apple"].Index == 1 && read.Entries["Mango"].Index == 2
                  && read.Entries["Zebra"].Index == 3,
                "lines with no index are numbered in key order, not in file order",
                "a dictionary promises no order; two machines would number the same file differently");

            check(next == 4 && read.Backfilled == 3,
                "and the counter carries on from there",
                "the next line the game shows takes the next number, so the order keeps meaning capture order");

            // ⚠ New numbers start ABOVE what is already there.
            var partial = Read(@"{""Old"":{""v"":""o"",""t"":""A"",""i"":50},""New"":""n""}");
            long after = partial.AssignMissingIndices();
            check(partial.Entries["New"].Index == 51 && after == 52,
                "and above the highest one already in the file",
                "reusing a number would put a new line where an old one already sits");

            check(partial.Entries["Old"].Index == 50,
                "a line that already has one keeps it",
                "the index is where it was captured; renumbering it moves somebody's line for no reason");

            var complete = Read(@"{""Play"":{""v"":""p"",""t"":""A"",""i"":3}}");
            long unchanged = complete.AssignMissingIndices();
            check(complete.Backfilled == 0 && !complete.NeedsRewrite && unchanged == 4,
                "a file where every line is numbered is not rewritten",
                "rewriting for nothing changes nothing on the server but costs a write at every launch");

            check(Read(@"{""Play"":""p""}").AssignMissingIndices() == 2,
                "and an unnumbered file starts at one",
                "not at zero: the index is a position, and the whole mod reads 1 as the first");

            // ⚠ In the CURRENT shape, missing only the index — a legacy string would already have
            // asked for a rewrite by itself, and the case would pass without the backfill doing
            // anything. It did, and only breaking the rule on purpose showed it.
            var backfilled = Read(@"{""Play"":{""v"":""p"",""t"":""A""}}");
            check(!backfilled.NeedsRewrite,
                "a file whose only fault is a missing index asks for nothing yet",
                "so what the next line proves is the backfill, and not something the reading already decided");

            backfilled.AssignMissingIndices();
            check(backfilled.NeedsRewrite,
                "and backfilling is what makes it have to be written back",
                "the index is left out of the content hash, so this costs nothing in sync — but unwritten, the same lines are backfilled at every launch");

            check(new TranslationFileEntries().AssignMissingIndices() == 1,
                "an empty file starts at one too",
                "a fresh translation numbers its first captured line 1, like every other");
        }

        private static void WritingThemBackOut(Action<bool, string, string> check)
        {
            // 🔴 The property that matters more than any single rule: what is written is what
            // comes back. Anything lost here is lost the next time the file is opened, silently,
            // because nothing anywhere compares the two.
            var original = Read(@"{""Zebra"":{""v"":""z"",""t"":""H"",""i"":9},"
                                + @"""Apple"":{""v"":""a"",""t"":""V"",""i"":2},"
                                + @"""Mango"":{""v"":""m"",""t"":""A""}}");

            var written = new JObject();
            TranslationFileEntries.WriteInto(written, original.Entries);
            var reread = TranslationFileEntries.ReadAll(written);

            bool identical = reread.Entries.Count == original.Entries.Count;
            foreach (var line in original.Entries)
            {
                if (!reread.Entries.TryGetValue(line.Key, out var back)
                    || back.Value != line.Value.Value
                    || back.Tag != line.Value.Tag
                    || back.Index != line.Value.Index)
                {
                    identical = false;
                }
            }

            check(identical,
                "what is written comes back as itself",
                "anything lost here is lost the next time the file is opened, and nothing compares the two");

            check(!reread.NeedsRewrite,
                "and comes back asking for nothing",
                "a file this build just wrote must not need repairing by this build — that is a save on every launch, for ever");

            // ⚠ Sorted: two saves of the same content must produce the same bytes. The file is
            // diffed by people, merged, and its hash compared with the server's.
            var order = new JObject();
            TranslationFileEntries.WriteInto(order, Read(@"{""Zebra"":""z"",""Apple"":""a""}").Entries);
            var names = new System.Collections.Generic.List<string>();
            foreach (var property in order.Properties()) names.Add(property.Name);
            check(names.Count == 2 && names[0] == "Apple" && names[1] == "Zebra",
                "lines are written in key order",
                "a dictionary's order would make every save look like a change to whoever diffs or merges the file");

            // 🔴 Never "i": null — the website refuses it outright, so such a file cannot be
            // published at all and the person is told their upload is invalid.
            var noIndex = new JObject();
            TranslationFileEntries.WriteInto(noIndex, Read(@"{""Play"":{""v"":""p"",""t"":""A""}}").Entries);
            check(!((JObject)noIndex["Play"]).ContainsKey("i"),
                "a line with no capture order carries no 'i' at all",
                "the site refuses a null there, so the file cannot be published and the person is told their upload is invalid");

            // ⚠ Built rather than read: reading always assigns a tag, so a case going through it
            // would pass with this rule broken. Breaking it on purpose is what showed that — and
            // what it guards is a line whose tag was explicitly cleared, which no caller does
            // today. Same convention as ContentHash, which reads an absent tag the same way.
            var untagged = new System.Collections.Generic.Dictionary<string, TranslationEntry>
            {
                ["Play"] = new TranslationEntry { Value = "p", Tag = null },
            };
            var tagged = new JObject();
            TranslationFileEntries.WriteInto(tagged, untagged);
            check(tagged["Play"]["t"].ToString() == "A",
                "a line whose tag was cleared is written as a model's",
                "the site and the quality score both read it; a line without one is a line nobody can attribute");

            var nothing = new JObject { ["_uuid"] = "abc" };
            TranslationFileEntries.WriteInto(nothing,
                new System.Collections.Generic.Dictionary<string, TranslationEntry>());
            check(nothing.Count == 1 && nothing["_uuid"] != null,
                "writing no lines leaves the metadata alone",
                "an empty translation is still a file with a lineage, and losing its uuid starts a new one");
        }

        private static void WhenTheFileMustBeWritten(Action<bool, string, string> check)
        {
            check(!Read(@"{""Play"":{""v"":""Jouer"",""t"":""A"",""i"":1}}").NeedsRewrite,
                "a file already in shape is left alone",
                "rewriting it changes its hash, which the site compares — every launch would look like a local change");

            check(Read(@"{""Play"":""Jouer""}").NeedsRewrite,
                "a file in the old shape has to be written back",
                "otherwise the same conversion happens at every launch while the file keeps travelling as it was");

            check(Read("{\"Line\\r\\nBreak\":\"x\"}").NeedsRewrite,
                "and so does one whose keys are not normalised",
                "the key on disk is not the key in memory, so what the site is compared against is not what is being used");

            check(Read(@"{""Options"":{""v"":""Options"",""t"":""M""}}").NeedsRewrite,
                "and one still carrying interface lines",
                "they were taken out of what is used; leaving them on disk means they come back at the next read");

            check(Read("{\"Line\\r\\nBreak\":{\"v\":\"Auto\",\"t\":\"A\"},"
                       + "\"Line\\nBreak\":{\"v\":\"Par\",\"t\":\"H\"}}").NeedsRewrite,
                "a collision that changed the answer counts too",
                "the file holds two lines where the mod now holds one, and only writing settles which");
        }
    }
}
