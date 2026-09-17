using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What a translation says about itself beside its lines, and WHEN each of those facts moves —
    /// the moments of <c>common/spec/translation-file/moments.md</c>, as one class a check can
    /// replay on a real folder.
    ///
    /// Three facts and two files:
    /// · <see cref="SourceHash"/> (<c>_source.hash</c>) — the server version this machine has SEEN;
    /// · <see cref="SiteId"/> (<c>_source.site_id</c>) — which row on the site;
    /// · <see cref="LocalChanges"/> (<c>_local_changes</c>) — how many lines differ from the ancestor;
    /// · the ancestor (<c>translations.json.ancestor</c>) — what the two sides last AGREED on;
    /// · the Main's ancestor (<c>.mainancestor</c>, branches only) — the Main as last merged from.
    ///
    /// 🔴 **A moment is one call.** Until 2026-09-11 each moment was a dance of three or four
    /// statements spread over the callers — set the hash, set the id, write the ancestor, write
    /// the file — and the ORDER of those statements is what made the count in the file true or
    /// false (the ancestor must move before the file is written, because the count is measured
    /// against it). Two of the five roads had the order wrong at some point, and nothing could
    /// see it: the panels read the counter in memory. Here the order is inside the moment.
    ///
    /// ⚠ Pure by contract: Newtonsoft and the file system, no Unity, no clock, no static state.
    /// TranslatorCore keeps its old names as a façade over one instance; the callers' vocabulary
    /// has not moved. The settings that travel with an ancestor come and go as the raw sections
    /// (a JObject of <c>_fonts</c>, <c>_exclusions</c>…): building a TranslationSettings from them
    /// is the caller's, since that type reads the game's live state.
    ///
    /// ⚠ Throws where the original swallowed and logged (a file that will not read or write):
    /// the caller sits at the process boundary and logs; a check wants the throw.
    /// </summary>
    public sealed class TranslationStore
    {
        public string TranslationPath { get; }
        public string AncestorPath => TranslationFiles.AncestorOf(TranslationPath);
        public string MainAncestorPath => TranslationFiles.MainAncestorOf(TranslationPath);

        private readonly Action<string> _log;

        // ── Identity and stamps ────────────────────────────────────────────────

        /// <summary>The lineage. Generated once, shared by every copy; a fork takes a new one.</summary>
        public string Uuid { get; set; }

        /// <summary>The server version this machine has seen (downloaded, uploaded or merged from). Never prefixed.</summary>
        public string SourceHash { get; set; }

        /// <summary>The Main as it stood at the last merge from it (branches only).</summary>
        public string MainHash { get; set; }

        /// <summary>The row on the site; what lets a mod with nobody signed in ask the public check.</summary>
        public int? SiteId { get; set; }

        /// <summary>Where a fork came from, written once by <see cref="Fork"/>, never touched again.</summary>
        public int? ForkedFromSiteId { get; private set; }
        public string ForkedFromHash { get; private set; }
        public int? ForkedFromResolvedLines { get; private set; }
        public string ForkedFromContentHash { get; private set; }

        /// <summary>Lines that differ from the ancestor. Recounted at every write, never carried.</summary>
        public int LocalChanges { get; private set; }

        /// <summary>What the two sides last agreed on. Empty when there is no ancestor.</summary>
        public Dictionary<string, TranslationEntry> Ancestor { get; private set; } = new Dictionary<string, TranslationEntry>();

        /// <summary>
        /// The ancestor's settings sections as written beside its lines, or null when UNKNOWN — an
        /// ancestor written before settings travelled with it, or built from a source whose
        /// settings were never seen. Null is honest, not degraded: with no baseline the mod cannot
        /// tell who changed a section, so it asks instead of guessing.
        /// </summary>
        public JObject AncestorSettings { get; private set; }

        public TranslationStore(string translationPath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(translationPath))
                throw new ArgumentException("A translation path is required.", nameof(translationPath));
            TranslationPath = translationPath;
            _log = log;
        }

        // ── Load ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The identity and stamps AS THE FILE STATES THEM — a value for every field, absent read
        /// as null or zero, never the previous file's. That is the whole point of LoadedFile.
        /// </summary>
        public void TakeIdentity(LoadedFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            Uuid = file.Uuid;
            SourceHash = file.LastSyncedHash;
            MainHash = file.LastMergedMainHash;
            SiteId = file.SourceSiteId;
            ForkedFromSiteId = file.ForkedFromSiteId;
            ForkedFromHash = file.ForkedFromHash;
            ForkedFromResolvedLines = file.ForkedFromResolvedLines;
            ForkedFromContentHash = file.ForkedFromContentHash;
            LocalChanges = file.LocalChanges;
        }

        /// <summary>
        /// Reads the ancestor beside the translation. No file: an empty ancestor and unknown
        /// settings. A file: its lines, and its sections when it carries any.
        /// </summary>
        public void LoadAncestor()
        {
            if (!File.Exists(AncestorPath))
            {
                Ancestor = new Dictionary<string, TranslationEntry>();
                AncestorSettings = null;
                return;
            }

            var parsed = JObject.Parse(File.ReadAllText(AncestorPath).Replace("\r\n", "\n"));
            Ancestor = LinesOf(parsed);
            AncestorSettings = SectionsOf(parsed);
            _log?.Invoke($"[Store] Loaded {Ancestor.Count} ancestor entries for merge support");
        }

        /// <summary>The Main as last merged from, or empty when this file never merged from one.</summary>
        public Dictionary<string, TranslationEntry> ReadMainAncestor()
        {
            if (!File.Exists(MainAncestorPath)) return new Dictionary<string, TranslationEntry>();
            return LinesOf(JObject.Parse(File.ReadAllText(MainAncestorPath).Replace("\r\n", "\n")));
        }

        /// <summary>The Main's settings as last merged from, or null when unknown.</summary>
        public JObject ReadMainAncestorSettings()
        {
            if (!File.Exists(MainAncestorPath)) return null;
            return SectionsOf(JObject.Parse(File.ReadAllText(MainAncestorPath).Replace("\r\n", "\n")));
        }

        // ── The moments ────────────────────────────────────────────────────────

        /// <summary>
        /// After a download or a successful upload: the two sides agree on <paramref name="lines"/>
        /// exactly, so it is the ancestor, and nothing is left to publish.
        /// </summary>
        /// <param name="hash">The server's hash of what was taken or sent.</param>
        /// <param name="siteId">The row, when the caller knows it; null keeps the one already held.</param>
        /// <param name="lines">What this machine now holds, which is what the server holds.</param>
        /// <param name="settings">The settings sections that travel with it, or null when unknown.</param>
        public void NoteSynced(string hash, int? siteId, IDictionary<string, TranslationEntry> lines, JObject settings)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (!string.IsNullOrEmpty(hash)) SourceHash = hash;
            if (siteId.HasValue && siteId.Value > 0) SiteId = siteId;

            WriteAncestorFile(lines, settings);
            Ancestor = Copy(lines);
            AncestorSettings = settings;
            LocalChanges = 0;
            _log?.Invoke($"[Store] Saved ancestor with {Ancestor.Count} entries");
        }

        /// <summary>
        /// After a merge: three facts, and getting the other two wrong is worse than not merging.
        /// The hash is the PUBLISHED version's (the version now seen, whether or not all of it was
        /// kept); the ancestor is the PUBLISHED content, never the merged one (what the two sides
        /// last agreed on is what was published — the merged file as ancestor would make every line
        /// just kept look like common ground, and the next merge would drop them); the count is
        /// what the merged lines have that the published ones do not — what still needs publishing.
        /// </summary>
        /// <param name="published">The published lines; null when the caller has only the merged result, in which case the merged lines stand as the baseline.</param>
        /// <param name="publishedSettings">The published settings, only when actually seen — an invented baseline is worse than none.</param>
        public void NoteMerged(string publishedHash, IDictionary<string, TranslationEntry> published, JObject publishedSettings,
                               IDictionary<string, TranslationEntry> merged)
        {
            if (merged == null) throw new ArgumentNullException(nameof(merged));
            if (!string.IsNullOrEmpty(publishedHash)) SourceHash = publishedHash;

            var baseline = published ?? merged;
            WriteAncestorFile(baseline, publishedSettings);
            Ancestor = Copy(baseline);
            AncestorSettings = publishedSettings;
            Recount(merged);
            _log?.Invoke($"[Store] Saved ancestor from remote with {Ancestor.Count} entries");
        }

        /// <summary>
        /// After a branch merged from its Main: the Main goes into ITS ancestor, never into ours —
        /// the two baselines answer different questions and must not mix.
        /// </summary>
        public void NoteMainMerged(IDictionary<string, TranslationEntry> main, string mainHash, JObject mainSettings)
        {
            if (main == null) throw new ArgumentNullException(nameof(main));
            WriteFile(MainAncestorPath, main, mainSettings);
            MainHash = mainHash;
            _log?.Invoke($"[Store] Saved upstream ancestor with {main.Count} entries");
        }

        /// <summary>
        /// The file leaves its lineage. Where it came from is written down BEFORE the reset wipes
        /// the sync state; the count is what was actually received, measured now because the
        /// original goes on growing. Then a new uuid, no server, no Main, every line local — and
        /// both ancestors deleted: a lineage left has no common ground with the one joined.
        /// </summary>
        /// <returns>How many ancestor files actually went.</returns>
        public int Fork(string newUuid, int resolvedLines, string contentHash, int lineCount)
        {
            if (string.IsNullOrEmpty(newUuid)) throw new ArgumentException("A new uuid is required.", nameof(newUuid));

            ForkedFromSiteId = SiteId;
            ForkedFromHash = SourceHash;
            ForkedFromResolvedLines = resolvedLines;
            ForkedFromContentHash = contentHash;

            Uuid = newUuid;
            SourceHash = null;
            MainHash = null;
            SiteId = null;
            LocalChanges = lineCount;

            Ancestor = new Dictionary<string, TranslationEntry>();
            AncestorSettings = null;
            return CompanionFiles.DeleteAncestors(TranslationPath);
        }

        /// <summary>
        /// One more line changed since the ancestor, counted as the edit lands — the running
        /// figure the screens show between two writes. The file never trusts it: <see cref="Recount"/>.
        /// </summary>
        public void NoteLocalEdit(string key, TranslationEntry entry)
        {
            if (Ancestor.Count == 0)
            {
                LocalChanges++;
                return;
            }
            if (!Ancestor.TryGetValue(key, out var ancestorEntry)
                || ancestorEntry.Value != entry.Value
                || ancestorEntry.Tag != entry.Tag)
            {
                LocalChanges++;
            }
        }

        /// <summary>
        /// Lines that differ from the ancestor, counted rather than trusted. No ancestor: every
        /// line is local. A line the ancestor had and these no longer do counts too — walking only
        /// the local lines made deletions invisible, and the divergence read as a SERVER update
        /// that would have restored what the player deleted. Except an interface line the server's
        /// copy holds and this file no longer does: taking those out is this version doing what it
        /// must, not work waiting to be shared.
        /// </summary>
        public int Recount(IDictionary<string, TranslationEntry> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));

            if (Ancestor.Count == 0)
            {
                LocalChanges = lines.Count;
                return LocalChanges;
            }

            int changes = 0;
            foreach (var kvp in lines)
            {
                if (kvp.Key.StartsWith("_")) continue;
                if (!Ancestor.TryGetValue(kvp.Key, out var ancestorEntry)
                    || ancestorEntry.Value != kvp.Value.Value
                    || ancestorEntry.Tag != kvp.Value.Tag)
                {
                    changes++;
                }
            }

            int removed = 0;
            foreach (var kvp in Ancestor)
            {
                if (kvp.Key.StartsWith("_")) continue;
                if (!Common.Merge.IsGameLine(kvp.Value?.Tag)) continue;
                if (!lines.ContainsKey(kvp.Key)) removed++;
            }
            changes += removed;

            LocalChanges = changes;
            _log?.Invoke($"[LocalChanges] Recalculated: {changes} local changes ({removed} deleted)");
            return changes;
        }

        /// <summary>
        /// The stamps as the file carries them: <c>_source</c> when there is anything to say,
        /// <c>_forked_from</c> for a fork, <c>_local_changes</c> only when non-zero. The caller
        /// recounts first (SaveCache does), so what the file says is true of the file.
        /// </summary>
        public void WriteStampsInto(JObject output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            if (!string.IsNullOrEmpty(SourceHash) || !string.IsNullOrEmpty(MainHash) || SiteId.HasValue)
            {
                var source = new JObject();
                if (!string.IsNullOrEmpty(SourceHash)) source["hash"] = SourceHash;
                if (!string.IsNullOrEmpty(MainHash)) source["main_hash"] = MainHash;
                if (SiteId.HasValue) source["site_id"] = SiteId.Value;
                output["_source"] = source;
            }

            // Separate from _source, which an older version reads and rewrites: this block is
            // unknown to it and would be dropped at its next save — a loss of credit, never a
            // breakage. It stays out of the content hash, so two installs never disagree about
            // whether they hold the same file over it.
            if (ForkedFromSiteId.HasValue)
            {
                var origin = new JObject();
                origin["site_id"] = ForkedFromSiteId.Value;
                if (!string.IsNullOrEmpty(ForkedFromHash)) origin["hash"] = ForkedFromHash;
                if (ForkedFromResolvedLines.HasValue) origin["resolved_lines"] = ForkedFromResolvedLines.Value;
                if (!string.IsNullOrEmpty(ForkedFromContentHash)) origin["content_hash"] = ForkedFromContentHash;
                output["_forked_from"] = origin;
            }

            if (LocalChanges > 0)
                output["_local_changes"] = LocalChanges;
        }

        // ── Files ──────────────────────────────────────────────────────────────

        private void WriteAncestorFile(IDictionary<string, TranslationEntry> lines, JObject settings)
            => WriteFile(AncestorPath, lines, settings);

        private static void WriteFile(string path, IDictionary<string, TranslationEntry> lines, JObject settings)
        {
            var output = new JObject();
            foreach (var kvp in lines)
            {
                if (kvp.Key.StartsWith("_")) continue;
                output[kvp.Key] = new JObject
                {
                    ["v"] = kvp.Value.Value,
                    ["t"] = kvp.Value.Tag ?? "A"
                };
            }

            // Only what was actually seen goes beside the lines: without the settings there is no
            // way to tell "the other side changed this" from "I changed this", and with invented
            // ones the next comparison would trust a baseline that never existed.
            if (settings != null)
            {
                foreach (var prop in settings.Properties())
                    output[prop.Name] = prop.Value.DeepClone();
            }

            AtomicFile.WriteAllText(path, output.ToString(Formatting.Indented));
        }

        /// <summary>The lines of an ancestor file: the `{v, t}` form, and the bare-string form older files carry.</summary>
        private static Dictionary<string, TranslationEntry> LinesOf(JObject parsed)
        {
            var result = new Dictionary<string, TranslationEntry>();
            foreach (var prop in parsed.Properties())
            {
                if (prop.Name.StartsWith("_")) continue;
                string key = TextNormalization.NormalizeLineEndings(prop.Name);

                if (prop.Value.Type == JTokenType.Object)
                {
                    var obj = prop.Value as JObject;
                    result[key] = new TranslationEntry
                    {
                        Value = TextNormalization.NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                        Tag = obj?["t"]?.ToString() ?? "A"
                    };
                }
                else if (prop.Value.Type == JTokenType.String)
                {
                    result[key] = new TranslationEntry
                    {
                        Value = TextNormalization.NormalizeLineEndings(prop.Value.ToString()),
                        Tag = "A"
                    };
                }
            }
            return result;
        }

        /// <summary>The `_` sections of a file, or null when it carries none.</summary>
        private static JObject SectionsOf(JObject parsed)
        {
            JObject sections = null;
            foreach (var prop in parsed.Properties())
            {
                if (!prop.Name.StartsWith("_")) continue;
                if (sections == null) sections = new JObject();
                sections[prop.Name] = prop.Value.DeepClone();
            }
            return sections;
        }

        private static Dictionary<string, TranslationEntry> Copy(IDictionary<string, TranslationEntry> lines)
        {
            var copy = new Dictionary<string, TranslationEntry>();
            foreach (var kvp in lines)
            {
                if (kvp.Key.StartsWith("_")) continue;
                copy[kvp.Key] = new TranslationEntry { Value = kvp.Value.Value, Tag = kvp.Value.Tag };
            }
            return copy;
        }
    }
}
