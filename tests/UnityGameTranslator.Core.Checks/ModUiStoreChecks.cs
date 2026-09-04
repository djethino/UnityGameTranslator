using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The interface file across a whole SEQUENCE — load, change, reload, save — on real files in a
    /// real folder.
    ///
    /// 🔴 **This exists because of the one defect the pure checks could never see.** Every rule
    /// about which interface line goes where was pure, checked, and right; what lost data was a
    /// right rule firing at the wrong MOMENT — a save flushed before the file had been read,
    /// replacing 333 translated labels with an empty file. It compiled, every check passed, the
    /// game ran, and the result is indistinguishable from an interface nobody had translated yet.
    ///
    /// A moment only exists in a sequence, so these cases are sequences. They write and read actual
    /// files under a temporary folder, deleted afterwards.
    ///
    /// ⚠ What is NOT covered, and cannot be from here: the order in which TranslatorCore calls
    /// these methods. What is pinned is that no ORDER can destroy the file — which is the property
    /// that matters, and the one that survives somebody rearranging the caller.
    /// </summary>
    internal static class ModUiStoreChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "ugt-modui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                NeverWritesBeforeReading(check, Fresh(root));
                RoundTrips(check, Fresh(root));
                KeepsAnUnreadableFile(check, Fresh(root));
                SetsAsideAndTakesBack(check, Fresh(root));
                SavesWhenOnlyTheFontMoved(check, Fresh(root));
                SurvivesAFailedReload(check, Fresh(root));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        /// <summary>A folder of its own per case, so one cannot leave state for the next.</summary>
        private static string Fresh(string root)
        {
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string FileIn(string folder) => Path.Combine(folder, ModUi.FileName);

        private static ModUiStore Store() => new ModUiStore { Info = _ => { }, Warn = _ => { } };

        private static void Write(string folder, string name, string content) =>
            File.WriteAllText(Path.Combine(folder, name), content);

        /// <summary>A file as the mod writes one, with the given lines.</summary>
        private static void WriteInterface(string folder, string language, string font,
                                           params string[] pairs)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"_engine_version\": 1");
            if (language != null) sb.Append(",\n  \"_target_language\": \"").Append(language).Append("\"");
            if (font != null) sb.Append(",\n  \"_settings\": { \"ui_font\": \"").Append(font).Append("\" }");
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                sb.Append(",\n  \"").Append(pairs[i]).Append("\": { \"v\": \"").Append(pairs[i + 1]).Append("\", \"t\": \"M\" }");
            sb.Append("\n}");
            Write(folder, ModUi.FileName, sb.ToString());
        }

        // ── 🔴 The one that lost data ─────────────────────────────────────────
        private static void NeverWritesBeforeReading(Action<bool, string, string> check, string folder)
        {
            WriteInterface(folder, "Thai", "Tahoma", "Apply", "ใช้", "Close", "ปิด");
            long before = new FileInfo(FileIn(folder)).Length;

            var store = Store();

            // A save asked for before any read — exactly what LoadCache does when it flushes a
            // pending write on its way to reading the file.
            bool wrote = store.Save(folder, "Thai", "Tahoma");

            check(!wrote && new FileInfo(FileIn(folder)).Length == before,
                "a save before any read is refused",
                "🔴 it replaced 333 translated labels with an empty file, and nothing threw");

            // And SaveIfDirty must not find a way round it — the font alone made it fire.
            store.SaveIfDirty(folder, "Thai", "SomeOtherFont");
            check(new FileInfo(FileIn(folder)).Length == before,
                "and a font change cannot get round it",
                "the font needs no line to be dirty, which is how the write fired that early");

            store.Load(folder, "Thai");
            check(store.Entries.Count == 2 && store.Entries["Apply"].Value == "ใช้",
                "the file was still there to be read",
                "the whole point: the refusal above is what left anything to load");
        }

        private static void RoundTrips(Action<bool, string, string> check, string folder)
        {
            var store = Store();
            store.Load(folder, "French");           // nothing on disk yet
            store.Entries["Apply"] = new TranslationEntry { Value = "Appliquer", Tag = ModUi.Tag };
            store.Entries["Apply ([!v*0])"] = new TranslationEntry { Value = "Appliquer ([!v*0])", Tag = ModUi.Tag };
            store.Entries["Two\nlines"] = new TranslationEntry { Value = "Deux\nlignes", Tag = ModUi.Tag };
            store.Modified = true;

            check(store.Save(folder, "French", "Tahoma"),
                "a store read from an absent file may be written",
                "no file is a reading too, and writing one destroys nothing");

            var back = Store();
            back.Load(folder, "French");

            check(back.Entries.Count == 3
                  && back.Entries["Apply"].Value == "Appliquer"
                  && back.Language == "French" && back.Font == "Tahoma",
                "what was written comes back",
                "a round trip that loses metadata loses the language and the font with it");

            check(back.Entries["Apply ([!v*0])"].Value == "Appliquer ([!v*0])",
                "placeholders survive the round trip",
                "a mangled [!v*0] is a label that shows a raw marker to a player");

            check(back.Entries["Two\nlines"].Value == "Deux\nlignes",
                "line breaks survive it too",
                "keys are matched exactly; a changed line ending is a key that never matches again");

            check(back.Entries["Apply"].Tag == ModUi.Tag,
                "every entry is an interface line",
                "the file holds one kind of thing, and that is what makes the split hold");
        }

        private static void KeepsAnUnreadableFile(Action<bool, string, string> check, string folder)
        {
            Write(folder, ModUi.FileName, "{ this is not json");
            long before = new FileInfo(FileIn(folder)).Length;

            var store = Store();
            store.Load(folder, "French");

            check(store.Entries.Count == 0,
                "an unreadable file yields an empty store",
                "half a file would show some labels translated and some not — read as a bug in the mod");

            check(!store.Save(folder, "French", null) && new FileInfo(FileIn(folder)).Length == before,
                "and it is never overwritten",
                "🔴 it is still somebody's work, and replacing it with an empty one cannot be undone");
        }

        private static void SetsAsideAndTakesBack(Action<bool, string, string> check, string folder)
        {
            WriteInterface(folder, "Thai", "Tahoma", "Apply", "ใช้");

            // The game switches to French: the Thai one is put away, not overwritten.
            var french = Store();
            french.Load(folder, "French");

            string thaiAside = Path.Combine(folder, ModUi.SetAsideFileName("Thai"));
            check(File.Exists(thaiAside) && french.Entries.Count == 0,
                "another language is set aside, and a fresh one starts",
                "an interface in one language is noise in another; deleting it throws away a pass");

            french.Entries["Apply"] = new TranslationEntry { Value = "Appliquer", Tag = ModUi.Tag };
            french.Modified = true;
            french.Save(folder, "French", "Tahoma");

            // And back to Thai: the French goes away, the Thai returns intact.
            var thai = Store();
            thai.Load(folder, "Thai");

            check(thai.Entries.Count == 1 && thai.Entries["Apply"].Value == "ใช้" && thai.Language == "Thai",
                "coming back to a language finds its work again",
                "the set-aside name is derived, so nothing has to have remembered it");

            check(File.Exists(Path.Combine(folder, ModUi.SetAsideFileName("French"))),
                "and the one that was in use is put away in its turn",
                "a swap that loses one side is a swap somebody only makes once");

            check(!File.Exists(thaiAside),
                "the file taken back is no longer also set aside",
                "two copies of one language is one of them going stale unnoticed");
        }

        private static void SavesWhenOnlyTheFontMoved(Action<bool, string, string> check, string folder)
        {
            WriteInterface(folder, "French", "Tahoma", "Apply", "Appliquer");

            var store = Store();
            store.Load(folder, "French");

            check(!store.Modified,
                "a freshly read store owes the file nothing",
                "a save on every launch would rewrite a file nobody changed");

            check(!store.SaveIfDirty(folder, "French", "Tahoma"),
                "and nothing is written when nothing moved",
                "same reason");

            check(store.SaveIfDirty(folder, "French", "NotoSans"),
                "🔴 a font change alone is written",
                "it is not in the dictionary, so nothing marks the store dirty — the file kept the "
                + "old font until a new line happened to be translated, which on a finished "
                + "interface is never");

            var back = Store();
            back.Load(folder, "French");
            check(back.Font == "NotoSans" && back.Entries.Count == 1,
                "and the lines are still there afterwards",
                "a write triggered by the font must write the whole file, not a header");
        }

        private static void SurvivesAFailedReload(Action<bool, string, string> check, string folder)
        {
            WriteInterface(folder, "French", "Tahoma", "Apply", "Appliquer", "Close", "Fermer");

            var store = Store();
            store.Load(folder, "French");
            check(store.Entries.Count == 2, "read once, two lines", "the starting point");

            // Something replaces the file with rubbish between two reads — a half-written file, a
            // hand edit, a crash mid-save.
            Write(folder, ModUi.FileName, "{ broken");
            store.Load(folder, "French");
            long broken = new FileInfo(FileIn(folder)).Length;

            check(store.Entries.Count == 0 && !store.Save(folder, "French", "Tahoma"),
                "🔴 a RELOAD that fails also blocks the next write",
                "the flag is lowered for the whole of a read, not only the first: an empty store "
                + "behind a flag still claiming to reflect the disk is the same loss, one launch later");

            check(new FileInfo(FileIn(folder)).Length == broken,
                "and the file on disk is left exactly as it was",
                "somebody can still repair it by hand; an overwrite ends that");
        }
    }
}
