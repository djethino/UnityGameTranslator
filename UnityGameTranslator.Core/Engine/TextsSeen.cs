using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// The text systems this game has actually shown while the mod ran, kept in
    /// <see cref="TextSystems.FileName"/> beside config.json for UGT Manager to read (format:
    /// common/spec/texts-seen). What the game merely CONTAINS is the Manager's own reading of its
    /// files; only what goes through the mod's roads is written here — see <see cref="TextSystems"/>.
    ///
    /// ⚠ A set that only grows, loaded once and rewritten only when it grows: a game meets its
    /// handful of systems in its first minutes, so the file is written a few times in its life and
    /// the check on every text costs one set lookup.
    /// ⚠ Main thread only, like the patches and the scanner that feed it.
    /// </summary>
    internal static class TextsSeen
    {
        private static HashSet<TextSystem> _systems;
        private static HashSet<string> _other;
        // Words a newer mod wrote into the file, kept as they are when this one rewrites it.
        private static List<string> _unknown;

        /// <summary>Has this system already been recorded? The cheap question asked first.</summary>
        internal static bool Has(TextSystem system)
        {
            Load();
            return _systems.Contains(system);
        }

        internal static void Note(TextSystem system)
        {
            Load();
            if (_systems.Add(system)) Save();
        }

        /// <summary>A generic text component: its system when known by name, else its name.</summary>
        internal static void NoteGeneric(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            var known = TextSystems.FromTypeName(typeName);
            if (known.HasValue) { Note(known.Value); return; }
            Load();
            if (_other.Add(typeName)) Save();
        }

        private static void Load()
        {
            if (_systems != null) return;
            _systems = new HashSet<TextSystem>();
            _other = new HashSet<string>(StringComparer.Ordinal);
            _unknown = new List<string>();

            string path = PathOf();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                if (root["systems"] is JArray systems)
                    foreach (var t in systems)
                    {
                        string word = (string)t;
                        if (TextSystems.TryParse(word, out var s)) _systems.Add(s);
                        else if (!string.IsNullOrEmpty(word) && !_unknown.Contains(word)) _unknown.Add(word);
                    }
                if (root["other"] is JArray other)
                    foreach (var t in other)
                        if (!string.IsNullOrEmpty((string)t)) _other.Add((string)t);
            }
            catch (Exception ex)
            {
                // Rewritten from what this session meets: the file only ever says what was seen.
                TranslatorCore.LogWarning($"[TextsSeen] {TextSystems.FileName} unreadable, starting over: {ex.Message}");
            }
        }

        private static void Save()
        {
            string path = PathOf();
            if (path == null) return;
            try
            {
                var systems = new JArray();
                foreach (TextSystem s in Enum.GetValues(typeof(TextSystem)))
                    if (_systems.Contains(s)) systems.Add(TextSystems.Word(s));
                foreach (var word in _unknown) systems.Add(word);

                var root = new JObject
                {
                    ["format"] = TextSystems.Format,
                    ["systems"] = systems,
                };
                if (_other.Count > 0)
                {
                    var other = new List<string>(_other);
                    other.Sort(StringComparer.Ordinal);
                    root["other"] = new JArray(other);
                }
                root["mod_version"] = PluginInfo.Version;

                File.WriteAllText(path, root.ToString());
                TranslatorCore.LogDebug($"[TextsSeen] now: {TextSystems.Describe(_systems, _other, _unknown)}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextsSeen] could not write {TextSystems.FileName}: {ex.Message}");
            }
        }

        private static string PathOf()
        {
            string folder = TranslatorCore.ModFolder;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, TextSystems.FileName);
        }
    }
}
