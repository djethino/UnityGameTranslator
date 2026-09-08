using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Loading a translation again re-derives EVERYTHING a translation carries — not its lines
    /// alone.
    ///
    /// 🔴 **The rule, in the user's words (2026-09-08):** *"chaque changement de traduction, que ce
    /// soit un download, une fusion vers le local ou un restore doit réappliquer toute la chaîne"*.
    /// It is one rule and it has been broken one link at a time, each time by somebody adding a
    /// layer and not knowing this chain existed:
    ///
    /// | link | what its absence did |
    /// |---|---|
    /// | the six settings sections | a restored translation kept the previous one's replacement image, its exclusions and its variables |
    /// | the fonts | the components went on wearing the font the previous translation asked for |
    /// | the screens | the parameters panel listed an image the game had already taken back off |
    /// | **the online watch** | the stream stayed open on the translation that was left, and rewrote the card back to it minutes later |
    ///
    /// ⚠ **A lexical check, like <see cref="LoadedIdentityChecks"/> and for the same reason**: every
    /// one of these calls needs a game, a screen or a server. What is checkable without running
    /// anything is that the chain still HAS its links — and that is precisely what went missing
    /// each time, since each defect was a call that was never made rather than a call that
    /// misbehaved.
    ///
    /// ⚠ It cannot see a SEVENTH layer added tomorrow and forgotten. Nothing can. What it does is
    /// hold what has already been paid for, so the same link is not dropped twice.
    /// </summary>
    internal static class ReloadChainChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string coreFile = Find("UnityGameTranslator.Core", "TranslatorCore.cs");
            string uiFile = Find("UnityGameTranslator.Core", "UI", "TranslatorUIManager.cs");

            check(coreFile != null && uiFile != null,
                "the two sources holding the chain are found",
                "this check reads them; without them, it proves nothing");
            if (coreFile == null || uiFile == null) return;

            string reload = BodyOf(File.ReadAllText(coreFile), "public static void ReloadCache()");
            string notify = BodyOf(File.ReadAllText(uiFile), "public static void NotifyTranslationReloaded()");
            string watch = BodyOf(File.ReadAllText(uiFile), "public static void StartSyncWatch()");

            check(reload != null && notify != null && watch != null,
                "and the three methods it is anchored on are still there",
                "renamed or moved, the check must say so rather than pass on an empty comparison");
            if (reload == null || notify == null || watch == null) return;

            // ── What a reload re-runs, in TranslatorCore. ──
            var chain = new (string Call, string Cost)[]
            {
                ("LoadCache()",
                 "without it a reload reloads nothing"),

                ("InvalidateForSections(SettingsSections.All",
                 "ALL of them: a subset is how a restored translation kept the previous one's font, and later its image"),

                ("ReapplyFontSettings(",
                 "changing a font is a transition off the old one — applying the new map alone leaves the components wearing what the previous translation asked for"),

                ("UI.TranslatorUIManager.NotifyTranslationReloaded()",
                 "said here rather than by the callers: it was one of five, and the four that forgot included putting a backup back"),
            };

            foreach (var link in chain)
            {
                check(reload.Contains(link.Call, StringComparison.Ordinal),
                    $"a reload runs {link.Call}",
                    link.Cost);
            }

            // ── What the screens re-read, in the UI manager. ──
            var screens = new (string Call, string Cost)[]
            {
                ("MainPanel?.RefreshUI()",
                 "the card describes a file that is no longer there"),

                ("TranslationParamsPanel?.RefreshFromTranslation()",
                 "its lists are otherwise built when their tab opens and never again — it went on listing an image the game had taken back off"),

                ("OptionsPanel?.RefreshFromConfig()",
                 "the file decides the languages and the setting follows it"),

                ("RewatchIfLineageChanged()",
                 "the online half is bound to a lineage and has no other way of hearing the translation changed — the stream stayed open on the one that was left and rewrote the card back to it"),
            };

            foreach (var link in screens)
            {
                check(notify.Contains(link.Call, StringComparison.Ordinal),
                    $"a reload runs {link.Call}",
                    link.Cost);
            }

            // ── And the watch remembers what it is a watch OF. ──
            //
            // ⚠ Without this the comparison is made against a value that never moves: the first
            // switch restarts the watch, and every later one is judged against the translation
            // loaded at startup — so coming back to it would look like no change at all.
            foreach (string assignment in new[] { "_watchedUuid = TranslatorCore.FileUuid", "_watchedSiteId = TranslatorCore.SourceSiteId" })
            {
                check(watch.Contains(assignment, StringComparison.Ordinal),
                    $"starting a watch records {assignment.Split(' ')[0]}",
                    "a watch that does not know what it watches cannot be told the translation changed");
            }
        }

        /// <summary>
        /// The body of a method, by counting braces from its signature.
        ///
        /// ⚠ Returns null rather than guessing when the signature is not found: the caller stops
        /// there, so a rename fails the section instead of quietly checking an empty string.
        /// </summary>
        private static string BodyOf(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return null;

            int open = text.IndexOf('{', start + signature.Length);
            if (open < 0) return null;

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
