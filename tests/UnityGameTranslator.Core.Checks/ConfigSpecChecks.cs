using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's reading of config.json against the contract: <c>common/spec/config/cases.json</c>,
    /// the files an older build wrote and what a current one derives from each.
    ///
    /// 🔴 **The cases are the specification, not the C#.** The Manager reads the same file for its
    /// half (what it shows of a game's setup), the schema for the written form. A migration that
    /// derives something else from an old file than the case says is wrong even if it has always
    /// done so — and then the case, or the migration, is changed on purpose.
    ///
    /// ⚠ Read exactly as the mod reads: Newtonsoft over the text (the migrations hang off
    /// [OnDeserialized]), then <see cref="ModConfig.CompleteSyncFromRaw"/> over the raw json, as
    /// LoadConfig does. `input.system_language` stands in for what the machine would answer.
    /// </summary>
    internal static class ConfigSpecChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string casesPath = Find("common", "spec", "config", "cases.json");
            check(casesPath != null, "the config contract's cases are found",
                "this check reads them; without them, it proves nothing");
            if (casesPath == null) return;

            var doc = JObject.Parse(File.ReadAllText(casesPath));
            int withRead = 0;

            foreach (JObject c in (JArray)doc["cases"])
            {
                string id = (string)c["id"];
                var read = c["read"];
                if (read == null) continue;
                withRead++;
                string why = (string)c["why"];

                string text = c["raw"] != null
                    ? (string)c["raw"]
                    : c["document"].ToString(Formatting.None);

                string systemLanguage = (string)(c["input"] as JObject)?["system_language"] ?? "English";
                ModConfig.SystemLanguage = () => systemLanguage;

                if (read.Type == JTokenType.String && (string)read == "throws")
                {
                    bool threw = false;
                    try { ReadAsTheModDoes(text); } catch (Exception) { threw = true; }
                    check(threw, id, why);
                    continue;
                }

                ModConfig config;
                try { config = ReadAsTheModDoes(text); }
                catch (Exception e)
                {
                    check(false, id, $"the reader threw {e.GetType().Name}: {e.Message} — {why}");
                    continue;
                }

                var derived = Derive(config);
                var failures = new List<string>();
                foreach (var expectation in (JObject)read)
                {
                    if (!derived.TryGetValue(expectation.Key, out var actual))
                    {
                        failures.Add($"{expectation.Key}: not a value this side derives");
                        continue;
                    }
                    string detail = Agrees(expectation.Value, actual);
                    if (detail != null) failures.Add($"{expectation.Key}: {detail}");
                }

                check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
            }

            check(withRead >= 20, $"{withRead} cases carry a reading for this side",
                "fewer than the contract holds: the file was mis-read");
        }

        /// <summary>Newtonsoft over the text, then the raw-json migrations — LoadConfig's order.</summary>
        private static ModConfig ReadAsTheModDoes(string text)
        {
            var config = JsonConvert.DeserializeObject<ModConfig>(text);
            if (config == null) throw new JsonException("not an object");
            config.CompleteSyncFromRaw(JObject.Parse(text));
            return config;
        }

        private static Dictionary<string, object> Derive(ModConfig c)
        {
            return new Dictionary<string, object>
            {
                ["translation_backend"] = c.translation_backend,
                ["ai_url"] = c.ai_url,
                ["ai_model"] = c.ai_model,
                ["target_language"] = c.target_language,
                ["source_language"] = c.source_language,
                ["TargetLanguage"] = c.GetTargetLanguage(),
                ["SourceLanguage"] = c.GetSourceLanguage(),
                ["enable_ai"] = c.enable_ai,
                ["debug_ai"] = c.debug_ai,
                ["timeout_ms"] = c.timeout_ms,
                ["config_version"] = c.config_version,
                ["translate_mod_ui"] = c.translate_mod_ui,
                ["ai_max_attempts"] = c.ai_max_attempts,
                ["AttemptsAllowed"] = c.AttemptsAllowed,
                ["TemperatureNormal"] = c.TemperatureNormal,
                ["TemperatureRepair"] = c.TemperatureRepair,
                ["TemperatureRetranslate"] = c.TemperatureRetranslate,
                ["IsTranslationEnabled"] = c.IsTranslationEnabled,
                ["ActiveBackendRequiresOnline"] = c.ActiveBackendRequiresOnline,
                ["settings_hotkey"] = c.settings_hotkey,
                ["api_token"] = c.api_token,
                ["api_user"] = c.api_user,
                ["deepl_api_key"] = c.deepl_api_key,
                ["proxy_mode"] = c.proxy_mode,
                ["sync.update_check_frequency"] = c.sync?.update_check_frequency,
                ["sync.realtime_own_translation"] = c.sync?.realtime_own_translation,
                ["sync.check_update_on_start"] = c.sync?.check_update_on_start,
                ["sync.notify_prereleases"] = c.sync?.notify_prereleases,
                ["sync.merge_strategy"] = c.sync?.merge_strategy,
            };
        }

        private static string Agrees(JToken expected, object actual)
        {
            switch (expected.Type)
            {
                case JTokenType.Null: return actual == null ? null : $"expected null, got {Show(actual)}";
                case JTokenType.Boolean: return Equals(actual, (bool)expected) ? null : $"expected {expected}, got {Show(actual)}";
                case JTokenType.Integer: return actual != null && !(actual is bool) && !(actual is string) && Convert.ToInt64(actual) == (long)expected ? null : $"expected {expected}, got {Show(actual)}";
                case JTokenType.Float: return actual != null && !(actual is bool) && !(actual is string) && Math.Abs(Convert.ToDouble(actual) - (double)expected) < 1e-6 ? null : $"expected {expected}, got {Show(actual)}";
                default: return string.Equals(actual as string, (string)expected, StringComparison.Ordinal) ? null : $"expected \"{expected}\", got {Show(actual)}";
            }
        }

        private static string Show(object value) => value == null ? "null" : $"\"{value}\"";

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
