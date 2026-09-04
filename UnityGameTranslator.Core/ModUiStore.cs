using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The mod's own interface, on disk: reading it, writing it, and putting it away when the game
    /// changes language.
    ///
    /// 🔴 **Here rather than inside TranslatorCore because THIS is the part that lost data.** The
    /// rules about which line goes where are pure and were checkable from the first day
    /// (<see cref="ModUiMigration"/>); what went wrong was neither of them — it was a right rule
    /// firing at the wrong MOMENT. A save flushed before the file had been read replaced somebody's
    /// interface with an empty one, and nothing threw, nothing logged, and the result looks exactly
    /// like an interface nobody has translated yet.
    ///
    /// That class of defect only shows in a SEQUENCE — load, change, reload, save — so the sequence
    /// has to be reachable without a game. TranslatorCore references UnityEngine and can never be
    /// linked into a console project; this file must therefore stay free of it, exactly like the
    /// pure rule files. See tests/UnityGameTranslator.Core.Checks/ModUiStoreChecks.cs.
    ///
    /// ⚠ It owns its own "has it been read" flag, deliberately. An invariant enforced at the door
    /// where the damage happens does not depend on every caller remembering it.
    /// </summary>
    public sealed class ModUiStore
    {
        /// <summary>The interface lines, keyed by their English source.</summary>
        public Dictionary<string, TranslationEntry> Entries { get; private set; }
            = new Dictionary<string, TranslationEntry>();

        /// <summary>The language the file states for itself, or null when it states none.</summary>
        public string Language { get; private set; }

        /// <summary>The font the file asks for, or null.</summary>
        public string Font { get; private set; }

        /// <summary>
        /// Whether the file has been read this session — the guard on every write.
        ///
        /// 🔴 An empty store is indistinguishable from an interface nobody has translated, so a
        /// write before a read destroys the file silently. Lowered for the whole of a read, not
        /// only the first: a RELOAD that fails to parse would otherwise leave an empty store behind
        /// a flag still claiming to reflect the disk.
        /// </summary>
        public bool Loaded { get; private set; }

        /// <summary>Something changed that the file does not yet hold.</summary>
        public bool Modified { get; set; }

        /// <summary>What a caller wants said out loud. Never null in practice; guarded anyway.</summary>
        public Action<string> Info { get; set; }

        /// <inheritdoc cref="Info"/>
        public Action<string> Warn { get; set; }

        private const int EngineVersion = 1;

        private void Say(Action<string> to, string what) { if (to != null) to(what); }

        /// <summary>
        /// Two languages are the same one exactly when they would be set aside under the same name
        /// — the identity the file names already use, asked once rather than spelled a second way.
        /// </summary>
        public static bool SameLanguage(string a, string b) =>
            string.Equals(ModUi.SetAsideFileName(a), ModUi.SetAsideFileName(b),
                          StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Read the interface for the language this game is being played in.
        ///
        /// 🔴 **A file in another language is put away, never overwritten and never used.** An
        /// interface translated into French is noise in a Korean game, and deleting it would throw
        /// away a pass of the translator for somebody trying a language for an evening. Coming back
        /// to that language finds it again, because the set-aside name is derived and not remembered.
        /// </summary>
        /// <param name="folder">Where the mod keeps this game's data.</param>
        /// <param name="wantedLanguage">The language in force. Null or "auto" leaves the file alone.</param>
        public void Load(string folder, string wantedLanguage)
        {
            Entries = new Dictionary<string, TranslationEntry>();
            Language = null;
            Font = null;
            Modified = false;
            Loaded = false;

            if (string.IsNullOrEmpty(folder)) return;

            string path = Path.Combine(folder, ModUi.FileName);

            try
            {
                // The file in place belongs to another language: put it away, then look for the one
                // that was put away for THIS language.
                if (File.Exists(path))
                {
                    string held = LanguageOf(path);
                    if (Languages.IsSettled(held) && Languages.IsSettled(wantedLanguage)
                        && !SameLanguage(held, wantedLanguage))
                    {
                        SetAside(folder, held);
                    }
                }

                if (!File.Exists(path) && Languages.IsSettled(wantedLanguage))
                {
                    string putAway = Path.Combine(folder, ModUi.SetAsideFileName(wantedLanguage));
                    if (File.Exists(putAway))
                    {
                        File.Move(putAway, path);
                        Say(Info, $"[ModUI] Took back the interface already translated into {wantedLanguage}");
                    }
                }

                // No file: an empty store IS the truth, and writing one destroys nothing.
                if (!File.Exists(path)) { Loaded = true; return; }

                var parsed = JObject.Parse(File.ReadAllText(path).Replace("\r\n", "\n"));

                foreach (var prop in parsed.Properties())
                {
                    if (prop.Name == "_target_language")
                    {
                        string stated = prop.Value.ToString();
                        if (Languages.IsSettled(stated)) Language = stated;
                    }
                    else if (prop.Name == "_settings" && prop.Value.Type == JTokenType.Object)
                    {
                        Font = (prop.Value as JObject)?["ui_font"]?.Value<string>();
                    }
                    else if (!prop.Name.StartsWith("_"))
                    {
                        string key = Normalize(prop.Name);
                        if (prop.Value.Type == JTokenType.Object)
                        {
                            var obj = prop.Value as JObject;
                            Entries[key] = new TranslationEntry
                            {
                                Value = Normalize(obj?["v"]?.ToString() ?? ""),
                                Tag = obj?["t"]?.ToString() ?? ModUi.Tag,
                                Index = ReadIndex(obj?["i"]),
                            };
                        }
                        else if (prop.Value.Type == JTokenType.String)
                        {
                            Entries[key] = new TranslationEntry
                            {
                                Value = Normalize(prop.Value.ToString()),
                                Tag = ModUi.Tag,
                            };
                        }
                    }
                }

                // Read in full: the store reflects the file, so writing it back cannot lose
                // anything. Deliberately NOT set when the parse threw — an unreadable file is still
                // somebody's work, and overwriting it with an empty one cannot be undone.
                Loaded = true;
            }
            catch (Exception e)
            {
                // Loud, and empty rather than half-read: a partially parsed interface would show
                // some labels translated and some not, which reads as a bug in the mod.
                Say(Warn, $"[ModUI] Failed to read {ModUi.FileName}: {e.Message}");
                Entries = new Dictionary<string, TranslationEntry>();
                Language = null;
                Font = null;
            }
        }

        /// <summary>
        /// Take a font over as the one this interface asks for.
        ///
        /// ⚠ One caller: the migration lifting `_settings.ui_font` out of a game translation, where
        /// it never belonged. It is not a setter for general use — the font written at every save is
        /// the one in FORCE, which the caller passes to <see cref="Save"/>; this only gives the file
        /// something to state when nothing else does.
        /// </summary>
        public void AdoptFont(string font)
        {
            if (string.IsNullOrEmpty(font)) return;
            Font = font;
            Modified = true;
        }

        /// <summary>The `_target_language` of a file, without loading it. Null when it says nothing.</summary>
        public string LanguageOf(string path)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(path).Replace("\r\n", "\n"))["_target_language"]?.ToString();
            }
            catch (Exception e)
            {
                Say(Warn, $"[ModUI] Could not read the language of {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Move the interface file out of the way, under the language it holds.
        ///
        /// ⚠ An existing file for that language is replaced, and that is right: both are the same
        /// interface in the same language, and the one being put away is the later of the two.
        /// </summary>
        public void SetAside(string folder, string language)
        {
            string path = Path.Combine(folder, ModUi.FileName);
            try
            {
                string target = Path.Combine(folder, ModUi.SetAsideFileName(language));
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
                Say(Info, $"[ModUI] Set aside the interface translated into {language} as {Path.GetFileName(target)}");
            }
            catch (Exception e)
            {
                // Nothing is lost — the file is still there — but it is in the wrong language, so
                // it must not be read. Said out loud rather than swallowed.
                Say(Warn, $"[ModUI] Could not set aside {ModUi.FileName} ({e.Message}); it will not be used.");
            }
        }

        /// <summary>
        /// Write the interface file. Same shape as translations.json, minus everything that
        /// describes a GAME translation: no uuid, no game, no sync state, no local-change count —
        /// this file is never published, so none of that has an answer here.
        /// </summary>
        /// <returns>Whether anything was written.</returns>
        public bool Save(string folder, string language, string font)
        {
            if (string.IsNullOrEmpty(folder)) return false;

            // 🔴 Never write what was never read. See Loaded.
            if (!Loaded)
            {
                Say(Warn, "[ModUI] Refused to write the interface file before reading it.");
                return false;
            }

            try
            {
                var output = new JObject();
                output["_engine_version"] = EngineVersion;

                // What this interface is written in, recorded at every save from the language in
                // force, so a file copied to another game announces itself.
                if (Languages.IsSettled(language))
                {
                    Language = language;
                    output["_target_language"] = language;
                }

                // The font that makes it readable, so it travels with the copy.
                if (!string.IsNullOrEmpty(font))
                {
                    Font = font;
                    output["_settings"] = new JObject { ["ui_font"] = font };
                }

                foreach (var key in Entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var entry = Entries[key];
                    var obj = new JObject
                    {
                        ["v"] = entry.Value,
                        ["t"] = entry.Tag ?? ModUi.Tag,
                    };
                    if (entry.Index.HasValue) obj["i"] = entry.Index.Value;
                    output[key] = obj;
                }

                File.WriteAllText(Path.Combine(folder, ModUi.FileName), output.ToString(Formatting.Indented));
                Modified = false;
                return true;
            }
            catch (Exception e)
            {
                Say(Warn, $"[ModUI] Failed to save {ModUi.FileName}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Write only when the file does not already hold what we have.
        ///
        /// ⚠ **The font counts as a change, and asking here is what makes that reliable.** It is
        /// not in the dictionary, so nothing marks the store dirty when somebody picks one — the
        /// file kept the previous font until a new line happened to be translated, which on a
        /// finished interface is never. Derived rather than signalled: a flag one caller sets is a
        /// flag the next caller forgets.
        /// </summary>
        public bool SaveIfDirty(string folder, string language, string font)
        {
            bool fontMoved = !string.IsNullOrEmpty(font)
                             && !string.Equals(font, Font, StringComparison.OrdinalIgnoreCase);

            if (!Modified && !fontMoved) return false;

            return Save(folder, language, font);
        }

        // ── Shapes shared with the game's file ────────────────────────────────
        // Kept here rather than called on TranslatorCore: this file must stay linkable without
        // Unity, and both are three lines.

        private static string Normalize(string text) =>
            text == null ? null : text.Replace("\r\n", "\n");

        private static long? ReadIndex(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer) return null;
            try
            {
                long value = token.Value<long>();
                return value >= 1 ? value : (long?)null;
            }
            catch
            {
                return null;
            }
        }
    }
}
