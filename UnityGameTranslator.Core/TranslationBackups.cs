using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The translation's own history on this machine: copies taken by an action, and copies taken
    /// because somebody asked.
    ///
    /// 🔴 **It lives in the mod because the mod is the only product always present.** Somebody may
    /// never install the Manager — that is the case for everyone today — and a safety net that
    /// only exists in a tool they do not have is not a safety net. The Manager reads and writes
    /// these very folders; it adds comfort, never capability.
    ///
    /// ⚠ **What is decided lives in <see cref="Backups"/>** (the shared library): the folder, the
    /// two families, their limits, which assets belong to a copy, and every word a row reads. This
    /// file does the reading and writing, because the two products disagree about their JSON and
    /// the shared library is deliberately dependency-free.
    ///
    /// ⚠ **Nothing here ever throws into the game.** A copy that cannot be written must not stop
    /// the act the player asked for — it is reported in the log and the act continues. Losing a
    /// safety copy is bad; losing the operation it was protecting is worse.
    /// </summary>
    public static class TranslationBackups
    {
        /// <summary>The folder holding every copy, inside this game's mod folder.</summary>
        public static string Folder =>
            string.IsNullOrEmpty(TranslatorCore.ModFolder)
                ? null
                : Path.Combine(TranslatorCore.ModFolder, Backups.FolderName);

        private const string TranslationFile = "translations.json";
        private const string AncestorFile = "translations.json.ancestor";

        /// <summary>Marks an id naming a file an earlier version left, rather than a folder.</summary>
        private const string LegacyPrefix = "legacy:";

        // ── Reading ───────────────────────────────────────────────────────

        /// <summary>
        /// Every copy this game holds, newest first.
        ///
        /// ⚠ Read from each copy's own `about.json` and never by opening the translation beside
        /// it: fifteen files of half a megabyte, parsed to draw a list, is a list that takes a
        /// second to appear in a game.
        /// </summary>
        public static List<BackupEntry> List()
        {
            var entries = new List<BackupEntry>();

            try
            {
                var root = Folder;

                // ⚠ Not an early return. Every install that exists today has no backups folder and
                // several older copies beside the translation; leaving at the first line would
                // have shown "nothing kept" to exactly the people who have the most to lose.
                if (root != null && Directory.Exists(root))
                {
                    foreach (var directory in Directory.GetDirectories(root))
                    {
                        var name = Path.GetFileName(directory);
                        if (!Backups.IsBackupFolder(name, out var saved)) continue;

                        // A folder with no translation in it is not a copy of anything. It happens
                        // when a write was interrupted, and offering it would promise a restore
                        // that puts nothing back.
                        if (!File.Exists(Path.Combine(directory, TranslationFile))) continue;

                        entries.Add(ReadAbout(directory, name, saved));
                    }
                }

                entries.AddRange(Legacy());
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not list copies: {e.Message}");
            }

            entries.Sort((a, b) => b.At.CompareTo(a.At));
            return entries;
        }

        /// <summary>
        /// What earlier versions left beside the translation: the single `.backup` this mod used
        /// to overwrite, the orphaned `.prepurge`, and the folder the Manager filled.
        ///
        /// 🔴 **Listed, not ignored.** Each of them is somebody's translation. And the Manager
        /// lists them, so a mod that did not would show "nothing kept" over the same folder the
        /// tool describes as holding four copies — two windows disagreeing about one disk.
        ///
        /// ⚠ Read-only in practice: nothing new is written in those shapes, and the rotation never
        /// touches them because they do not live in the backups folder.
        /// </summary>
        private static List<BackupEntry> Legacy()
        {
            var found = new List<BackupEntry>();

            void Add(string path)
            {
                try
                {
                    found.Add(new BackupEntry
                    {
                        Id = LegacyPrefix + Path.GetFileName(path),
                        At = File.GetLastWriteTime(path),
                        Reason = BackupReason.Unknown,
                        Lines = CountLines(path),
                        Uuid = UuidIn(path),
                        WithAssets = false,
                    });
                }
                catch (Exception e)
                {
                    TranslatorCore.LogWarning($"[Backups] Skipped {path}: {e.Message}");
                }
            }

            try
            {
                var folder = TranslatorCore.ModFolder;
                if (string.IsNullOrEmpty(folder)) return found;

                foreach (var loose in new[] { ".backup", ".prepurge" })
                {
                    var file = TranslatorCore.CachePath + loose;
                    if (File.Exists(file)) Add(file);
                }

                var removed = Path.Combine(folder, "removed");
                if (Directory.Exists(removed))
                {
                    foreach (var file in Directory.GetFiles(removed, "translations-*.json"))
                        Add(file);
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not read older copies: {e.Message}");
            }

            return found;
        }

        /// <summary>
        /// The file behind a legacy id, or null when the id names one of our own folders.
        ///
        /// ⚠ The name only, never a path: an id is data read off a disk, and one walking up with
        /// ".." would have a restore reach outside the folder it belongs to.
        /// </summary>
        private static string LegacyPath(string id)
        {
            if (id == null || !id.StartsWith(LegacyPrefix, StringComparison.Ordinal)) return null;

            var name = Path.GetFileName(id.Substring(LegacyPrefix.Length));
            if (string.IsNullOrEmpty(name)) return null;

            // The two loose ones sit beside the translation; the dated ones in `removed/`.
            var beside = Path.Combine(TranslatorCore.ModFolder, name);
            if (File.Exists(beside)) return beside;

            var removed = Path.Combine(TranslatorCore.ModFolder, "removed", name);
            return File.Exists(removed) ? removed : null;
        }

        private static int CountLines(string path)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var count = 0;

                foreach (var property in root.Properties())
                {
                    if (!property.Name.StartsWith("_", StringComparison.Ordinal)) count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static string UuidIn(string path)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(path))["_uuid"]?.Value<string>();
            }
            catch
            {
                return null;
            }
        }

        private static BackupEntry ReadAbout(string directory, string id, bool saved)
        {
            var entry = new BackupEntry
            {
                Id = id,
                Reason = saved ? BackupReason.Saved : BackupReason.Unknown,
                WithAssets = saved,
            };

            // ⚠ The folder name is the fallback for the date, and it is a good one: it was written
            // by us, in a sortable form, and it survives a copy of the folder — which a file
            // timestamp does not.
            entry.At = StampOf(id);

            try
            {
                var about = Path.Combine(directory, Backups.AboutFileName);
                if (!File.Exists(about)) return entry;

                var json = JObject.Parse(File.ReadAllText(about));

                if (json["at"]?.Value<string>() is string at
                    && DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    entry.At = parsed;
                }

                if (json["reason"]?.Value<string>() is string reason
                    && Enum.TryParse(reason, ignoreCase: true, out BackupReason known))
                {
                    entry.Reason = known;
                }

                entry.By = json["by"]?.Value<string>();
                entry.Label = json["label"]?.Value<string>();
                entry.Lines = json["lines"]?.Value<int>() ?? 0;
                entry.ByHand = json["by_hand"]?.Value<int>() ?? 0;
                entry.Uuid = json["uuid"]?.Value<string>();
                entry.WithAssets = json["assets"]?.Value<bool>() ?? saved;
            }
            catch (Exception e)
            {
                // A description we cannot read costs the row its details, never its existence:
                // the translation beside it is still restorable, and that is what matters.
                TranslatorCore.LogWarning($"[Backups] Unreadable description in {id}: {e.Message}");
            }

            return entry;
        }

        private static DateTime StampOf(string id)
        {
            var dash = id.IndexOf('-');
            if (dash >= 0
                && DateTime.TryParseExact(id.Substring(dash + 1), "yyyyMMdd-HHmmss",
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.None, out var at))
            {
                return at;
            }

            return DateTime.MinValue;
        }

        // ── Taking a copy ─────────────────────────────────────────────────

        /// <summary>
        /// The copy an ACTION takes, before something replaces the translation wholesale.
        ///
        /// 🔴 Called from inside the replacement itself, never beside it — see
        /// <see cref="TranslatorCore.ReplaceTranslationFile"/>. A path added later that writes the
        /// file without asking anybody is exactly how this was found missing once already.
        /// </summary>
        /// <param name="by">Whose translation the act involves, as a mention. Optional.</param>
        public static void TakeAutomatic(BackupReason reason, string by = null)
        {
            if (reason == BackupReason.Saved)
            {
                TranslatorCore.LogWarning("[Backups] TakeAutomatic asked for a saved copy — ignored");
                return;
            }

            Take(reason, by, label: null, withAssets: false);
            Prune();
        }

        /// <summary>
        /// The copy somebody asks for. Carries the fonts and images the translation names.
        ///
        /// ⚠ No name is asked for here. Somebody taking a safety copy before a risky move must not
        /// be stopped by a text field — in a game, least of all. The row is renamed afterwards.
        /// </summary>
        /// <returns>The new copy's id, or null when it could not be taken.</returns>
        public static string SaveCopy()
        {
            if (!Backups.CanSaveAnother(List()))
            {
                TranslatorCore.LogWarning("[Backups] Refused: no free slot");
                return null;
            }

            return Take(BackupReason.Saved, by: null, label: null, withAssets: true);
        }

        private static string Take(BackupReason reason, string by, string label, bool withAssets)
        {
            try
            {
                var root = Folder;
                if (root == null) return null;

                var source = TranslatorCore.CachePath;
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    // Nothing to copy is not a failure: a game whose translation has never been
                    // written has no history to keep.
                    return null;
                }

                // ⚠ Written to disk first. The cache in memory may hold lines this file does not,
                // and a copy of "what the player has" must be the file the player has — the same
                // thing a restore will put back.
                TranslatorCore.SaveCache();

                var id = UniqueId(root, reason);
                var directory = Path.Combine(root, id);
                Directory.CreateDirectory(directory);

                File.Copy(source, Path.Combine(directory, TranslationFile), overwrite: true);

                // 🔴 The ancestor travels with the translation, always. It describes the version
                // both sides agreed on; restoring a file while leaving the newer ancestor behind
                // leaves the next merge comparing against a state that never existed, and nothing
                // would ever notice.
                var ancestor = source + ".ancestor";
                if (File.Exists(ancestor))
                    File.Copy(ancestor, Path.Combine(directory, AncestorFile), overwrite: true);

                if (withAssets) CopyAssets(directory);

                WriteAbout(directory, reason, by, label, withAssets);

                TranslatorCore.LogInfo($"[Backups] Kept {id} ({Backups.Describe(reason, by)})");
                return id;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not keep a copy: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Two copies inside one second would collide on the name. Rare, and cheap to rule out.
        /// </summary>
        private static string UniqueId(string root, BackupReason reason)
        {
            var at = DateTime.Now;
            var id = Backups.NewId(reason, at);

            var attempt = 1;
            while (Directory.Exists(Path.Combine(root, id)) && attempt < 60)
            {
                at = at.AddSeconds(1);
                id = Backups.NewId(reason, at);
                attempt++;
            }

            return id;
        }

        private static void CopyAssets(string directory)
        {
            var wanted = Backups.AssetsToCopy(ImagesInUse(), FontsInUse());

            foreach (var relative in wanted)
            {
                try
                {
                    var source = Path.Combine(TranslatorCore.ModFolder,
                                              relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source)) continue;

                    var target = Path.Combine(directory,
                                              relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, overwrite: true);
                }
                catch (Exception e)
                {
                    // One asset that cannot be copied costs that asset, never the copy: the
                    // translation itself is the thing that exists nowhere else.
                    TranslatorCore.LogWarning($"[Backups] Skipped {relative}: {e.Message}");
                }
            }
        }

        /// <summary>The image files this translation puts in place, read from the file itself.</summary>
        private static List<string> ImagesInUse()
        {
            var names = new List<string>();

            try
            {
                var path = TranslatorCore.CachePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return names;

                var root = JObject.Parse(File.ReadAllText(path));
                if (!(root["_images"] is JArray images)) return names;

                foreach (var item in images)
                {
                    if (!(item is JObject obj)) continue;

                    var file = obj.Value<string>("file")
                               ?? obj.Value<string>("replacement_file")
                               ?? obj.Value<string>("original_file");

                    if (!string.IsNullOrEmpty(file)) names.Add(file);
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not read the images in use: {e.Message}");
            }

            return names;
        }

        /// <summary>
        /// The font SOURCE files this game holds — the .ttf somebody dropped in.
        ///
        /// ⚠ Generated atlases are deliberately left out: they are rebuilt from the font beside
        /// them, and they are the largest thing in that folder. Keeping them would multiply the
        /// size of every copy for something nobody can lose.
        /// </summary>
        private static List<string> FontsInUse()
        {
            var names = new List<string>();

            try
            {
                var folder = Path.Combine(TranslatorCore.ModFolder, "fonts");
                if (!Directory.Exists(folder)) return names;

                foreach (var file in Directory.GetFiles(folder))
                {
                    var extension = Path.GetExtension(file);
                    if (string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(Path.GetFileName(file));
                    }
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not read the fonts folder: {e.Message}");
            }

            return names;
        }

        private static void WriteAbout(string directory, BackupReason reason, string by,
                                       string label, bool withAssets)
        {
            try
            {
                var stats = StatusCardStats();

                var about = new JObject
                {
                    ["at"] = DateTime.Now.ToString("o"),
                    ["reason"] = reason.ToString(),
                    ["lines"] = stats.Key,
                    ["by_hand"] = stats.Value,
                    ["assets"] = withAssets,
                };

                if (!string.IsNullOrEmpty(by)) about["by"] = by;
                if (!string.IsNullOrEmpty(label)) about["label"] = label;
                if (!string.IsNullOrEmpty(TranslatorCore.FileUuid)) about["uuid"] = TranslatorCore.FileUuid;

                File.WriteAllText(Path.Combine(directory, Backups.AboutFileName),
                                  about.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not describe the copy: {e.Message}");
            }
        }

        /// <summary>Lines, and of those the ones a human wrote or settled.</summary>
        private static KeyValuePair<int, int> StatusCardStats()
        {
            var lines = 0;
            var byHand = 0;

            try
            {
                foreach (var entry in TranslatorCore.TranslationCache)
                {
                    lines++;

                    var tag = entry.Value?.Tag;
                    if (tag == "H" || tag == "V" || tag == "S") byHand++;
                }
            }
            catch
            {
                // Counting is a nicety on a row; it must never cost the copy itself.
            }

            return new KeyValuePair<int, int>(lines, byHand);
        }

        // ── Rotation ──────────────────────────────────────────────────────

        private static void Prune()
        {
            try
            {
                var root = Folder;
                if (root == null) return;

                foreach (var id in Backups.AutomaticToDrop(List()))
                {
                    var directory = Path.Combine(root, id);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not drop the oldest copies: {e.Message}");
            }
        }

        // ── Acting on one ─────────────────────────────────────────────────

        /// <summary>
        /// Puts a copy back in place, after keeping what stands there now.
        ///
        /// 🔴 The current state is kept FIRST, as an automatic copy. Restoring is the one act here
        /// that replaces work, and it must be as undoable as everything else — somebody who picks
        /// the wrong row has to be able to walk back out of it.
        /// </summary>
        public static bool Restore(string id)
        {
            try
            {
                var root = Folder;
                if (root == null) return false;

                TakeAutomatic(BackupReason.Restored);

                // A copy an older version left: one loose file, no ancestor of its own.
                if (LegacyPath(id) is { } loose)
                {
                    if (!File.Exists(loose)) return false;

                    File.Copy(loose, TranslatorCore.CachePath, overwrite: true);

                    // ⚠ The stale ancestor goes rather than staying to describe an agreement that
                    // never happened. A blind first merge is a known state; a wrong base is not.
                    var stale = TranslatorCore.CachePath + ".ancestor";
                    if (File.Exists(stale)) File.Delete(stale);

                    TranslatorCore.ReloadCache();
                    TranslatorCore.LogInfo($"[Backups] Put {id} back");
                    return true;
                }

                var directory = Path.Combine(root, id);
                var source = Path.Combine(directory, TranslationFile);
                if (!File.Exists(source)) return false;

                File.Copy(source, TranslatorCore.CachePath, overwrite: true);

                // The ancestor of the copy, or none at all — never the one belonging to the file
                // we have just replaced. See the note where copies are taken.
                var ancestorTarget = TranslatorCore.CachePath + ".ancestor";
                var ancestorSource = Path.Combine(directory, AncestorFile);

                if (File.Exists(ancestorSource)) File.Copy(ancestorSource, ancestorTarget, overwrite: true);
                else if (File.Exists(ancestorTarget)) File.Delete(ancestorTarget);

                RestoreAssets(directory);

                // Straight into the running game, exactly as a downloaded translation is: a file on
                // disk the game has not read is a restore that appears not to have happened.
                TranslatorCore.ReloadCache();

                TranslatorCore.LogInfo($"[Backups] Put {id} back");
                return true;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not put {id} back: {e.Message}");
                return false;
            }
        }

        private static void RestoreAssets(string directory)
        {
            foreach (var folder in Backups.AssetFolders)
            {
                try
                {
                    var source = Path.Combine(directory, folder);
                    if (!Directory.Exists(source)) continue;

                    var target = Path.Combine(TranslatorCore.ModFolder, folder);
                    Directory.CreateDirectory(target);

                    foreach (var file in Directory.GetFiles(source))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception e)
                {
                    TranslatorCore.LogWarning($"[Backups] Could not put back {folder}: {e.Message}");
                }
            }
        }

        /// <summary>Removes one copy. Only ever called about a copy somebody chose to keep.</summary>
        public static bool Delete(string id)
        {
            try
            {
                var root = Folder;
                if (root == null) return false;

                if (LegacyPath(id) is { } loose)
                {
                    if (!File.Exists(loose)) return false;

                    File.Delete(loose);
                    return true;
                }

                var directory = Path.Combine(root, id);
                if (!Directory.Exists(directory)) return false;

                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not delete {id}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Moves an automatic copy into the deliberate ones, so it stops rotating.
        ///
        /// ⚠ The gesture that closes the loop between the two lists: somebody recognises "the one
        /// from before the merge" and keeps it, instead of watching it age out.
        /// </summary>
        public static bool Keep(string id)
        {
            try
            {
                var root = Folder;
                if (root == null) return false;

                var entries = List();
                if (!Backups.CanSaveAnother(entries)) return false;

                // ⚠ A loose file is promoted by being moved INTO a proper folder: it has no
                // description of its own, and left where it is the next tidy-up owns its fate.
                if (LegacyPath(id) is { } loose)
                {
                    if (!File.Exists(loose)) return false;

                    Directory.CreateDirectory(root);
                    var home = Path.Combine(root, UniqueId(root, BackupReason.Saved));
                    Directory.CreateDirectory(home);

                    File.Move(loose, Path.Combine(home, TranslationFile));
                    WriteAbout(home, BackupReason.Saved, by: null, label: null, withAssets: false);
                    return true;
                }

                var directory = Path.Combine(root, id);
                if (!Directory.Exists(directory)) return false;

                var moved = Path.Combine(root, Backups.NewId(BackupReason.Saved, StampOf(id)));
                if (Directory.Exists(moved)) return false;

                Directory.Move(directory, moved);

                // ⚠ The reason it was taken is KEPT, not overwritten with "Saved by you". "Before
                // installing @Seniorito's translation" is why this copy is worth keeping; losing
                // that at the moment somebody decides to keep it would be perverse.
                Retouch(moved, about =>
                {
                    about["kept"] = true;
                    return about;
                });

                return true;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not keep {id}: {e.Message}");
                return false;
            }
        }

        /// <summary>What somebody calls a copy. Only meaningful on one they chose to keep.</summary>
        public static bool Rename(string id, string label)
        {
            try
            {
                var root = Folder;
                if (root == null) return false;

                var directory = Path.Combine(root, id);
                if (!Directory.Exists(directory)) return false;

                Retouch(directory, about =>
                {
                    if (string.IsNullOrWhiteSpace(label)) about.Remove("label");
                    else about["label"] = label.Trim();

                    return about;
                });

                return true;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Backups] Could not rename {id}: {e.Message}");
                return false;
            }
        }

        private static void Retouch(string directory, Func<JObject, JObject> change)
        {
            var path = Path.Combine(directory, Backups.AboutFileName);

            var about = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            about = change(about);

            File.WriteAllText(path, about.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }
}
