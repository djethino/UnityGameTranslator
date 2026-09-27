using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>What applying an asset pack did.</summary>
    public sealed class AssetPackResult
    {
        public AssetPackResult(bool done, int fonts, int images, string failure)
        {
            Done = done;
            Fonts = fonts;
            Images = images;
            Failure = failure;
        }

        public bool Done { get; }
        public int Fonts { get; }
        public int Images { get; }
        public string Failure { get; }
    }

    /// <summary>
    /// Asset packs (`.ugtpack`) for the game this mod runs in: which ones sit in the packs folder, what
    /// one would do, and the act itself — applied while the game runs.
    ///
    /// 🔴 **Every decision is the socle's** (<see cref="AssetPlanner"/>, <see cref="AssetPackReader"/>),
    /// the very one UGT Manager takes: both refuse the same packs, for the same reasons, in the same
    /// words. What is left here is this product's part — the game as the mod holds it in memory, the
    /// manifest read with the mod's JSON, and the act on the mod's own state (fonts registered, image
    /// settings set, pictures loaded). Design: analyse/manager-onglet-assets.md (root).
    ///
    /// ⚠ No interface here (the engine never names it): the Translation Tools window calls this, off
    /// the main thread for the reading, and on it for the act — the image replacer creates textures.
    /// </summary>
    public static class AssetPackService
    {
        /// <summary>The folder a player puts packs in, prepared at start (AssetPacks.PreparedFolders).</summary>
        public static string PacksFolder =>
            string.IsNullOrEmpty(TranslatorCore.ModFolder) ? null : Path.Combine(TranslatorCore.ModFolder, AssetPacks.PacksFolder);

        /// <summary>The packs in the packs folder, by name.</summary>
        public static List<string> PacksInFolder()
        {
            var packs = new List<string>();
            var folder = PacksFolder;

            try
            {
                if (folder != null && Directory.Exists(folder))
                {
                    packs.AddRange(Directory.GetFiles(folder).Where(f => AssetPacks.IsPack(f)));
                    packs.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                TranslatorCore.LogWarning($"[Assets] Could not read the packs folder: {e.Message}");
            }

            return packs;
        }

        /// <summary>
        /// What these files would do to this game — reads them, writes nothing. Safe off the main
        /// thread, given a <paramref name="side"/> taken on it: this reads files only.
        /// </summary>
        public static AssetPlan Plan(IReadOnlyList<string> paths, GameAssetSide side)
        {
            var dropped = paths.Select(path => new DroppedFile(Path.GetFileName(path), () => File.OpenRead(path))).ToList();
            return AssetPlanner.Plan(side, dropped, ParseManifest);
        }

        /// <summary>
        /// The game as the mod holds it, in the socle's terms — ⚠ taken on the MAIN thread: it copies
        /// the image settings, which the inspector edits there, before any reading goes elsewhere.
        /// </summary>
        public static GameAssetSide Side()
        {
            var folder = TranslatorCore.ModFolder;
            var game = TranslatorCore.CurrentGame;

            return new GameAssetSide
            {
                GameName = game?.name ?? "",
                ProductName = game?.product_name,
                SteamId = game?.steam_id,
                TranslationExists = File.Exists(TranslatorCore.CachePath),
                TranslationDamaged = TranslatorCore.TranslationFileUnreadable,
                TargetLanguage = TranslatorCore.FileTargetLanguage,
                Definitions = ImageReplacer.Definitions(),
                ExistingSha256 = (kind, name) =>
                {
                    var path = Path.Combine(folder, AssetPacks.FolderOf(kind), name);
                    if (!File.Exists(path)) return null;
                    using (var stream = File.OpenRead(path)) return AssetPackReader.Measure(stream, long.MaxValue).Sha256;
                },
                Room = FreeSpace(folder) ?? long.MaxValue,
            };
        }

        /// <summary>A manifest, read with the mod's JSON into the socle's model — null when it is not JSON.</summary>
        private static PackManifest ParseManifest(byte[] bytes)
        {
            try
            {
                if (!(JToken.Parse(Encoding.UTF8.GetString(bytes)) is JObject root)) return null;

                var game = root[PackManifest.GameField] as JObject;
                var manifest = new PackManifest
                {
                    Format = root[PackManifest.FormatField] is JValue f && f.Type == JTokenType.Integer ? (int?)f.Value<int>() : null,
                    GameName = Text(game?[PackManifest.GameNameField]),
                    SteamId = Text(game?[PackManifest.SteamIdField]),
                    TargetLanguage = Text(root[PackManifest.TargetLanguageField]),
                };

                if (root[PackManifest.ImagesField] is JArray images)
                {
                    foreach (var item in images.OfType<JObject>())
                    {
                        var definition = ImageDefinition.Read(field => item[field] is JValue value ? value.Value : null);
                        if (definition != null) manifest.Images.Add(definition);
                    }
                }

                return manifest;
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is FormatException || e is InvalidCastException)
            {
                // Said to the log, and to the screen by the planner: "its manifest cannot be read".
                Faults.Say("AssetPackService.ParseManifest", e);
                return null;
            }
        }

        private static string Text(JToken token) =>
            token is JValue value && value.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)value) ? (string)value : null;

        /// <summary>
        /// Writes what the person kept of a plan, and puts it to use. Must run on the main thread: the
        /// image replacer creates textures.
        ///
        /// ⚠ Files first, settings after — a setting naming a picture not yet there is what the game
        /// would read if the write stopped halfway. A backup WITH the assets first, whenever something
        /// is replaced or a setting changes (a replaced picture may be one retouched by hand).
        /// </summary>
        /// <param name="sources">The paths the plan was made from, in the same order.</param>
        public static AssetPackResult Apply(IReadOnlyList<string> sources, IEnumerable<AssetOffer> accepted)
        {
            var offers = accepted.ToList();
            var toWrite = offers.SelectMany(o => o.Files).Where(f => f.Change != AssetChange.Same).ToList();
            var toDefine = offers.SelectMany(o => o.Definitions).Where(d => d.Change != AssetChange.Same).ToList();
            if (toWrite.Count == 0 && toDefine.Count == 0) return new AssetPackResult(true, 0, 0, null);

            var folder = TranslatorCore.ModFolder;

            var needed = toWrite.Sum(f => f.Asset.Length);
            if (FreeSpace(folder) is long free && needed > free)
                return new AssetPackResult(false, 0, 0, $"Not enough free space on this drive: {AssetPlanner.Megabytes(needed)} needed, {AssetPlanner.Megabytes(free)} free.");

            if (toDefine.Count > 0 && AssetPlanner.ImageSettingsRefusal(Side()) is string cannot)
                return new AssetPackResult(false, 0, 0, cannot);

            if (toDefine.Count > 0 || toWrite.Any(f => f.Change == AssetChange.Replace))
                TranslationBackups.TakeAutomatic(BackupReason.AssetsAdded, withAssets: true);

            int fonts = 0, images = 0;

            try
            {
                foreach (var planned in toWrite)
                {
                    var asset = planned.Asset;
                    if (!AssetPacks.IsSafeFileName(asset.Name)) continue;   // decided at planning; held again at the door

                    var directory = Path.Combine(folder, AssetPacks.FolderOf(asset.Kind));
                    Directory.CreateDirectory(directory);
                    var target = Path.Combine(directory, asset.Name);
                    var temp = target + ".tmp";

                    try
                    {
                        using (var file = File.OpenRead(sources[asset.Source]))
                        using (var source = asset.EntryName == null ? file : AssetPackReader.OpenByName(file, asset.EntryName))
                        using (var output = File.Create(temp))
                        {
                            AssetPackReader.CopyExactly(source, output, asset.Length, asset.Sha256, asset.Name);
                        }

                        if (File.Exists(target)) File.Delete(target);
                        File.Move(temp, target);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }

                    if (asset.Kind == AssetKind.Font)
                    {
                        CustomFontLoader.Register(target);
                        fonts++;
                    }
                }

                // The settings, then each picture loaded — a replaced picture is read again from disk.
                foreach (var planned in toDefine)
                {
                    ImageReplacer.SetDefinition(planned.Definition);
                    images++;
                }

                var pictures = offers.Where(o => o.Kind == AssetKind.Image)
                    .SelectMany(o => o.Definitions.Select(d => d.Definition.Sprite)).ToList();
                foreach (var sprite in pictures)
                {
                    var entry = ImageReplacer.GetAll().TryGetValue(sprite, out var e) ? e : null;
                    if (entry?.File != null) ImageReplacer.ImportReplacement(entry.SpriteName, entry.File);
                }

                // A picture handed over on its own fills a setting the translation already has.
                foreach (var planned in toWrite.Where(f => f.Asset.Kind == AssetKind.Image))
                {
                    foreach (var entry in ImageReplacer.GetAll().Values.Where(r => string.Equals(r.File, planned.Asset.Name, StringComparison.OrdinalIgnoreCase)))
                        ImageReplacer.ImportReplacement(entry.SpriteName, entry.File);
                }

                return new AssetPackResult(true, fonts, images, null);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                return new AssetPackResult(false, fonts, images, $"{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Free bytes on the drive holding this folder — null when the system cannot say.</summary>
        private static long? FreeSpace(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return null;
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                return string.IsNullOrEmpty(root) ? (long?)null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                // A drive that cannot be measured is not refused: the write itself will say if it fails.
                Faults.Say("AssetPackService.FreeSpace", e);
                return null;
            }
        }
    }
}
