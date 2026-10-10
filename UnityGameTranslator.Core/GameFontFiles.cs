using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Common;
using UnityGameTranslator.Common.UnityFiles;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The game's own fonts as FILES, read from its data files (common's UnityFiles.GameFonts).
    ///
    /// 🔴 Why (issue #33): a legacy Font (UI.Text, TextMesh…) falls back only on fonts the engine
    /// finds by NAME in its font folder, so a letter our replacement lacks could come from the
    /// game's font only where that font is installed — elsewhere from the system's default. The
    /// game's font file is inside its data files: extracted, and shown to the engine like fonts/
    /// (FontFolderRedirect), it backs the replacement up exactly (decided with the user, 2026-10-10 —
    /// analyse/export-polices-jeu.md).
    ///
    /// - The INDEX (which data file holds which font, and whether it embeds its file) is read in the
    ///   background, once per version of the game: kept with a stamp of the data folder, read again
    ///   only when it changes. Shown in the corner while it runs (FontJobs).
    /// - A BACKUP (the original font of a component whose font is replaced) is extracted
    ///   automatically, alone, into a cache next to the derived copies — never a Custom font, never
    ///   exported: every player's mod makes its own. Shown at start from the next launch; in this
    ///   session under a pool name (DerivedFonts.LateCopy).
    /// - EXTRACT (a person's choice, one font) writes into fonts/: it becomes a Custom font.
    ///
    /// ⚠ The reading runs off the main thread (disk only, no Unity); what it gives back is applied in
    /// the scanner's tick (<see cref="Tick"/>), on the main thread.
    /// </summary>
    internal static class GameFontFiles
    {
        internal const string Folder = ".ugt-game-fonts";
        private const string IndexFile = "index.txt";

        private static string _cache, _dataFolder, _unityVersion;
        private static volatile GameFonts.Reading _index;
        private static int _indexing;
        // Backups shown to the engine at start (their own family), by game font name → file.
        private static readonly Dictionary<string, string> _shownAtStart = new Dictionary<string, string>(StringComparer.Ordinal);
        // Backups lent a pool name this session, by game font name → the family the engine knows it by.
        private static readonly Dictionary<string, string> _lent = new Dictionary<string, string>(StringComparer.Ordinal);
        // Game fonts whose backup was asked for: tried once a session (said when it cannot be).
        private static readonly HashSet<string> _wanted = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<Action> _onMain = new ConcurrentQueue<Action>();

        /// <summary>
        /// Before FontFolderRedirect is installed: where the cache is, and the backups extracted by an
        /// earlier launch — shown to the engine at start, which lists its font folder once.
        /// </summary>
        internal static List<string> Prepare(string fontsFolder)
        {
            var shown = new List<string>();
            if (string.IsNullOrEmpty(fontsFolder)) return shown;
            _cache = Path.Combine(fontsFolder, Folder);
            if (!Directory.Exists(_cache)) return shown;
            foreach (var file in Directory.GetFiles(_cache))
            {
                if (!AssetPacks.IsFontFile(Path.GetFileName(file))) continue;
                _shownAtStart[Path.GetFileNameWithoutExtension(file)] = file;
                shown.Add(file);
            }
            return shown;
        }

        /// <summary>The game's data folder and Unity version — read by the caller on the main thread (Application).</summary>
        internal static void Locate(string dataFolder, string unityVersion)
        {
            _dataFolder = dataFolder;
            _unityVersion = unityVersion;
        }

        /// <summary>The index, or null while it is read (or when it could not be).</summary>
        internal static GameFonts.Reading Index => _index;

        /// <summary>Whether the index is being read now.</summary>
        internal static bool Indexing => System.Threading.Volatile.Read(ref _indexing) == 1;

        /// <summary>
        /// Reads the index in the background unless it is known or being read: from the cache when the
        /// game's files have not changed, from the game's files otherwise. <paramref name="why"/> is said.
        /// </summary>
        internal static void EnsureIndex(string why)
        {
            if (_index != null || _cache == null || string.IsNullOrEmpty(_dataFolder)) return;
            if (System.Threading.Interlocked.Exchange(ref _indexing, 1) == 1) return;
            string dataFolder = _dataFolder, version = _unityVersion, saved = Path.Combine(_cache, IndexFile);
            System.Threading.Tasks.Task.Run(() =>
            {
                FontJobs.Job job = null;
                try
                {
                    var known = GameFonts.LoadIndex(saved);
                    string stamp = GameFonts.Stamp(dataFolder);
                    if (known != null && known.Stamp == stamp)
                    {
                        _index = known;
                        TranslatorCore.LogInfo($"[GameFonts] {Summary(known)} — kept from an earlier launch (the game's files have not changed)");
                        return;
                    }
                    job = FontJobs.Start("Reading the game's fonts", "files");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var index = GameFonts.FromDataFolder(dataFolder, version, withData: false, progress: (done, total) => { job.SetTotal(total); job.SetDone(done); });
                    GameFonts.SaveIndex(index, saved);
                    _index = index;
                    TranslatorCore.LogInfo($"[GameFonts] {Summary(index)}, read in {clock.Elapsed.TotalSeconds:0.0} s ({why})");
                    foreach (var newer in index.NewerThanTable)
                        TranslatorCore.LogWarning($"[GameFonts] Unity {newer} is newer than the font layouts this mod knows: its fonts were read with the closest one, which may be wrong");
                    foreach (var notRead in index.NotRead)
                        TranslatorCore.LogInfo($"[GameFonts] not read: {notRead.Key} — {notRead.Value}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is UnityFileFormatException)
                {
                    // The boundary of a background thread: said; asked again at the next launch.
                    TranslatorCore.LogWarning($"[GameFonts] The game's fonts could not be read: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (job != null) FontJobs.Finish(job);
                    System.Threading.Volatile.Write(ref _indexing, 0);
                    // Whatever waited for the index: asked again, on the main thread.
                    _onMain.Enqueue(RetryWanted);
                }
            });
        }

        private static string Summary(GameFonts.Reading index)
        {
            int withFile = 0;
            foreach (var f in index.Fonts) if (f.HasFile) withFile++;
            return $"{index.Fonts.Count} font(s) in the game's files, {withFile} with their file";
        }

        /// <summary>
        /// The font of the index that is the game's Font of this name — the one with a file first
        /// (a name can be carried by an empty built-in reference too); null when none has a file.
        /// </summary>
        internal static GameFonts.Font Find(string gameFontName)
        {
            var index = _index;
            if (index == null || string.IsNullOrEmpty(gameFontName)) return null;
            foreach (var f in index.Fonts)
                if (f.HasFile && string.Equals(f.Name, gameFontName, StringComparison.Ordinal)) return f;
            return null;
        }

        /// <summary>
        /// What the engine knows the backup of <paramref name="gameFontName"/> by, for a replacement's
        /// fontNames: the FILE shown at start (its names are in its name table), or the pool FAMILY it
        /// was lent this session. Neither yet: the backup is asked for (extracted in the background,
        /// then <see cref="FontManager.OnGameFontBackupReady"/>), and both come back null.
        /// </summary>
        internal static void BackupOf(string gameFontName, out string fileShownAtStart, out string lentFamily)
        {
            fileShownAtStart = null;
            lentFamily = null;
            if (string.IsNullOrEmpty(gameFontName) || _cache == null) return;
            string key = DerivedFonts.Sanitized(gameFontName);
            if (_shownAtStart.TryGetValue(key, out fileShownAtStart)) return;
            if (_lent.TryGetValue(gameFontName, out lentFamily)) return;
            Want(gameFontName);
        }

        private static void Want(string gameFontName)
        {
            if (!_wanted.Add(gameFontName)) return;
            if (_index == null) { EnsureIndex("the original font of a replaced text is wanted as its backup"); return; }
            ExtractBackup(gameFontName);
        }

        private static void RetryWanted()
        {
            if (_index == null) return;
            foreach (var name in new List<string>(_wanted)) ExtractBackup(name);
        }

        private static readonly HashSet<string> _extracting = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Main thread: the backup of one game font read and written in the background, then lent a pool name.</summary>
        private static void ExtractBackup(string gameFontName)
        {
            if (_lent.ContainsKey(gameFontName) || !_extracting.Add(gameFontName)) return;
            var font = Find(gameFontName);
            if (font == null)
            {
                // No file to extract: a built-in or system font, or "Include Font Data" unticked —
                // its own names stand behind ours as before.
                TranslatorCore.LogDebug($"[GameFonts] '{gameFontName}': no font file in the game's data — its names stay its backup");
                return;
            }
            string dataFolder = _dataFolder, version = _unityVersion, cache = _cache;
            System.Threading.Tasks.Task.Run(() =>
            {
                string written = null;
                try
                {
                    var data = GameFonts.ReadData(dataFolder, font, version);
                    if (data == null) return;
                    Directory.CreateDirectory(cache);
                    written = Path.Combine(cache, DerivedFonts.Sanitized(gameFontName) + GameFonts.ExtensionOf(data));
                    File.WriteAllBytes(written, data);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is UnityFileFormatException)
                {
                    TranslatorCore.LogWarning($"[GameFonts] '{gameFontName}' could not be extracted as a backup: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                _onMain.Enqueue(() => Lend(gameFontName, written));
            });
        }

        /// <summary>Main thread: an extracted backup shown to the engine under a pool name, and the replacement told.</summary>
        private static void Lend(string gameFontName, string file)
        {
            var slot = DerivedFonts.LateCopy("game-" + gameFontName, file, out _);
            if (slot == null)
            {
                TranslatorCore.LogInfo($"[GameFonts] '{gameFontName}': extracted as a backup, shown to legacy text from the next launch");
                return;
            }
            _lent[gameFontName] = slot.Value.Family;
            TranslatorCore.LogInfo($"[GameFonts] '{gameFontName}': extracted as the backup of its replacement, shown as '{slot.Value.Family}'");
            FontManager.OnGameFontBackupReady(gameFontName);
        }

        /// <summary>Main thread, the scanner's tick: what the background reading gave back.</summary>
        internal static void Tick()
        {
            while (_onMain.TryDequeue(out var action)) action();
        }
    }
}
