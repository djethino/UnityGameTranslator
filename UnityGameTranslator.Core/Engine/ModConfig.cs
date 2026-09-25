using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    // ── The config.json contract ───────────────────────────────────────────────────────────────
    //
    // 🔴 **What this file IS: a format, and its migrations.** config.json is read by the mod and
    // written by it, and the Manager reads the same file to show and change a game's setup. So
    // every key here is a contract between two programs, and every migration below is what keeps
    // a file written by an older build meaning what its owner meant.
    //
    // ⚠ **A migration is never a tidy-up.** These keys sit in a file on every machine the mod runs
    // on. An unrecognised value read as the default silently undoes somebody's choice, which is why
    // the retired values are still named and still mapped rather than deleted.
    //
    // ⚠ **Pure by contract**, like its neighbours in Engine/: no Unity, no disk, no clock. The one
    // thing it cannot answer on its own — what language this machine is set to — is handed in (see
    // ModConfig.SystemLanguage).
    //
    // Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md),
    // verbatim apart from that one hook.

    public class ModConfig
    {
        // Which service does the translating: "llm", "google", "deepl", or "none" for a setup
        // that only ever uses translations written by somebody else.
        //
        // WHICH service, never WHETHER: that second question is enable_ai, and keeping the two
        // apart is what lets a configured backend sit idle without being forgotten.
        public string translation_backend { get; set; } = "none";

        // LLM Translation settings (universal OpenAI-compatible)
        public string ai_url { get; set; } = Endpoints.OllamaDefault;
        public string ai_model { get; set; } = "";
        public string target_language { get; set; } = "auto";
        public string source_language { get; set; } = "auto";
        public bool strict_source_language { get; set; } = false;

        /// <summary>
        /// Closing the game asks a local server (Ollama) to unload the model this session used.
        /// On by default: left to itself Ollama keeps it on the graphics card five more minutes,
        /// taking it from whatever game starts next.
        /// </summary>
        public bool ai_unload_on_exit { get; set; } = true;

        /// <summary>
        /// Keep the model loaded while the game runs, instead of the server's own delay (five
        /// minutes for Ollama): no reload in the middle of play after a quiet spell. Off by
        /// default — it holds the graphics card memory for the whole session. Released at exit
        /// with <see cref="ai_unload_on_exit"/>; after a crash it stays until the server restarts.
        /// </summary>
        public bool ai_keep_loaded { get; set; } = false;
        public string game_context { get; set; } = "";
        /// <summary>
        /// How long the mod waits for a translation backend before giving up on ONE request.
        ///
        /// 🔴 **Deliberately very wide.** A local model on a machine whose GPU is busy answers in
        /// minutes, not seconds — measured on a 27B model that had spilled into system RAM: 40 s,
        /// then 130 s, then 197 s for one-line texts, and every one of them came back. Cutting
        /// those off loses translations somebody was waiting for, which is the one thing this must
        /// never do. What the ceiling exists for is the opposite case: a game that hooks the
        /// network stack and installs a proxy which SWALLOWS the request, where no answer will ever
        /// come and without a ceiling the worker waits for the rest of the session in silence.
        ///
        /// ⚠ **Not in the settings window, on purpose**: nobody should have to think about this.
        /// It is here for whoever runs something slow enough to need it.
        ///
        /// ⚠ Was 30000 and was read by NOTHING — declared, written into every config.json, and
        /// documented on the site as "milliseconds before a translation request is given up on".
        /// See the v3 migration: a file carrying that exact value is carrying a default nobody
        /// could have chosen for an effect that did not exist.
        /// </summary>
        public int timeout_ms { get; set; } = DefaultTimeoutMs;

        /// <summary>Five minutes. Named so the migration and the reader agree on one value.</summary>
        internal const int DefaultTimeoutMs = 300000;

        /// <summary>The value every config written before the option did anything carries.</summary>
        internal const int DeadTimeoutMs = 30000;

        /// <summary>
        /// Whether live translation runs at all, whichever backend is selected.
        ///
        /// ⚠ It used to be a synonym of translation_backend == "llm", and that cost us two
        /// defects: pausing translation had to blank the backend (losing which one had been
        /// chosen, since the previous value was only remembered in a static field for the
        /// lifetime of the process), and everything that asked "can this machine translate a
        /// line" — the live edit session's per-line retranslate button, for one — answered no to
        /// every Google and DeepL user, because their config legitimately carried false.
        ///
        /// So: translation_backend says WHICH service, this says WHETHER to use it. A paused
        /// setup keeps every credential and every choice it had, which is what makes pausing
        /// something one can undo.
        /// </summary>
        public bool enable_ai { get; set; } = false;
        public bool cache_new_translations { get; set; } = true;
        public bool normalize_numbers { get; set; } = true;
        public bool debug { get; set; } = false;
        public bool debug_ai { get; set; } = false;
        public bool preload_model { get; set; } = true;

        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string ai_api_key { get; set; } = null;

        // Google Translate API settings
        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string google_api_key { get; set; } = null;

        // DeepL API settings
        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string deepl_api_key { get; set; } = null;
        public bool deepl_use_free { get; set; } = true;

        /// <summary>
        /// Delay in seconds before retrying after a rate limit (HTTP 429).
        /// Applies to all backends (LLM, Google, DeepL). Supports decimals (e.g., 0.5).
        /// </summary>
        public float rate_limit_retry_delay { get; set; } = 3f;

        #region What the model is asked, and how hard (Advanced)

        /// <summary>
        /// How many requests one line may cost, at most. Governs BOTH jobs that ask twice:
        /// repairing an answer that broke a placeholder, and retranslating a line somebody did not
        /// like. One number for both because there is no reason for them to differ — the default
        /// is <see cref="Placeholders.MaxAttempts"/>, which is also what the Manager's model bench
        /// scores against, so a model measured there behaves as measured here.
        ///
        /// ⚠ Every unit above 1 is a real request to a real backend, paid in time and possibly in
        /// money. Clamped on read, never trusted from the file.
        /// </summary>
        public int ai_max_attempts { get; set; } = Placeholders.MaxAttempts;

        /// <summary>
        /// Temperature for an ordinary translation.
        ///
        /// ⚠ Zero by default, and that is not timidity: the answer is cached, shared through the
        /// website and merged with other people's files. Two runs disagreeing about the same line
        /// would surface as a conflict nobody made.
        /// </summary>
        public double ai_temperature { get; set; } = 0.0;

        /// <summary>
        /// Temperature when re-asking because the answer broke a placeholder. Slightly above zero:
        /// an identical request would return the identical broken answer, so something has to move
        /// — but the goal is still the SAME translation, correctly marked up.
        /// </summary>
        public double ai_temperature_repair { get; set; } = 0.3;

        /// <summary>
        /// Temperature when a human rejected the translation and asked for another.
        /// High on purpose: the instructions are unchanged, only the draw is meant to differ.
        /// </summary>
        public double ai_temperature_retranslate { get; set; } = 0.8;

        /// <summary>
        /// Fixed seeds, one per job, null meaning "send none" (and, for a retranslation, "draw a
        /// new one every time").
        ///
        /// ⚠ Setting one makes runs comparable between machines — the point of a seed. For the
        /// retranslation it is used as seed + round number, so it still varies from one attempt to
        /// the next while staying reproducible; a single fixed seed there would hand back the same
        /// rejected answer forever.
        ///
        /// ⚠ Being accepted is not being honoured: several servers take the field and ignore it,
        /// saying nothing. The variation rests on the temperature; the seed only makes it
        /// repeatable where it is actually implemented (see Negotiation.SendSeed).
        /// </summary>
        public int? ai_seed { get; set; } = null;
        public int? ai_seed_repair { get; set; } = null;
        public int? ai_seed_retranslate { get; set; } = null;

        /// <summary>
        /// Attempts as an actually usable number, whatever the file says. The bounds are the
        /// socle's (<see cref="LineTranslation.ClampAttempts"/>): the Manager reads this game's file
        /// to answer the browser editor, and must read the same number.
        /// </summary>
        [JsonIgnore]
        public int AttemptsAllowed => LineTranslation.ClampAttempts(ai_max_attempts);

        /// <summary>Temperatures clamped to what an OpenAI-compatible server accepts (<see cref="LineTranslation.ClampTemperature"/>).</summary>
        [JsonIgnore]
        public double TemperatureNormal => LineTranslation.ClampTemperature(ai_temperature);
        [JsonIgnore]
        public double TemperatureRepair => LineTranslation.ClampTemperature(ai_temperature_repair);
        [JsonIgnore]
        public double TemperatureRetranslate => LineTranslation.ClampTemperature(ai_temperature_retranslate);

        #endregion

        /// <summary>
        /// Maximum time, in seconds, that a newly instantiated text component can stay
        /// untranslated before the periodic scanner picks it up. This is the worst-case
        /// detection latency for components that are not caught by the get_text/set_text
        /// Harmony hooks (i.e. instantiated and shown without their text being read or
        /// written immediately).
        ///
        /// Lower = more responsive but higher CPU usage when many text types are registered.
        /// The actual scan work is spread across frames using an adaptive frame-time budget,
        /// so the per-frame impact stays under the natural frame-time noise even at low values.
        /// </summary>
        public float max_text_detection_latency_seconds { get; set; } = 1f;

        /// <summary>
        /// True when live translation should run: a backend is selected AND it is switched on.
        ///
        /// ⚠ Both halves are required, and the second one used to be missing for the paid
        /// backends — this read `enable_ai || backend == "google" || backend == "deepl"`, so a
        /// Google or DeepL setup could not be switched off at all except by forgetting which
        /// backend it was. Turning something off must never mean erasing how it was configured.
        ///
        /// This is the single gate for the whole translation path: the worker, the scanner and
        /// every Harmony patch ask here, and the backend dispatch below them is only reached
        /// through it. One place to say no.
        /// </summary>
        [JsonIgnore]
        public bool IsTranslationEnabled => LineTranslation.IsEnabled(enable_ai, translation_backend);

        /// <summary>
        /// Returns true if the active backend requires online mode.
        /// LLM can be local (Ollama), Google and DeepL always need internet.
        /// </summary>
        [JsonIgnore]
        public bool ActiveBackendRequiresOnline =>
            translation_backend == "google" || translation_backend == "deepl";

        // Backward-compatible migration from old config format
        [JsonExtensionData]
        private IDictionary<string, JToken> _extraData;

        [System.Runtime.Serialization.OnDeserialized]
        private void OnDeserialized(System.Runtime.Serialization.StreamingContext context)
        {
            // Migrate old Ollama config fields (if present as unknown keys)
            if (_extraData != null)
            {
                bool migrated = false;
                if (_extraData.TryGetValue("ollama_url", out var url))
                {
                    ai_url = url.ToString();
                    migrated = true;
                }
                if (_extraData.TryGetValue("enable_ollama", out var eo))
                {
                    enable_ai = eo.Value<bool>();
                    migrated = true;
                }
                if (_extraData.TryGetValue("debug_ollama", out var dbg))
                {
                    debug_ai = dbg.Value<bool>();
                    migrated = true;
                }
                if (_extraData.TryGetValue("model", out var m) && string.IsNullOrEmpty(ai_model))
                {
                    ai_model = m.ToString();
                    migrated = true;
                }
                if (migrated)
                {
                    _configMigrated = true;
                }
                _extraData = null;
            }

            // One spelling of this machine, whatever an older file or a person typed — at EVERY
            // read, not versioned: somebody can type "localhost" again tomorrow. The host alone
            // changes, compared whole (Endpoints.Canonical, spec/config: localhost-is-respelled…).
            string canonicalUrl = Endpoints.Canonical(ai_url);
            if (canonicalUrl != ai_url)
            {
                ai_url = canonicalUrl;
                _configMigrated = true;
            }

            // Migrate: if enable_ai is true but translation_backend is still "none",
            // the user had AI enabled before the backend system was added
            if (enable_ai && translation_backend == "none")
            {
                translation_backend = "llm";
                _configMigrated = true;
            }

            if (config_version < CurrentConfigVersion)
            {
                // v1 — translate_mod_ui became tri-state. Every config written before that carries
                // an explicit `false`, because the mod always serialised the default, and the
                // option did nothing at all until it was made effective. Reading those as a
                // deliberate refusal would hide the feature from everyone who upgrades, so they
                // go back to "undecided" and the translation decides.
                //
                // This runs ONCE (guarded by config_version): a false the user sets from here on
                // is their choice and is honoured for good.
                if (config_version < 1 && translate_mod_ui == false)
                    translate_mod_ui = null;

                // v2 — enable_ai stopped meaning "the backend is llm" and started meaning "run
                // the translation". Every config written before this carries false whenever the
                // backend is Google or DeepL, because that is what the wizard and the options
                // screen both wrote: the flag was kept in sync with the backend rather than
                // asked about. Reading those as "switched off" would stop translating for every
                // Google and DeepL user the moment they update, with nothing on screen to
                // explain it — so they are read for what they meant, which is "on".
                //
                // Deliberately NOT applied to the llm backend: there, false already meant off
                // under both readings, and turning it on would resume a translation somebody had
                // stopped on purpose.
                if (config_version < 2
                    && !enable_ai
                    && (translation_backend == "google" || translation_backend == "deepl"))
                {
                    enable_ai = true;
                }

                // v3 — timeout_ms became effective. Exactly the same shape as v1: the key was
                // declared, serialised into every config.json and documented on the site, and read
                // by NOTHING; the real ceiling was five minutes, compiled in. So the 30000 that
                // every existing file carries is a default nobody could have chosen, for an effect
                // that did not exist — and honouring it now would cut every request at thirty
                // seconds, losing translations on precisely the slow local models the option was
                // advertised to help.
                //
                // ⚠ Only that exact value. Anyone who followed the documentation and typed a
                // different number made a real choice, about a real setting as far as they knew,
                // and it is kept.
                if (config_version < 3 && timeout_ms == DeadTimeoutMs)
                    timeout_ms = DefaultTimeoutMs;

                config_version = CurrentConfigVersion;
                _configMigrated = true;
            }
        }

        /// <summary>
        /// The two migrations of the <c>sync</c> block that need the RAW json beside the parsed
        /// object, run once the object exists. True when something moved and the file is worth
        /// writing back.
        ///
        /// 🔴 Lived in LoadConfig until 2026-09-11 — the one part of this contract nothing could
        /// hold to a case, since LoadConfig reads a disk and logs through the loader. Verbatim
        /// from there; spec/config's cases now hold it.
        ///
        /// · <c>check_update_on_start</c> became a frequency: false meant "never look", true meant
        ///   "look on every connection" — which is what the permanent stream did. Existing users
        ///   land on the new default rather than keeping a stream open for the whole session.
        /// · One setting became two (2026-08-20): the frequency decided BOTH the rhythm and whether
        ///   to keep a stream open. Read from the value AS STORED, before Normalize() folds "auto"
        ///   and "realtime" into a rhythm — those two are precisely the ones that asked for a
        ///   connection, and once folded there is no way left to tell they did. The RAW json
        ///   decides, not the property: it defaults to true for a new install, so the property
        ///   alone cannot tell an absent field from a deliberate yes.
        /// </summary>
        public bool CompleteSyncFromRaw(JObject raw)
        {
            if (sync == null) return false;
            bool moved = false;

            if (sync.check_update_on_start.HasValue)
            {
                sync.update_check_frequency = sync.check_update_on_start.Value
                    ? UpdateCheckFrequency.Hourly
                    : UpdateCheckFrequency.Never;
                sync.check_update_on_start = null;
                moved = true;
            }

            if ((raw?["sync"] as JObject)?["realtime_own_translation"] == null)
            {
                string stored = sync.update_check_frequency;
                sync.realtime_own_translation = UpdateCheckFrequency.AskedForRealtime(stored);
                sync.update_check_frequency = UpdateCheckFrequency.Normalize(stored);
                moved = true;
            }

            return moved;
        }

        /// <summary>Config schema version, bumped when a one-shot migration is added above.</summary>
        private const int CurrentConfigVersion = 3;

        // 0 = written before migrations were versioned. Persisted so each migration runs once.
        public int config_version { get; set; } = 0;

        [JsonIgnore]
        internal bool _configMigrated = false;

        // General settings
        public bool capture_keys_only { get; set; } = false;
        // Translate the mod's own interface. THREE states: true/false = the user decided and that
        // wins; ABSENT = let the translation decide (a file carrying "M" lines was authored with a
        // translated UI). See TranslatorCore.ShouldTranslateOwnUI.
        public bool? translate_mod_ui { get; set; } = null;
        // Local override for the interface font (game/system/custom font name, possibly with a
        // "[Game] "/"[Custom] " picker prefix). null = use whatever the translation asks for
        // (_settings.ui_font), or UniverseLib's default when it asks for nothing.
        public string interface_font { get; set; } = null;

        /// <summary>
        /// Advanced fallback: Translate at localization string level (ToString/op_Implicit).
        /// WARNING: Ignores font-based enable/disable settings.
        /// Only enable if some text is not being captured by other methods.
        /// </summary>
        public bool translate_localization_fallback { get; set; } = false;

        // Online mode and sync settings
        public bool first_run_completed { get; set; } = false;
        public bool online_mode { get; set; } = false;
        public bool enable_translations { get; set; } = true;

        // Runtime debug toggles — persisted to config.json so developers/translators
        // can keep them off between sessions. End users should leave these at true.
        public bool enable_image_replacement { get; set; } = true;
        public bool enable_font_replacement { get; set; } = true;

        // What the mod takes from the game while one of its windows is open. A preference of
        // whoever is working, hence here and not in the shared translation file.
        public bool capture_keyboard { get; set; } = true;
        // ⚠ Only while our interface actually holds the keyboard focus. Default ON, and it is what
        // makes "capture the keyboard" safe to have on by default: the game keeps its keys until
        // somebody types or navigates in a mod window.
        public bool capture_keyboard_focus_only { get; set; } = true;
        // OFF by default, unlike the keyboard: typing into a field must never drive the game — that
        // hits everyone, including someone who only opened the language search. These two are
        // comfort, they touch the EventSystem and the pointer, and that is where every mishap of
        // this feature came from.
        public bool capture_game_menus { get; set; } = false;
        public bool capture_game_clicks { get; set; } = false;
        public bool capture_mouse_axes { get; set; } = false;
        // Off by default: what it does depends entirely on the game, and it must never be used in
        // a multiplayer one. See analyse/pause-the-game-feasibility.md.
        public bool pause_game { get; set; } = false;

        // How solid a mod window is, focused and not. Deliberately a hair apart by default: enough
        // to tell at a glance which window has the keyboard, never enough to hinder reading. The
        // unfocused one is also what lets a translator keep a second window open — the options,
        // say — and still read the game underneath it.
        public float panel_opacity_focused { get; set; } = 1f;
        public float panel_opacity_unfocused { get; set; } = 0.75f;

        // Max SDF atlas dimension the auto-quality picker may use when rasterizing a
        // replacement font. 0 = automatic default (4096). Raising it (e.g. 8192) renders
        // replacement fonts at a higher SDF resolution → crisper when the translator scales
        // the text up, at a VRAM cost. Capped by SystemInfo.maxTextureSize. LAYOUT-NEUTRAL
        // (TMP normalizes the SDF by pointSize → text size is unchanged, only sharpness),
        // so it is safe on already-published translations. See analyse/font-rendering-target-size.md.
        public int max_font_atlas_size { get; set; } = 0;

        public string settings_hotkey { get; set; } = "F10";

        // Additional hotkeys (empty = disabled). Configured via Options panel only.
        // Each one maps to a toggle/action. Unused by the wizard to avoid conflicts.
        public string toggle_translations_hotkey { get; set; } = "";
        public string toggle_ai_hotkey { get; set; } = "";
        public string toggle_images_hotkey { get; set; } = "";
        public string toggle_fonts_hotkey { get; set; } = "";
        public string toggle_overlay_hotkey { get; set; } = "";
        public string open_inspector_hotkey { get; set; } = "";
        public string open_upload_hotkey { get; set; } = "";
        public string open_exclusion_mode_hotkey { get; set; } = "";
        public string open_text_editor_hotkey { get; set; } = "";
        public string force_scan_hotkey { get; set; } = "";

        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string api_token { get; set; } = null;
        public string api_user { get; set; } = null;
        // Server URL where the token was issued (for security: invalidate if URL changes)
        public string api_token_server { get; set; } = null;

        // Advanced: Override API URLs (null = use compiled default from Directory.Build.props)
        // For self-hosting or testing. Edit config.json manually to use.
        public string api_base_url { get; set; } = null;
        public string website_base_url { get; set; } = null;
        public string sse_base_url { get; set; } = null;

        // Proxy configuration for the mod's HTTP requests (AI provider, UGT site, GitHub).
        // Some games intercept or hook outbound HTTP at the process level (DRM, anti-cheat,
        // EOS bootstrap, etc.), which can make the default HttpClient hang indefinitely.
        // Modes:
        //   "default" — let HttpClient inherit WebRequest.DefaultProxy (legacy behavior;
        //               can be silently replaced by the game at runtime, hence the option)
        //   "system"  — force a fresh GetSystemWebProxy() ignoring any runtime overrides
        //   "none"    — bypass all proxies, talk directly (fixes the "stuck on Testing..." case)
        //   "custom"  — route through proxy_url with optional credentials
        public string proxy_mode { get; set; } = "default";
        public string proxy_url { get; set; } = null;
        public string proxy_username { get; set; } = null;
        // Often a corporate/AD credential — encrypted at rest like every other secret.
        // Plaintext values from existing configs are re-encrypted on next save.
        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string proxy_password { get; set; } = null;
        public bool proxy_bypass_local { get; set; } = true;

        public SyncConfig sync { get; set; } = new SyncConfig();
        public WindowPreferences window_preferences { get; set; } = new WindowPreferences();

        /// <summary>
        /// What this machine calls its own language, for a target still left at "auto".
        ///
        /// 🔴 **A fact the HOST knows, not a rule this file can hold.** Reading it means asking
        /// .NET's culture and, when a mod loader has flattened that to invariant, Unity's
        /// `Application.systemLanguage` — an engine call, in the one class that describes a file
        /// format shared with the Manager.
        ///
        /// ⚠ Set once, and it cannot be missed: every caller of
        /// <see cref="GetTargetLanguage"/> reaches this object through `TranslatorCore.Config`
        /// (all sixteen of them, checked before this was introduced), so TranslatorCore's own
        /// static initialisation has always run first — and that is where it is assigned.
        /// </summary>
        public static Func<string> SystemLanguage { get; set; }

        /// <summary>
        /// The target language, with "auto" resolved against this machine.
        ///
        /// ⚠ "auto" is a MODE, not a value: it answers differently on the next machine, and
        /// follows the player's system language if they change it. What settles it into a value is
        /// the translation's first line — see LanguageState.SettleTargetOnFirstLine.
        /// </summary>
        public string GetTargetLanguage()
        {
            if (string.IsNullOrEmpty(target_language) || target_language.ToLower() == "auto")
            {
                return SystemLanguage?.Invoke();
            }
            return target_language;
        }

        public string GetSourceLanguage()
        {
            if (string.IsNullOrEmpty(source_language) || source_language.ToLower() == "auto")
            {
                return null;
            }
            return source_language;
        }
    }

    /// <summary>
    /// The values <see cref="SyncConfig.update_check_frequency"/> accepts, and the
    /// only place that knows what each one costs in seconds. Anything unknown on
    /// disk falls back to hourly rather than being silently treated as "never":
    /// a typo must not stop someone from ever hearing about an update.
    /// </summary>
    public static class UpdateCheckFrequency
    {
        public const string Never = "never";
        public const string Startup = "startup";
        public const string Hourly = "1h";
        public const string ThreeHourly = "3h";
        public const string SixHourly = "6h";

        /// <summary>Ordered as shown in the options dropdown.</summary>
        public static readonly string[] All =
        {
            Never, Startup, Hourly, ThreeHourly, SixHourly
        };

        // ── What this setting used to also carry (2026-08-20) ─────────────────
        //
        // 🔴 It answered two questions at once: the rhythm of the checks, AND whether to keep a
        // stream open. That is what made contributions arrive in real time — a Main was woken up
        // by every branch anybody sent, and each wake-up now costs a read of their files. The two
        // questions are separate controls, and these three values only survive to be migrated.
        //
        // ⚠ Never deleted outright: they sit in config.json on every machine the mod runs on, and
        // an unrecognised value would be silently read as the default, undoing somebody's choice.
        private const string LegacyAuto = "auto";
        private const string LegacyRealtime = "realtime";
        private const string LegacyHalfHourly = "30m";

        /// <summary>
        /// Seconds between two checks, or 0 when this frequency never repeats
        /// ("never" and "startup").
        /// </summary>
        public static float IntervalSeconds(string frequency)
        {
            switch (frequency)
            {
                case Hourly:      return 60f * 60f;
                case ThreeHourly: return 3f * 60f * 60f;
                case SixHourly:   return 6f * 60f * 60f;
                default:          return 0f;
            }
        }

        /// <summary>
        /// The stored value, read as one of the choices that still exist.
        ///
        /// ⚠ Anything unknown falls back to hourly rather than being read as "never": a typo, or a
        /// value written by a newer version, must not silently stop somebody from ever hearing
        /// about an update.
        /// </summary>
        public static string Normalize(string frequency)
        {
            if (Array.IndexOf(All, frequency) >= 0) return frequency;

            // The three retired values. "auto" and "realtime" also asked for a stream, which is
            // now its own setting — see WantsRealtimeFor, which reads the same stored string.
            switch (frequency)
            {
                case LegacyHalfHourly: return Hourly;
                case LegacyAuto:       return Hourly;
                case LegacyRealtime:   return Hourly;
                default:               return Hourly;
            }
        }

        /// <summary>
        /// Did this stored value ask for a permanent connection?
        ///
        /// Used ONCE, to fill the new setting from an existing config.json: "auto" opened a stream
        /// for anybody owning a translation, and "realtime" for everybody. Every other value never
        /// opened one, and reading it as a yes would hand somebody a connection they had declined.
        ///
        /// ⚠ "never" answers no, so somebody who asked for silence keeps it whole.
        /// </summary>
        public static bool AskedForRealtime(string storedFrequency)
        {
            return storedFrequency == LegacyAuto || storedFrequency == LegacyRealtime;
        }
    }

    public class SyncConfig
    {
        /// <summary>
        /// How often the mod asks the site what changed. Values: "never", "startup", "1h", "3h",
        /// "6h". Every rhythm also checks once at startup — that is the moment an update can be
        /// applied without interrupting play.
        ///
        /// 🔴 **The rhythm, and nothing else.** It used to decide whether to keep a stream open as
        /// well, which put the contributions a Main receives on that stream: they arrived within
        /// seconds, and every one of them woke the game up to recount. Whether to stay connected is
        /// now <see cref="realtime_own_translation"/>, and the two read together — this one is the
        /// pace, that one says what does not wait for it.
        /// </summary>
        public string update_check_frequency { get; set; } = UpdateCheckFrequency.Hourly;

        /// <summary>
        /// Keep a connection open so what THIS account publishes elsewhere — the website, another
        /// machine — comes back to the game as it happens.
        ///
        /// ⚠ **Only ever about one's own line.** What other people do (a contribution arriving, a
        /// Main moving on, a newer version published) follows
        /// <see cref="update_check_frequency"/>, whatever this says. Somebody publishing every ten
        /// minutes would otherwise wake every contributor of their lineage each time.
        ///
        /// ⚠ A permission, not an order: the mod opens the stream only when there is something of
        /// one's own to watch — a published line in this lineage. A player merely using somebody
        /// else's translation opens nothing either way.
        ///
        /// ⚠ On by default for a NEW config only. An existing one is filled from what its owner had
        /// already chosen — see the migration in LoadConfig, which reads the raw JSON to tell an
        /// absent property from a stored false.
        /// </summary>
        public bool realtime_own_translation { get; set; } = true;

        /// <summary>
        /// Superseded by <see cref="update_check_frequency"/>. Read ONCE to migrate
        /// existing config files (false meant "never look"), then removed from disk.
        /// Nullable so an absent property is distinguishable from a stored false.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? check_update_on_start { get; set; }

        public bool auto_download { get; set; } = false;
        public bool notify_updates { get; set; } = true;

        /// <summary>
        /// Enable the corner notification overlay (mod updates, sync, AI queue).
        /// When false, all overlay notifications are hidden.
        /// </summary>
        public bool notifications_enabled { get; set; } = true;

        /// <summary>
        /// Screen corner for notification overlay.
        /// Values: "top-right", "top-left", "bottom-right", "bottom-left"
        /// </summary>
        public string notification_position { get; set; } = "top-right";

        public string merge_strategy { get; set; } = "ask";
        public List<string> ignored_uuids { get; set; } = new List<string>();

        /// <summary>
        /// Notices this install has been shown once and dismissed, as "type:uuid" — for example
        /// "main-ignoring:9cabf6da-...". Generic on purpose: the next notice that must be said
        /// once will not need a third field, and the file stays readable to whoever opens it.
        ///
        /// Dismissing is final for that translation. Somebody who wants it back deletes the line.
        /// </summary>
        public List<string> dismissed_notices { get; set; } = new List<string>();

        /// <summary>
        /// Check for mod updates on GitHub at startup.
        /// Only works when online_mode is enabled.
        /// </summary>
        public bool check_mod_updates { get; set; } = true;

        /// <summary>
        /// Also notify about beta releases (GitHub pre-releases). Off by default:
        /// most players should only hear about stable releases.
        /// </summary>
        public bool notify_prereleases { get; set; } = false;

        // ⚠ `last_seen_mod_version`, `last_seen_from_version` and `last_seen_published_at` were
        // removed here: they were read in exactly one place, to decide that a release already
        // shown once must never be shown again. That is a "skip this version" nobody asked for,
        // and it silenced the main panel's banner along with the notice. Whether an update is
        // offered is now read from the release itself every time, and hiding it is what the
        // closing cross does — for the session, as a dismissal should.
        //
        // A config.json written by an older build still carries the three keys; Newtonsoft
        // ignores what the class no longer declares, and the next save drops them.
    }

    /// <summary>
    /// Per-panel window preferences for persistence across sessions.
    /// Position and size are saved independently.
    /// </summary>
    public class WindowPreference
    {
        /// <summary>Panel X position (anchored position, center-relative)</summary>
        public float x { get; set; }
        /// <summary>Panel Y position (anchored position, center-relative)</summary>
        public float y { get; set; }
        /// <summary>Panel width in pixels</summary>
        public float width { get; set; }
        /// <summary>Panel height in pixels</summary>
        public float height { get; set; }
        /// <summary>True if user manually moved the panel (apply saved position)</summary>
        public bool hasPosition { get; set; }
        /// <summary>True if user manually resized (don't auto-adjust size)</summary>
        public bool userResized { get; set; }
    }

    /// <summary>
    /// Collection of window preferences keyed by panel name.
    /// Screen dimensions are stored globally since all panels share the same screen.
    /// </summary>
    public class WindowPreferences
    {
        /// <summary>Screen width when preferences were last saved</summary>
        public int screenWidth { get; set; }
        /// <summary>Screen height when preferences were last saved</summary>
        public int screenHeight { get; set; }
        /// <summary>Per-panel position and size preferences</summary>
        public Dictionary<string, WindowPreference> panels { get; set; } = new Dictionary<string, WindowPreference>();
    }
}
