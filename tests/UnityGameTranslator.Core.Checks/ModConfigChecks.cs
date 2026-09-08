using System;
using Newtonsoft.Json;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The config.json contract: what a file written by an older build still means, and what a
    /// value nobody should have written is read as.
    ///
    /// 🔴 **This is a format shared between two programs.** The mod reads and writes it; the
    /// Manager reads the same file to show and change a game's setup. So a key here is not a
    /// preference, it is an agreement — and a migration is what keeps a file written a year ago
    /// meaning what its owner meant.
    ///
    /// 🔴 **The failure mode is silent and it takes somebody's choice away.** A retired value read
    /// as the default undoes a decision they made, on a screen that then shows the default as
    /// though they had chosen it. Nothing throws, nothing is logged, and the only evidence is a
    /// setting that quietly moved. That is why the retired values are still named in the code, and
    /// why they are checked here.
    ///
    /// ⚠ The migrations run through Newtonsoft, on real JSON, because that is where they run: they
    /// hang off [OnDeserialized] and [JsonExtensionData], and calling them any other way would
    /// check a method rather than the behaviour a file gets.
    /// </summary>
    internal static class ModConfigChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            HowOftenToLook(check);
            WhatAnOldFileStillMeans(check);
            WhatAValueOutOfRangeBecomes(check);
            WhichLanguageIsAimedAt(check);
            WhatIsWrittenBack(check);
        }

        private static ModConfig Read(string json)
        {
            return JsonConvert.DeserializeObject<ModConfig>(json);
        }

        private static void HowOftenToLook(Action<bool, string, string> check)
        {
            check(UpdateCheckFrequency.IntervalSeconds("1h") == 3600f
                  && UpdateCheckFrequency.IntervalSeconds("3h") == 10800f
                  && UpdateCheckFrequency.IntervalSeconds("6h") == 21600f,
                "a rhythm is the number of seconds it says",
                "one wrong factor is a game asking the site sixty times more often than somebody agreed to");

            check(UpdateCheckFrequency.IntervalSeconds("never") == 0f
                  && UpdateCheckFrequency.IntervalSeconds("startup") == 0f,
                "and the two that do not repeat are zero",
                "zero is what the caller reads as 'do not schedule anything', so a stray value here starts a loop nobody asked for");

            // 🔴 The three retired values. They are still in config.json on every machine the mod
            // has ever run on, and reading one as the default is a decision taken away in silence.
            check(UpdateCheckFrequency.Normalize("30m") == "1h"
                  && UpdateCheckFrequency.Normalize("auto") == "1h"
                  && UpdateCheckFrequency.Normalize("realtime") == "1h",
                "a retired value is still read, and mapped",
                "deleting them outright would read them as the default: somebody's choice undone with nothing on screen to say so");

            check(UpdateCheckFrequency.Normalize("never") == "never"
                  && UpdateCheckFrequency.Normalize("startup") == "startup",
                "a value that still exists is left alone",
                "including 'never' — somebody who asked for silence must keep it whole");

            // ⚠ Unknown falls to hourly, NOT to never. A typo or a value from a newer build must
            // not silently stop somebody hearing about an update.
            check(UpdateCheckFrequency.Normalize("weekly") == "1h"
                  && UpdateCheckFrequency.Normalize("") == "1h"
                  && UpdateCheckFrequency.Normalize(null) == "1h",
                "and anything unrecognised falls to hourly, never to silence",
                "a typo, or a value written by a newer version, must not quietly stop the updates for good");

            check(UpdateCheckFrequency.AskedForRealtime("auto")
                  && UpdateCheckFrequency.AskedForRealtime("realtime"),
                "only the two values that opened a stream asked for one",
                "read once to fill the new setting from an existing file");

            check(!UpdateCheckFrequency.AskedForRealtime("1h")
                  && !UpdateCheckFrequency.AskedForRealtime("never")
                  && !UpdateCheckFrequency.AskedForRealtime(null),
                "and no other value did",
                "reading one as a yes hands somebody a permanent connection they had declined");
        }

        private static void WhatAnOldFileStillMeans(Action<bool, string, string> check)
        {
            // The names before the mod spoke to anything but Ollama.
            var ollama = Read(@"{""ollama_url"":""http://127.0.0.1:11434"",""enable_ollama"":true,
                                 ""debug_ollama"":true,""model"":""qwen2.5""}");

            check(ollama.ai_url == "http://127.0.0.1:11434" && ollama.enable_ai
                  && ollama.debug_ai && ollama.ai_model == "qwen2.5",
                "the keys from before any backend but one still carry over",
                "otherwise a working setup comes back empty, and it reads as the update having broken it");

            check(ollama.translation_backend == "llm",
                "and a file that had the model on picks the backend it meant",
                "the backend field did not exist then; 'none' would leave a configured setup switched off");

            // 🔴 v2. enable_ai stopped meaning "the backend is llm" and started meaning "translate".
            var google = Read(@"{""translation_backend"":""google"",""enable_ai"":false,""config_version"":1}");
            check(google.enable_ai,
                "a Google or DeepL setup written before v2 is read as switched ON",
                "the flag was kept in sync with the backend, never asked about: reading it as off stops translating for every one of them with nothing to explain it");

            var llmOff = Read(@"{""translation_backend"":""llm"",""enable_ai"":false,""config_version"":1}");
            check(!llmOff.enable_ai,
                "but an llm setup switched off stays off",
                "there, false already meant off under both readings — turning it on resumes a translation somebody stopped on purpose");

            // 🔴 v1. translate_mod_ui became tri-state, and the explicit false was serialised by
            // every build before that.
            var oldUi = Read(@"{""translate_mod_ui"":false,""config_version"":0}");
            check(oldUi.translate_mod_ui == null,
                "an interface flag written before it was a choice goes back to undecided",
                "the option did nothing at all until then; reading those as a refusal hides the feature from everyone who upgrades");

            var decided = Read(@"{""translate_mod_ui"":false,""config_version"":2}");
            check(decided.translate_mod_ui == false,
                "and one written since is honoured",
                "the version guard is what makes the migration run once instead of overriding a decision for ever");

            check(Read(@"{""config_version"":0}").config_version == 2,
                "a migrated file records that it was migrated",
                "without it every one of these runs again at the next launch, over values somebody has since set");

            // ⚠ An empty file is defaults PLUS the stamp — and the stamp is the point. Every
            // migration above is a no-op on it (nothing to repair), but leaving config_version at
            // 0 would make all of them run again at the next launch, over values somebody has set
            // in between. Measured rather than assumed: this case first asserted the opposite.
            var blank = Read("{}");
            check(blank.translation_backend == "none" && blank.translate_mod_ui == null
                  && !blank.enable_ai && blank.config_version == 2,
                "an empty file is defaults, stamped with the current version",
                "the migrations find nothing to repair; what the stamp buys is that they never run again over what is set afterwards");
        }

        private static void WhatAValueOutOfRangeBecomes(Action<bool, string, string> check)
        {
            var config = new ModConfig();

            config.ai_max_attempts = 0;
            check(config.AttemptsAllowed == 1,
                "no attempt at all is still one attempt",
                "zero would mean a line is never sent and never translated, which no screen offers");

            config.ai_max_attempts = 500;
            check(config.AttemptsAllowed == 10,
                "and five hundred is ten",
                "every unit above one is a real request to a real backend, paid in time and possibly in money");

            config.ai_temperature = -1.0;
            check(config.TemperatureNormal == 0.0,
                "a temperature below zero is zero",
                "a server refuses it outright, so the line is not translated and the refusal reads as the model being wrong");

            config.ai_temperature = double.NaN;
            check(config.TemperatureNormal == 0.0,
                "and so is one that is not a number",
                "a hand-edited file can hold anything, and NaN serialised into a request is a malformed request");

            config.ai_temperature_retranslate = 7.0;
            check(config.TemperatureRetranslate == 2.0,
                "two is the ceiling",
                "what an OpenAI-compatible server accepts, and the retranslate default sits highest so it is the one that reaches it");

            check(new ModConfig().TemperatureNormal == 0.0,
                "and an ordinary translation is deterministic by default",
                "the answer is cached, shared through the website and merged: two runs disagreeing would surface as a conflict nobody made");
        }

        private static void WhichLanguageIsAimedAt(Action<bool, string, string> check)
        {
            var previous = ModConfig.SystemLanguage;
            try
            {
                ModConfig.SystemLanguage = () => "German";
                var config = new ModConfig();

                check(config.target_language == "auto" && config.GetTargetLanguage() == "German",
                    "a target left at 'auto' asks the machine",
                    "it is a MODE, not a value: the same file aims elsewhere on the next machine");

                config.target_language = "French";
                check(config.GetTargetLanguage() == "French",
                    "and one that was settled does not",
                    "a value chosen or published must never be re-resolved against whoever happens to be reading");

                check(config.GetSourceLanguage() == null,
                    "an unset source stays unset",
                    "'auto' there means DETECT, a working mode with no value to hand a model — inventing one would say the wrong thing about the text");

                config.source_language = "English";
                check(config.GetSourceLanguage() == "English",
                    "and a declared one is given",
                    "this is what tells the model what it is translating FROM, and a downloaded file often has none");

                ModConfig.SystemLanguage = null;
                config.target_language = "auto";
                check(config.GetTargetLanguage() == null,
                    "with no machine to ask, 'auto' answers nothing",
                    "null is honest — the alternative is a hard-coded language quietly aimed at by everyone the host never told");
            }
            finally
            {
                ModConfig.SystemLanguage = previous;
            }
        }

        private static void WhatIsWrittenBack(Action<bool, string, string> check)
        {
            // 🔴 A secret goes to disk protected and comes back usable, or somebody is signed out
            // for no reason they can see.
            var config = new ModConfig { api_token = "ugt_abcdef0123456789" };
            string json = JsonConvert.SerializeObject(config);

            check(!json.Contains("ugt_abcdef0123456789"),
                "a token is not written in the clear",
                "config.json travels: support threads, cloud sync, the odd screenshot");

            check(Read(json).api_token == "ugt_abcdef0123456789",
                "and comes back as itself on this machine",
                "the round trip is the whole mechanism; broken, it presents as being signed out with nothing wrong on screen");

            var legacy = Read(@"{""api_token"":""ugt_writtenbeforeencryption""}");
            check(legacy.api_token == "ugt_writtenbeforeencryption",
                "a token written before encryption existed is still read",
                "it is recognised by the shape of OUR token, and the next save rewrites it");

            check(Read(@"{""api_token"":null}").api_token == null
                  && Read("{}").api_token == null,
                "and nothing stored is nothing read",
                "callers have always relied on null rather than an empty string to mean 'not signed in'");

            // ⚠ Not the same question as being readable: what a copy from another machine does is
            // the socle's business (Secrets), and it is checked there. What is checked here is that
            // this format asks for protection at all.
            check(JsonConvert.SerializeObject(new ModConfig { ai_api_key = "sk-plainlyvisible" })
                      .Contains("sk-plainlyvisible") == false,
                "and every secret in the file is asked for the same way",
                "a key added later without the attribute is written in the clear, and nothing anywhere would say so");
        }
    }
}
