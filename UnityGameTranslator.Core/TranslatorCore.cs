using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGameTranslator.Core.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Interface for mod loader abstraction (logging, paths, etc.)
    /// </summary>
    public interface IModLoaderAdapter
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        string GetPluginFolder();

        /// <summary>
        /// Mod loader type identifier for GitHub release asset selection.
        /// Values: "BepInEx5", "BepInEx6-Mono", "BepInEx6-IL2CPP", "MelonLoader-Mono", "MelonLoader-IL2CPP"
        /// </summary>
        string ModLoaderType { get; }

        /// <summary>
        /// Whether this mod loader is running on IL2CPP (vs Mono).
        /// Used to determine which UniverseLib variant to use and which scanning method to apply.
        /// </summary>
        bool IsIL2CPP { get; }
    }

    /// <summary>
    /// Main translation engine - shared across all mod loaders
    /// </summary>
    public class TranslatorCore
    {
        public static TranslatorCore Instance { get; private set; }
        public static IModLoaderAdapter Adapter { get; private set; }
        public static volatile bool ShuttingDown;
        public static ModConfig Config { get; private set; } = new ModConfig();
        public static Dictionary<string, TranslationEntry> TranslationCache { get; private set; } = new Dictionary<string, TranslationEntry>();
        public static List<PatternEntry> PatternEntries { get; private set; } = new List<PatternEntry>();
        public static string CachePath { get; private set; }
        public static string ConfigPath { get; private set; }
        public static string ModFolder { get; private set; }
        public static bool DebugMode { get; private set; } = false;
        public static string FileUuid { get; private set; }

        /// <summary>
        /// Per-font settings for translation and fallback.
        /// Stored in translations.json as _fonts for sharing.
        /// Key = font name (case-insensitive), Value = settings (enabled, fallback)
        /// </summary>
        public static Dictionary<string, FontSettings> FontSettingsMap { get; set; } = new Dictionary<string, FontSettings>(StringComparer.OrdinalIgnoreCase);

        public static GameInfo CurrentGame { get; internal set; }

        /// <summary>
        /// Server state for current translation (populated via check-uuid, not persisted)
        /// </summary>
        public static ServerTranslationState ServerState { get; set; }

        /// <summary>
        /// Context for pending fork operation. Set before CreateFork() with source translation info.
        /// Used by UploadPanel to skip UploadSetupPanel since languages/game are already known.
        /// Cleared after successful upload.
        /// </summary>
        public static ForkContext PendingFork { get; set; }

        public static int LocalChangesCount { get; private set; } = 0;

        /// <summary>
        /// Translated lines this session has actually put on screen.
        /// </summary>
        private static int _translationsShownThisSession = 0;

        /// <summary>
        /// Enough of the translation seen for an opinion to be worth anything.
        ///
        /// A rating given seconds after installing measures nothing — Nexus Mods reached the
        /// same conclusion and only allows an endorsement fifteen minutes after the download.
        /// Lines shown beat elapsed time here: one can sit in a pause menu for a quarter of an
        /// hour without reading a single translated word.
        /// </summary>
        private const int TranslationsShownBeforeRating = 50;

        /// <summary>
        /// Has this player seen enough of the translation to judge it? Read by the settings
        /// panel to decide whether to offer the vote at all.
        /// </summary>
        public static bool HasUsedTranslationEnoughToRate
            => _translationsShownThisSession >= TranslationsShownBeforeRating;

        /// <summary>
        /// One translated line reached the screen. Called from the scanner's apply path, which
        /// runs on the main thread — Interlocked all the same, because IL2CPP has surprised us
        /// on which thread a Unity callback ends up.
        /// </summary>
        public static void NoteTranslationShown()
        {
            System.Threading.Interlocked.Increment(ref _translationsShownThisSession);
        }

        /// <summary>
        /// True when metadata (fonts, images, exclusions) has been modified locally since last upload.
        /// Included in sync direction calculation so metadata changes trigger an upload prompt.
        /// </summary>
        public static bool MetadataDirty { get; private set; } = false;
        public static Dictionary<string, TranslationEntry> AncestorCache { get; private set; } = new Dictionary<string, TranslationEntry>();

        /// <summary>
        /// The SETTINGS as they stood at the last sync, or null when unknown
        /// (ancestor written before settings travelled with it, or an ancestor
        /// built from a source whose settings we never saw).
        ///
        /// null is not a degraded value, it is an honest one: with no common
        /// baseline the mod cannot tell who changed a section, so it asks
        /// instead of guessing. See analyse/metadata-visibility-and-sync.md.
        /// </summary>
        public static TranslationSettings AncestorSettings { get; private set; }

        /// <summary>
        /// Hash of the translation at last sync (download or upload).
        /// Used to detect if server has changed since our last sync.
        /// Stored in translations.json as _source.hash
        /// </summary>
        public static string LastSyncedHash { get; set; } = null;

        /// <summary>
        /// Hash of the MAIN as it stood the last time this branch merged from it.
        /// Stored in translations.json as _source.main_hash.
        ///
        /// Without it a branch cannot tell "the Main moved" from "I differ from the
        /// Main", which is true permanently and would notify forever. Distinct from
        /// LastSyncedHash on purpose: that one tracks this translation's own line on
        /// the site, this one tracks the upstream it derives from. Never mix them —
        /// see analyse/main-to-branch-sync.md §2.
        /// </summary>
        public static string LastMergedMainHash { get; set; } = null;

        /// <summary>
        /// Site id of the translation this file came from, kept in
        /// translations.json as _source.site_id.
        ///
        /// Exists for the ONE case that had no way of hearing about an update:
        /// someone with no account. Searching and downloading need no account,
        /// but every update path went through the authenticated sync state, so
        /// they installed a translation and were never told it had moved again.
        /// With the id, the public check endpoint answers them.
        ///
        /// Public identifier of a public translation: nothing to protect here,
        /// and the endpoint already refuses branches to anyone but their Main.
        /// </summary>
        public static int? SourceSiteId { get; set; } = null;

        /// <summary>
        /// Where this file came from when it was forked, and how much of it was already written
        /// at that moment. Set once by CreateFork(), never touched again.
        ///
        /// Kept APART from SourceSiteId on purpose. That one drives synchronisation, and a fork
        /// must forget it — otherwise the mod keeps offering to merge from a lineage it has just
        /// left. But detaching the sync is not the same as erasing where the work came from, and
        /// one variable used to carry both: forking wiped the provenance as a side effect, so a
        /// fork arrived on the site as a brand-new translation and whoever wrote the first three
        /// thousand lines lost every trace of it.
        ///
        /// The line count is measured here rather than asked of the server later: the original
        /// keeps growing, so the question only has an answer at the instant of the fork.
        /// </summary>
        public static int? ForkedFromSiteId { get; set; } = null;
        public static string ForkedFromHash { get; set; } = null;
        public static int? ForkedFromResolvedLines { get; set; } = null;

        /// <summary>
        /// The forked file as it stood at the fork — lines, tags and the settings that travel
        /// with them — fingerprinted, so that "have I made anything of my own yet" has an answer.
        ///
        /// 🔴 **ForkedFromHash cannot answer it.** That is the server's hash of the source, and
        /// ContentHash hashes the uuid alongside the lines — a fork gets a new uuid, so the two
        /// differ from the first instant whatever the content. Comparing them would report work
        /// nobody did.
        ///
        /// ⚠ So the fingerprint deliberately hashes the lines with the uuid held CONSTANT. It is
        /// not a file_hash and must never be sent as one: it answers one question, here.
        /// </summary>
        public static string ForkedFromContentHash { get; set; } = null;

        /// <summary>
        /// A fork that is still, line for line, the copy it was made from.
        ///
        /// 🔴 **Publishing that is publishing somebody else's file under one's own name.** A fork
        /// is free to publish whenever it likes — no account gate, no waiting — but not before it
        /// holds something of its own, and the site would otherwise carry two identical entries
        /// competing for the same readers.
        ///
        /// ⚠ False when the fingerprint is absent: a file forked before this existed, or one that
        /// never was a fork. Unknown is not "identical", and refusing on a question nobody answered
        /// would take publishing away from people who have every right to it.
        /// </summary>
        public static bool ForkIsStillTheCopy =>
            !string.IsNullOrEmpty(ForkedFromContentHash)
            && string.Equals(ComputeContentFingerprint(), ForkedFromContentHash, StringComparison.Ordinal);

        /// <summary>
        /// If true, UniverseLib won't override the game's EventSystem.
        /// Enable this if the game's UI animations or navigation don't work with the mod.
        /// Stored in translations.json as _settings.disable_eventsystem_override
        /// Requires game restart to take effect.
        /// </summary>
        public static bool DisableEventSystemOverride { get; set; } = false;

        /// <summary>
        /// While a mod panel is open, stop the game from reading input under it.
        /// </summary>
        /// <remarks>
        /// ⚠ In config.json, NOT in translations.json — these say how the person working wants the
        /// interface to behave around them, which is a preference and not a property of the game.
        /// translations.json is uploaded when a translation is shared: putting them there would
        /// publish somebody's habits with their work, and impose them on everyone who downloads it.
        ///
        /// The one that DOES belong in the translation is "let the game handle its own interface
        /// input": it answers a defect of a particular game, so it is worth carrying to whoever
        /// installs that translation next.
        /// </remarks>
        public static bool CaptureKeyboard
        {
            get { return Config == null || Config.capture_keyboard; }
            set { if (Config != null) Config.capture_keyboard = value; }
        }
        /// <inheritdoc cref="CaptureKeyboard"/>
        /// <summary>
        /// Take the keyboard only while our interface holds the keyboard focus.
        /// </summary>
        /// <remarks>
        /// Measured on the selection, not on a panel being open: "a panel is open" would mean
        /// capturing all the time, which is the parent option. And not on "a text field is active"
        /// either — that would send Tab and the arrow keys to the game and break our own keyboard
        /// navigation.
        /// </remarks>
        public static bool CaptureKeyboardFocusOnly
        {
            get { return Config == null || Config.capture_keyboard_focus_only; }
            set { if (Config != null) Config.capture_keyboard_focus_only = value; }
        }

        /// <summary>Stop the game's own menus and buttons from answering the pointer.</summary>
        public static bool CaptureGameMenus
        {
            get { return Config != null && Config.capture_game_menus; }
            set { if (Config != null) Config.capture_game_menus = value; }
        }

        /// <summary>Stop the game from reading clicks for itself — shooting, interacting.</summary>
        /// <remarks>
        /// Separate from <see cref="CaptureGameMenus"/> because they are separate paths: a menu is
        /// reached by a raycast, a gameplay click is a read. One switch for both meant that giving
        /// the game's menus back also gave it every click.
        /// </remarks>
        public static bool CaptureGameClicks
        {
            get { return Config != null && Config.capture_game_clicks; }
            set { if (Config != null) Config.capture_game_clicks = value; }
        }
        /// <inheritdoc cref="CaptureKeyboard"/>
        public static bool CaptureMouseAxes
        {
            get { return Config != null && Config.capture_mouse_axes; }
            set { if (Config != null) Config.capture_mouse_axes = value; }
        }
        /// <inheritdoc cref="CaptureKeyboard"/>
        public static bool PauseGame
        {
            get { return Config != null && Config.pause_game; }
            set { if (Config != null) Config.pause_game = value; }
        }

        /// <summary>Opacity of a mod window, focused and unfocused. Floored so it stays readable.</summary>
        public static float PanelOpacityFocused
        {
            get { return Config == null ? 1f : Clamp01Floor(Config.panel_opacity_focused); }
            set { if (Config != null) Config.panel_opacity_focused = Clamp01Floor(value); }
        }
        /// <inheritdoc cref="PanelOpacityFocused"/>
        public static float PanelOpacityUnfocused
        {
            get { return Config == null ? 0.75f : Clamp01Floor(Config.panel_opacity_unfocused); }
            set { if (Config != null) Config.panel_opacity_unfocused = Clamp01Floor(value); }
        }

        /// <summary>
        /// ⚠ Floor at 0.4: uGUI applies the alpha to the whole subtree, text included, so below
        /// that a window is not translucent — it is unreadable, and somebody would conclude the
        /// mod is broken rather than that they moved a slider too far.
        /// </summary>
        private static float Clamp01Floor(float value)
        {
            if (value < 0.4f) return 0.4f;
            if (value > 1f) return 1f;
            return value;
        }

        /// <summary>Detect typewriting effects (text appearing letter by letter). Stored in translations.json.</summary>
        public static bool TypewritingDetection { get; set; } = true;
        /// <summary>Detect procedural text building (tooltips, item descriptions). Stored in translations.json.</summary>
        public static bool ConcatDetection { get; set; } = true;


        // What this provider+model turned out to need, negotiated from refusals rather than guessed
        // from a URL or a model name — see UnityGameTranslator.Common.Negotiation, shared with the
        // bench so it stops scoring models on a request shape their server would not take.
        private static readonly Negotiation _negotiation = new Negotiation();

        /// <summary>Forget what we learned when the provider or the model changes.</summary>
        private static void EnsureProviderQuirks() =>
            _negotiation.ForgetIfChanged($"{Config?.ai_url}|{Config?.ai_model}");

        /// <summary>
        /// Whether the source and target languages may still be changed.
        ///
        /// Two reasons they may not, and the second was missing:
        ///
        /// · **published** — the server keeps the languages a translation was published with and
        ///   ignores any sent with an update, so nothing here could move them anyway;
        ///
        /// · 🔴 **this file already holds lines.** A target language is not a preference, it is
        ///   what the file IS: retargeting a file that already carries lines leaves every one of
        ///   them written in a language the game is no longer asking for, and the next captures
        ///   arrive in the new one. One file, two languages, and nothing said so. Captured lines
        ///   count — they are the ones that would be orphaned.
        ///
        /// The way to change it is to clear the translation first, which is what the panel says.
        /// The manager applies the same rule on its own screens.
        /// </summary>
        public static bool AreLanguagesLocked =>
            (ServerState != null && ServerState.Exists) || TranslationCache.Count > 0;

        /// <summary>Which of the two reasons applies, so the panel can say the right one.</summary>
        public static bool LanguagesLockedByPublishing => ServerState != null && ServerState.Exists;

        /// <summary>
        /// Returns true if a remote translation's UUID matches our local FileUuid.
        /// Used to highlight translations from the same lineage in the community list.
        /// </summary>
        public static bool IsUuidMatch(string remoteUuid)
        {
            return !string.IsNullOrEmpty(remoteUuid) &&
                   !string.IsNullOrEmpty(FileUuid) &&
                   remoteUuid == FileUuid;
        }

        private static float lastSaveTime = 0f;
        private static int translatedCount = 0;
        private static int aiTranslationCount = 0;
        private static int cacheHitCount = 0;
        private static Dictionary<int, string> lastSeenText = new Dictionary<int, string>();
        private static HashSet<string> pendingTranslations = new HashSet<string>();
        private static Queue<string> translationQueue = new Queue<string>();

        /// <summary>
        /// Texts already refused for being longer than any backend accepts, so the warning is
        /// logged once instead of on every scan. In memory only: nothing about a refusal belongs
        /// in the translation file.
        /// </summary>
        private static readonly HashSet<string> tooLongTexts = new HashSet<string>();
        // Texts queued explicitly as mod-UI (guarded by lockObj, like the queue itself).
        private static readonly HashSet<string> pendingOwnUITexts = new HashSet<string>();
        // Own-UI texts already submitted in this session, so a label rewritten every frame is
        // submitted once even when the answer never produces a cache entry. Cleared on cache reload.
        private static readonly HashSet<string> ownUISubmitted = new HashSet<string>();
        // Note: Own UI detection now happens at processing time using IsOwnUITranslatable(component)
        // instead of string-based tracking which caused false positives when game text matched mod UI text
        private static object lockObj = new object();
        private static bool cacheModified = false;
        // Next capture-order index "i" to assign (monotonic, per lineage).
        // Recomputed as max(i)+1 at every LoadCache — never persisted as metadata.
        // Assign ONLY via NextOrderIndex() (lock-protected).
        private static long nextTranslationIndex = 1;
        private static HttpClient httpClient;
        private static int skippedAlreadyTranslated = 0;
        private static bool _enableTranslationsLogOnce = true; // Log once when translations disabled

        // Reverse cache: all translated values (to detect already-translated text)
        private static ConcurrentDictionary<string, byte> translatedTexts = new ConcurrentDictionary<string, byte>();

        // Decoration-insensitive form of the same translated values. The exact reverse cache above
        // misses a whole family: games that build text from templates re-format their slots when they
        // read a component back — {0} becomes 3, or <color=#F4FF58>3</color>. What comes back is OUR
        // translation wearing a decoration we never produced, so it looks like new source text and
        // gets re-translated, drifting on each round trip and polluting the cache with target-language
        // keys. Comparing on a decoration-insensitive form recognises it, synchronously, with no delay.
        // See analyse/readback-substitution-fr-keys-analysis.md.
        private static ConcurrentDictionary<string, byte> readbackTranslations = new ConcurrentDictionary<string, byte>();
        // A text qualifies once it carries a real word: at least two letters, at least two of them
        // adjacent. Deliberately NOT a letter COUNT — one ideograph is one letter, so a threshold
        // tuned on the alphabet protected a Latin sentence while leaving its Chinese equivalent
        // exposed. Measured across the bench: identical results, minus the Latin assumption.
        private const int ReadbackMinLetters = 2;
        private const int ReadbackMinLetterRun = 2;
        private const int ReadbackSkipLogBudget = 10;
        private static int _readbackSkipLogCount;
        private static int _shapedQueueRefusals;
        private static int _readbackStoreLogged;

        /// <summary>
        /// Decoration-insensitive form of a text: rich-text tags dropped, every number and every one
        /// of our placeholders collapsed to '#', brace/bracket/emphasis decoration removed, whitespace
        /// collapsed, lowercased. Letters are preserved untouched, which is what makes the comparison
        /// safe: two texts only collide when they carry the same words.
        /// Returns null when the result holds fewer than ReadbackMinLetters letters — short numeric or
        /// symbolic strings ("3", "x2", "100%") all collapse onto each other and must never be matched
        /// this way.
        /// Hand-rolled rather than regex: this runs on the set_text path, thousands of calls per second.
        /// </summary>
        internal static string NormalizeForReadbackMatch(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var sb = new System.Text.StringBuilder(text.Length);
            int letters = 0;
            int run = 0, longestRun = 0;
            bool lastWasSpace = true;   // leading whitespace is dropped

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Rich-text tag: <color=#...>, </color>, <b>, <size=..>…
                if (c == '<')
                {
                    int close = text.IndexOf('>', i + 1);
                    if (close > i && close - i <= 64)
                    {
                        char next = text[i + 1];
                        if (next == '/' || char.IsLetter(next)) { i = close; continue; }
                    }
                }

                // Our own placeholders ([!v*0], [!t*1], [!STR*2]) and any literal number the game
                // re-injected in their place — one and the same slot, so one and the same token.
                if (c == '[' && i + 2 < text.Length && text[i + 1] == '!')
                {
                    int close = text.IndexOf(']', i + 2);
                    if (close > i && close - i <= 16)
                    {
                        sb.Append('#'); lastWasSpace = false; run = 0; i = close; continue;
                    }
                }
                if (char.IsDigit(c))
                {
                    while (i + 1 < text.Length)
                    {
                        char nx = text[i + 1];
                        if (char.IsDigit(nx)) { i++; continue; }
                        // A separator belongs to the number only when a digit follows it: "3,5" is
                        // one number, "= 3, les points" is a number then punctuation. Swallowing that
                        // comma made the SAME sentence normalise differently depending on whether the
                        // slot still held our placeholder or the value the game had re-injected — so
                        // the guards upstream never recognised what the storage guard did.
                        if ((nx == '.' || nx == ',') && i + 2 < text.Length && char.IsDigit(text[i + 2])) { i += 2; continue; }
                        break;
                    }
                    if (i + 1 < text.Length && text[i + 1] == '%') i++;
                    sb.Append('#'); lastWasSpace = false; run = 0; continue;
                }

                // Decoration the game adds or removes around the same words
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == '*') continue;

                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                    run = 0;
                    continue;
                }

                if (char.IsLetter(c))
                {
                    letters++;
                    run++;
                    if (run > longestRun) longestRun = run;
                }
                else run = 0;
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }

            if (letters < ReadbackMinLetters || longestRun < ReadbackMinLetterRun) return null;
            // Trailing space, if any
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// Index a produced translation for decoration-insensitive recognition.
        /// Only entries whose value is a REAL translation are indexed: when the normalized value
        /// equals the normalized key, the "translation" is the source text itself (unchanged output,
        /// or a typewriter frame whose only difference is a tag). Indexing those would let the gate
        /// refuse genuine source text — measured on the bench, that single guard took one game from
        /// 42 wrong matches down to zero.
        /// </summary>
        // Presented (shaped) form → the LOGICAL string it was composed from. The refuse-to-learn
        // set says "this is ours"; only this map can say "and HERE is its truth" — without it the
        // in-game editor resolved a shaped display back to a shaped KEY and offered to save it
        // (found by the user: an Arabic key in the text editor).
        private static readonly ConcurrentDictionary<string, string> presentedToLogical =
            new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Register a PRESENTED string — one the RTL pipeline composed for display — as our own
        /// output, together with the logical string it came from. Every gate
        /// (<see cref="IsAlreadyTargetText"/>: the scanner, the getters, the setter prefixes)
        /// then refuses to learn from it, and everything that resolves a DISPLAYED text back to
        /// the cache (<see cref="ResolveDisplayedText"/>) recovers the logical truth first —
        /// a shaped form must never be queued to the AI, cached as a source text, or written to
        /// translations.json (decision D8). Also used by the temporary RtlProbe bench.
        /// </summary>
        internal static void RegisterPresentedText(string presented, string logical)
        {
            if (string.IsNullOrEmpty(presented)) return;
            string n = NormalizeForReadbackMatch(presented);
            if (n == null) return;
            readbackTranslations.TryAdd(n, 0);
            if (!string.IsNullOrEmpty(logical) && !string.Equals(presented, logical, StringComparison.Ordinal))
                presentedToLogical[n] = logical;
        }

        /// <summary>The logical string behind a presented one, or null when the text is not ours.</summary>
        internal static string TryGetPresentedLogical(string displayed)
        {
            if (string.IsNullOrEmpty(displayed) || presentedToLogical.IsEmpty) return null;
            string n = NormalizeForReadbackMatch(displayed);
            if (n == null) return null;
            return presentedToLogical.TryGetValue(n, out var logical) ? logical : null;
        }

        private static void IndexReadbackTranslation(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            string nv = NormalizeForReadbackMatch(value);
            if (nv == null) return;
            string nk = NormalizeForReadbackMatch(key);
            if (nk != null && string.Equals(nk, nv, StringComparison.Ordinal)) return;
            readbackTranslations.TryAdd(nv, 0);
        }

        /// <summary>
        /// True when the text is one of our own translations handed back by the game with a different
        /// decoration. Callers must treat it exactly like an exact reverse-cache hit: leave the text
        /// alone. Nothing on screen changes — the game's own rendering is kept as the developer built
        /// it; we simply refuse to learn from it.
        /// </summary>
        /// <summary>
        /// "This text is ALREADY in the target language — do not translate it." The single question
        /// every gate must ask, and the single place that answers it: the exact reverse cache, then
        /// the decoration-insensitive index.
        /// Deliberately NOT merged into HasCachedTranslation, which answers the opposite question —
        /// "does a translation exist for this SOURCE text?" — for the mod's own labels and the
        /// scanner refresh. A read-back match there would be a false yes on an untranslated label.
        /// Conflating the two is what let each new path re-implement its own variant and forget a
        /// case; every gate calls this now, so a path added later asks the right thing by default.
        /// </summary>
        /// <param name="normalizedTrimmed">Already-normalized+trimmed form when the caller has it,
        /// to avoid normalizing twice on the set_text path.</param>
        public static bool IsAlreadyTargetText(string text, string normalizedTrimmed = null)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string probe = normalizedTrimmed ?? NormalizeForCacheLookup(text).TrimEnd();
            if (translatedTexts.ContainsKey(probe)) return true;
            return IsReadbackOfOwnTranslation(text);
        }

        public static bool IsReadbackOfOwnTranslation(string text)
        {
            if (readbackTranslations.Count == 0) return false;
            string n = NormalizeForReadbackMatch(text);
            if (n == null) return false;
            if (!readbackTranslations.ContainsKey(n)) return false;

            if (_readbackSkipLogCount < ReadbackSkipLogBudget)
            {
                _readbackSkipLogCount++;
                LogDebug($"[Readback] Not queued, this is our own translation re-decorated by the game: '{(text.Length > 60 ? text.Substring(0, 60) + "..." : text)}'");
            }
            return true;
        }

        // Component tracking: components waiting for a translation (using object to avoid Unity dependencies)
        private static Dictionary<string, List<object>> pendingComponents = new Dictionary<string, List<object>>();

        // Pattern match failure cache (texts that don't match any pattern)
        private static HashSet<string> patternMatchFailures = new HashSet<string>();

        // Texts whose translation failed placeholder validation after all retries.
        // In-memory only: never cached to disk, retried on next game launch.
        // Prevents hammering the backend every scan cycle within the same session.
        private static ConcurrentDictionary<string, byte> validationFailedTexts = new ConcurrentDictionary<string, byte>();

        // Callback for updating components when translation completes
        public static Action<string, string, List<object>> OnTranslationComplete;

        // Queue status for UI overlay
        private static bool isTranslating = false;
        private static string currentlyTranslating = null;
        public static int QueueCount { get { lock (lockObj) { return translationQueue.Count; } } }
        public static bool IsTranslating => isTranslating;
        public static string CurrentText => currentlyTranslating;
        /// <summary>True while the text being translated belongs to the mod's own interface.
        /// The overlay excerpt is meant to show GAME text; showing our own label there reads as
        /// the mod translating itself ("Translating: Translating:").</summary>
        public static bool CurrentTextIsOwnUI => currentTextIsOwnUI;
        private static bool currentTextIsOwnUI = false;

        // Own UI component tracking (mod interface)
        private static HashSet<int> ownUIExcluded = new HashSet<int>();      // Never translate (title, lang codes, config values)
        private static HashSet<int> ownUITranslatable = new HashSet<int>();  // Translate with UI-specific prompt
        private static HashSet<int> ownUIPanelRoots = new HashSet<int>();    // Root GameObjects of our panels (for hierarchy check)

        // User exclusions (chat windows, player names, etc.) - stored in translations.json as _exclusions
        private static List<string> userExclusions = new List<string>();
        // ⚠ Keyed by long, like every other per-target map: uGUI passes an instance id, UI Toolkit
        // passes an id from beyond the int range. The widening from int is implicit, so nothing
        // that used to pass a component id had to change.
        private static Dictionary<long, bool> userExclusionCache = new Dictionary<long, bool>();

        /// <summary>
        /// Current user exclusion patterns. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<string> UserExclusions => userExclusions;

        // Font overrides (per-pattern font/size rules) - stored in translations.json as _font_overrides
        private static List<FontOverrideRule> fontOverrides = new List<FontOverrideRule>();
        // Long, like userExclusionCache and the routing state: one id space for every framework.
        private static Dictionary<long, FontOverrideRule> fontOverrideCache = new Dictionary<long, FontOverrideRule>();

        /// <summary>
        /// Current font override rules. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<FontOverrideRule> FontOverrides => fontOverrides;

        /// <summary>
        /// Find the first matching font override rule for a component.
        /// Uses caching by component instance ID. Returns null if no override matches.
        /// </summary>
        /// <param name="componentId">Component instance ID for caching</param>
        /// <param name="gameObjectPath">Hierarchy path (e.g. "Canvas/Panel/Text")</param>
        /// <param name="fontName">Current font name</param>
        /// <param name="textContent">Current text content</param>
        public static FontOverrideRule FindFontOverride(long componentId, string gameObjectPath, string fontName, string textContent)
        {
            // Check cache first
            if (fontOverrideCache.TryGetValue(componentId, out var cached))
                return cached;

            FontOverrideRule matched = null;
            for (int i = 0; i < fontOverrides.Count; i++)
            {
                var rule = fontOverrides[i];
                if (!rule.enabled) continue;
                if (MatchesFontOverride(rule, gameObjectPath, fontName, textContent))
                {
                    matched = rule;
                    break; // First match wins
                }
            }

            fontOverrideCache[componentId] = matched;
            return matched;
        }

        /// <summary>
        /// Drop what these caches hold for a target that no longer exists.
        ///
        /// 🔴 Both are STRONG dictionaries keyed by id. For a Component that is harmless — ids are
        /// never reused and the maps are cleared wholesale when the rules change. For a UI Toolkit
        /// element it is not: they are recycled by the hundred, so an entry per element scrolled
        /// past would accumulate for the life of the process. The framework that holds its targets
        /// weakly calls this when one is collected.
        /// </summary>
        public static void ForgetTargetCaches(long id)
        {
            userExclusionCache.Remove(id);
            fontOverrideCache.Remove(id);
        }

        /// <summary>
        /// Whether RTL text on a component should MIRROR its horizontal alignment (left↔right)
        /// or keep the game's own. Resolution order: the matched override rule, then the font's
        /// shared setting, then mirror — the default an RTL reader expects (user-arbitrated).
        /// Null/unknown font falls through to the default rather than guessing.
        /// </summary>
        public static bool ShouldMirrorRtlAlignment(string settingsFontName, FontOverrideRule overrideRule)
        {
            string choice = overrideRule?.rtl_alignment;
            if (string.IsNullOrEmpty(choice) && !string.IsNullOrEmpty(settingsFontName)
                && FontSettingsMap.TryGetValue(settingsFontName, out var settings))
                choice = settings.rtl_alignment;
            return !string.Equals(choice, "keep", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether this game's translation involves right-to-left text AT ALL, in either
        /// direction: RTL values (an LTR game translated to Arabic) or RTL source keys (an RTL
        /// game translated out). Gates the RTL controls in the Fonts tab — they are noise for
        /// everyone else (user-arbitrated: "seulement quand utile"). Scanned on demand; callers
        /// are screens, not hot paths.
        /// </summary>
        public static bool TranslationTouchesRtl()
        {
            try
            {
                foreach (var kvp in TranslationCache)
                {
                    if (TextShaping.RtlText.ContainsStrongRtl(kvp.Key)
                        || TextShaping.RtlText.ContainsPresentationForms(kvp.Key)) return true;
                    string v = kvp.Value?.Value;
                    if (v != null && TextShaping.RtlText.ContainsStrongRtl(v)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Test if a font override rule matches the given context.
        /// </summary>
        private static bool MatchesFontOverride(FontOverrideRule rule, string path, string fontName, string text)
        {
            string match = rule.match;
            if (string.IsNullOrEmpty(match)) return false;

            // Prefix-based matching
            if (match.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(path) && MatchesExclusionPattern(path, pattern);
            }
            if (match.StartsWith("font:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(fontName) &&
                       string.Equals(fontName, pattern, StringComparison.OrdinalIgnoreCase);
            }
            if (match.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                if (string.IsNullOrEmpty(text)) return false;
                // Regex if wrapped in /.../
                if (pattern.StartsWith("/") && pattern.EndsWith("/") && pattern.Length > 2)
                {
                    string regex = pattern.Substring(1, pattern.Length - 2);
                    try { return System.Text.RegularExpressions.Regex.IsMatch(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { return false; }
                }
                return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // No prefix: try path first, then text substring
            if (!string.IsNullOrEmpty(path) && MatchesExclusionPattern(path, match))
                return true;
            if (!string.IsNullOrEmpty(text) && text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// Replace all font override rules at once (called by Apply in UI).
        /// Marks metadata dirty but does NOT save — caller should save after all changes.
        /// </summary>
        public static void SetFontOverrides(List<FontOverrideRule> rules)
        {
            fontOverrides.Clear();
            fontOverrides.AddRange(rules);
            fontOverrideCache.Clear();
            FontManager.ClearComponentScaleOverrides();
            // Clear font size caches so ApplyFontScale re-reads from true originals
            TranslatorPatches.ClearFontSizeCache();
            // Clear last-translated cache so ForceRefreshAllText doesn't early-return
            // before reaching ApplyFontScale (the "already translated" check skips font scale)
            TranslatorPatches.ClearLastTranslatedCache();
            SetMetadataDirty();
        }

        #region Settings sections (fonts, rules, images, exclusions, variables, game settings)

        // These six sections travel inside translations.json alongside the lines.
        // Building and applying them lives here, in ONE place, so that loading a
        // file, saving it, and replacing a single section on the player's request
        // can never drift apart. See analyse/metadata-visibility-and-sync.md.

        /// <summary>
        /// The section as it stands in memory, in the exact shape SaveCache
        /// writes to disk. Returns null when the section is empty.
        /// </summary>
        internal static JToken BuildSettingsSection(string section)
        {
            switch (section)
            {
                case SettingsSection.Fonts:
                    return BuildFontsSection();
                case SettingsSection.FontRules:
                    return BuildFontOverridesSection();
                case SettingsSection.Images:
                    return ImageReplacer.SaveToJson();
                case SettingsSection.Exclusions:
                    return BuildExclusionsSection();
                case SettingsSection.Variables:
                    return VariableManager.SaveToJson();
                case SettingsSection.GameSettings:
                    return BuildGameSettingsSection();
                default:
                    return null;
            }
        }

        private static JObject BuildFontsSection()
        {
            if (FontSettingsMap.Count == 0) return null;

            var fontsObj = new JObject();
            foreach (var kvp in FontSettingsMap)
            {
                var fontObj = new JObject
                {
                    ["enabled"] = kvp.Value.enabled,
                    ["fallback"] = kvp.Value.fallback,
                    ["type"] = kvp.Value.type
                };
                // Save the effective scale if not default (1.0). This is the value an
                // older mod reads (it knows neither scale_auto nor size_percent), so it
                // must carry the full materialized product for backward-compatible size.
                if (Math.Abs(kvp.Value.scale - 1.0f) > 0.001f)
                {
                    fontObj["scale"] = kvp.Value.scale;
                }
                // Persist the Phase B decomposition so a newer mod recomputes the
                // effective scale from the live design-scale + the deliberate percent.
                if (kvp.Value.scale_auto)
                    fontObj["scale_auto"] = true;
                if (Math.Abs(kvp.Value.size_percent - 1.0f) > 0.001f)
                    fontObj["size_percent"] = kvp.Value.size_percent;
                fontsObj[kvp.Key] = fontObj;
            }

            return fontsObj;
        }

        private static JArray BuildFontOverridesSection()
        {
            if (fontOverrides.Count == 0) return null;

            var overridesArray = new JArray();
            foreach (var rule in fontOverrides)
            {
                var ruleObj = new JObject { ["match"] = rule.match };
                if (!string.IsNullOrEmpty(rule.replacement))
                    ruleObj["replacement"] = rule.replacement;
                if (Math.Abs(rule.size_multiplier) > 0.001f)
                    ruleObj["size_multiplier"] = rule.size_multiplier;
                if (!rule.enabled)
                    ruleObj["enabled"] = false;
                if (!string.IsNullOrEmpty(rule.comment))
                    ruleObj["comment"] = rule.comment;
                overridesArray.Add(ruleObj);
            }

            return overridesArray;
        }

        private static JArray BuildExclusionsSection()
        {
            if (userExclusions.Count == 0) return null;

            var exclusionsArray = new JArray();
            foreach (var pattern in userExclusions)
            {
                exclusionsArray.Add(pattern);
            }

            return exclusionsArray;
        }

        private static JObject BuildGameSettingsSection()
        {
            // Written only when a value leaves its default: the absence of the
            // section is itself the information "nothing was changed here"
            var settingsObj = new JObject();
            if (DisableEventSystemOverride)
                settingsObj["disable_eventsystem_override"] = true;
            if (!TypewritingDetection)
                settingsObj["typewriting_detection"] = false;
            if (!ConcatDetection)
                settingsObj["concat_detection"] = false;
            if (!string.IsNullOrEmpty(TranslationUIFont))
                settingsObj["ui_font"] = TranslationUIFont;

            return settingsObj.Count > 0 ? settingsObj : null;
        }

        /// <summary>
        /// Parse the _fonts section. Shared by loading a file and by replacing
        /// the section on the player's request, so both read it identically.
        /// </summary>
        private static Dictionary<string, FontSettings> ParseFontsSection(JToken token)
        {
            var result = new Dictionary<string, FontSettings>();
            var obj = token as JObject;
            if (obj == null) return result;

            foreach (var fontProp in obj.Properties())
            {
                var settings = new FontSettings();
                var fontObj = fontProp.Value as JObject;
                if (fontObj != null)
                {
                    settings.enabled = fontObj["enabled"]?.Value<bool>() ?? true;
                    settings.fallback = fontObj["fallback"]?.Value<string>();
                    settings.type = fontObj["type"]?.Value<string>();
                    settings.scale = fontObj["scale"]?.Value<float>() ?? 1.0f;
                    settings.scale_auto = fontObj["scale_auto"]?.Value<bool>() ?? false;
                    // Migration: pre-B translations have no size_percent and stored the
                    // deliberate % directly in `scale` (auto was always off) → carry it
                    // over so the effective size is preserved exactly (frozen).
                    var sizePercentToken = fontObj["size_percent"];
                    settings.size_percent = sizePercentToken != null
                        ? sizePercentToken.Value<float>()
                        : (settings.scale_auto ? 1.0f : settings.scale);
                }
                result[fontProp.Name] = settings;
            }

            return result;
        }

        private static List<FontOverrideRule> ParseFontOverridesSection(JToken token)
        {
            var result = new List<FontOverrideRule>();
            var array = token as JArray;
            if (array == null) return result;

            foreach (var item in array)
            {
                var ruleObj = item as JObject;
                if (ruleObj == null) continue;
                var rule = new FontOverrideRule
                {
                    match = ruleObj["match"]?.Value<string>(),
                    replacement = ruleObj["replacement"]?.Value<string>(),
                    size_multiplier = ruleObj["size_multiplier"]?.Value<float>() ?? 0f,
                    enabled = ruleObj["enabled"]?.Value<bool>() ?? true,
                    comment = ruleObj["comment"]?.Value<string>()
                };
                if (!string.IsNullOrEmpty(rule.match))
                {
                    result.Add(rule);
                }
            }

            return result;
        }

        private static List<string> ParseExclusionsSection(JToken token)
        {
            var result = new List<string>();
            var array = token as JArray;
            if (array == null) return result;

            foreach (var item in array)
            {
                var pattern = item.ToString();
                if (!string.IsNullOrEmpty(pattern))
                {
                    result.Add(pattern);
                }
            }

            return result;
        }

        private static void ApplyGameSettingsSection(JToken token)
        {
            var settingsObj = token as JObject;

            // A missing key means the default, never "keep what I had": these
            // values are only written when they leave their default, so reading
            // a file that omits them must restore the defaults
            DisableEventSystemOverride = settingsObj?["disable_eventsystem_override"]?.Value<bool>() ?? false;
            TypewritingDetection = settingsObj?["typewriting_detection"]?.Value<bool>() ?? true;
            ConcatDetection = settingsObj?["concat_detection"]?.Value<bool>() ?? true;
            TranslationUIFont = settingsObj?["ui_font"]?.Value<string>();
            InvalidateInterfaceFontAvailability();
        }

        /// <summary>
        /// Replace one section with the given content (null = the other side has
        /// nothing here). Used when the player chooses, section by section, what
        /// to take from a downloaded translation.
        ///
        /// The caller saves and calls AfterSettingsSectionsChanged once.
        /// </summary>
        internal static void ApplySettingsSection(string section, JToken token)
        {
            switch (section)
            {
                case SettingsSection.Fonts:
                    ApplyFontsSectionPreservingInventory(token);
                    break;

                case SettingsSection.FontRules:
                    fontOverrides.Clear();
                    fontOverrides.AddRange(ParseFontOverridesSection(token));
                    break;

                case SettingsSection.Images:
                    ImageReplacer.LoadFromJson(token);
                    break;

                case SettingsSection.Exclusions:
                    userExclusions.Clear();
                    userExclusions.AddRange(ParseExclusionsSection(token));
                    break;

                case SettingsSection.Variables:
                    VariableManager.LoadFromJson(token);
                    break;

                case SettingsSection.GameSettings:
                    ApplyGameSettingsSection(token);
                    break;
            }
        }

        /// <summary>
        /// Replace the font SETTINGS while keeping the discovery inventory.
        ///
        /// FontSettingsMap holds two different things: what the translator
        /// deliberately configured, and every font the mod happened to meet
        /// in-game (FontManager adds them on sight, with defaults). Clearing the
        /// map to take someone else's fonts would throw away the inventory of a
        /// game this player has explored and the other has not — and the mod
        /// needs it to know what it can act on.
        ///
        /// So: entries present in the incoming section are overwritten, entries
        /// absent from it are reset to defaults but KEPT (with their detected
        /// type), and incoming fonts we have never seen are added.
        /// </summary>
        private static void ApplyFontsSectionPreservingInventory(JToken token)
        {
            var incoming = ParseFontsSection(token);

            foreach (var name in FontSettingsMap.Keys.ToList())
            {
                if (incoming.ContainsKey(name)) continue;

                // Known locally, unset remotely: drop OUR settings, keep the entry
                var current = FontSettingsMap[name];
                FontSettingsMap[name] = new FontSettings
                {
                    enabled = true,
                    fallback = null,
                    type = current.type,
                    scale = 1.0f,
                    scale_auto = false,
                    size_percent = 1.0f
                };
            }

            foreach (var kvp in incoming)
            {
                // Keep a locally detected type when the incoming file has none:
                // the other player may never have met this font
                if (string.IsNullOrEmpty(kvp.Value.type)
                    && FontSettingsMap.TryGetValue(kvp.Key, out var known)
                    && !string.IsNullOrEmpty(known.type))
                {
                    kvp.Value.type = known.type;
                }
                FontSettingsMap[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// Invalidate what the changed sections affect, once for the whole batch.
        /// Splitting this out keeps ApplySettingsSection free of side effects, so
        /// applying six sections does not clear the same caches six times.
        /// </summary>
        internal static void AfterSettingsSectionsChanged(IEnumerable<string> sections)
        {
            var changed = new HashSet<string>(sections ?? Enumerable.Empty<string>());
            if (changed.Count == 0) return;

            if (changed.Contains(SettingsSection.Fonts) || changed.Contains(SettingsSection.FontRules))
            {
                fontOverrideCache.Clear();
                FontManager.ClearComponentScaleOverrides();
                // Font sizes are read from true originals again
                TranslatorPatches.ClearFontSizeCache();
                // Without this, the "already translated" check returns early and
                // never reaches the font scale
                TranslatorPatches.ClearLastTranslatedCache();
            }

            if (changed.Contains(SettingsSection.Exclusions))
            {
                userExclusionCache.Clear();
            }

            if (changed.Contains(SettingsSection.Images))
            {
                ImageReplacer.LoadAllReplacements();
            }

            SetMetadataDirty();
            ClearProcessingCaches();

            LogInfo($"[Settings] Replaced sections: {string.Join(", ", changed.ToArray())}");
        }

        #endregion

        /// <summary>
        /// Clear the font override cache (call on scene change).
        /// </summary>
        public static void ClearFontOverrideCache()
        {
            fontOverrideCache.Clear();
        }

        // Panel construction mode: when true, all translations are skipped
        // This prevents texts created during panel construction from being queued before we can register them
        private static int _constructionModeCount = 0;
        private static object _constructionModeLock = new object();

        /// <summary>
        /// Enter panel construction mode. While active, all translations are skipped.
        /// Call this before creating panel UI elements. Supports nested calls (reference counted).
        /// </summary>
        public static void EnterConstructionMode()
        {
            lock (_constructionModeLock)
            {
                _constructionModeCount++;
            }
        }

        /// <summary>
        /// Exit panel construction mode. Decrements the reference count.
        /// </summary>
        public static void ExitConstructionMode()
        {
            lock (_constructionModeLock)
            {
                if (_constructionModeCount > 0)
                    _constructionModeCount--;
            }
        }

        /// <summary>
        /// Returns true if we're currently in panel construction mode.
        /// </summary>
        public static bool IsInConstructionMode
        {
            get
            {
                lock (_constructionModeLock)
                {
                    return _constructionModeCount > 0;
                }
            }
        }

        /// <summary>
        /// Set while RestoreAllOriginals rewrites displayed texts back to their
        /// originals (cache reload): the text patches must not re-translate those
        /// writes — the outgoing cache is still loaded at that point and would
        /// reapply the stale translation over the restored original.
        /// </summary>
        public static volatile bool SuppressTranslationPatches = false;

        /// <summary>
        /// Register a component to be excluded from translation (mod title, language codes, config values).
        /// </summary>
        public static void RegisterExcluded(UnityEngine.Object component)
        {
            if (component != null)
                ownUIExcluded.Add(component.GetInstanceID());
        }

        /// <summary>
        /// Register a component to be translated with UI-specific prompt (labels, buttons).
        /// </summary>
        public static void RegisterUIText(UnityEngine.Object component)
        {
            if (component != null)
                ownUITranslatable.Add(component.GetInstanceID());
        }

        /// <summary>
        /// Register a panel root GameObject. All children will be identified as own UI via hierarchy check.
        /// Call this BEFORE creating any child components.
        /// </summary>
        public static void RegisterPanelRoot(GameObject panelRoot)
        {
            if (panelRoot != null)
            {
                ownUIPanelRoots.Add(panelRoot.GetInstanceID());
                // Clear hierarchy cache — components checked before this registration
                // may have been cached as "not own UI" and need re-evaluation
                _ownUIHierarchyCache.Clear();
            }
        }

        // Cache for IsOwnUIByHierarchy results (avoids repeated hierarchy traversal)
        // Key: component instanceId, Value: is own UI
        // Cleared on scene change (components become invalid)
        private static readonly Dictionary<int, bool> _ownUIHierarchyCache = new Dictionary<int, bool>();

        /// <summary>
        /// Check if a component is part of our UI by traversing up the hierarchy.
        /// Returns true if any parent is a registered panel root.
        /// Results are cached per instanceId to avoid repeated traversal.
        /// </summary>
        public static bool IsOwnUIByHierarchy(Component component)
        {
            if (component == null) return false;

            int id = component.GetInstanceID();
            if (_ownUIHierarchyCache.TryGetValue(id, out bool cached))
                return cached;

            bool result = false;
            Transform current = component.transform;
            while (current != null)
            {
                if (ownUIPanelRoots.Contains(current.gameObject.GetInstanceID()))
                {
                    result = true;
                    break;
                }
                current = current.parent;
            }

            // Both answers are cached. A NO can change when a panel root is registered — and
            // RegisterPanelRoot clears this cache for exactly that reason — so caching only the
            // YES meant every ordinary game component re-walked its whole parent chain on every
            // scanner pass (measured with the batch loop at 25 % of a core on a uGUI game).
            _ownUIHierarchyCache[id] = result;
            return result;
        }

        #region User Exclusions

        /// <summary>
        /// Get the full hierarchy path of a GameObject (e.g., "Canvas/Panel/ChatWindow/MessageList").
        /// Used for exclusion pattern matching.
        /// </summary>
        public static string GetGameObjectPath(GameObject obj)
        {
            if (obj == null) return "";

            var parts = new List<string>();
            var current = obj.transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>
        /// Check if a component is excluded by user-defined patterns.
        /// Uses caching for performance.
        /// </summary>
        public static bool IsUserExcluded(Component component)
        {
            // Before the instance id: this runs on every text write, and reading an id off an
            // IL2CPP proxy is not free when nobody has written a single pattern.
            if (component == null || userExclusions.Count == 0) return false;

            long id = component.GetInstanceID();
            if (TryCachedExclusion(id, out bool cached)) return cached;

            return RememberExclusion(id, GetGameObjectPath(component.gameObject));
        }

        /// <summary>True when any patterns exist at all. Ask before building a path.</summary>
        public static bool HasExclusionPatterns => userExclusions.Count > 0;

        /// <summary>
        /// The answer already known for this target, if there is one.
        ///
        /// 🔴 Split from <see cref="RememberExclusion"/> so a caller can check the cache BEFORE
        /// building a path. Walking a hierarchy on every text write, only to hit a cache, is the
        /// kind of cost that does not show up anywhere and never goes away — and a version taking
        /// the path as a callback was worse: it allocated a closure per call for anyone who had
        /// written a single pattern.
        /// </summary>
        public static bool TryCachedExclusion(long id, out bool excluded)
        {
            return userExclusionCache.TryGetValue(id, out excluded);
        }

        /// <summary>
        /// Decide, and remember, whether this path is excluded.
        ///
        /// 🔴 The decision itself, shared by every framework: only the PATH is theirs. A second
        /// set of exclusion rules would mean one written pattern meaning two different things
        /// depending on what the label happens to be made of.
        /// </summary>
        public static bool RememberExclusion(long id, string path)
        {
            if (userExclusions.Count == 0) return false;

            string resolved = path ?? "";
            bool excluded = MatchesAnyExclusionPattern(resolved);
            userExclusionCache[id] = excluded;

            if (excluded)
                LogDebug($"[Exclusion] Matched: {resolved}");

            return excluded;
        }

        /// <summary>
        /// Check if a path matches any exclusion pattern.
        /// Supports: ** (any depth), * (single level), exact match.
        /// </summary>
        private static bool MatchesAnyExclusionPattern(string path)
        {
            foreach (var pattern in userExclusions)
            {
                if (MatchesExclusionPattern(path, pattern))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Match a path against an exclusion pattern.
        /// Patterns: "Canvas/Chat/**" matches any child, "**/PlayerName" matches at any depth.
        /// An exact path also matches all children (excluding "Canvas/Panel" excludes "Canvas/Panel/Text").
        /// </summary>
        public static bool MatchesExclusionPattern(string path, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            // Exact path exclusions implicitly exclude all children:
            // Pattern "Canvas/Panel" should match "Canvas/Panel", "Canvas/Panel/Child", "Canvas/Panel/Child/Text"
            // Only apply this when pattern has no wildcards (pure path exclusion)
            if (!pattern.Contains("*"))
            {
                if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Check if path is a child of the excluded path
                if (path.Length > pattern.Length && path[pattern.Length] == '/' &&
                    path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            // Wildcard pattern matching
            // ** = any number of path segments (including zero)
            // * = any single path segment name

            // Split both into segments
            var pathParts = path.Split('/');
            var patternParts = pattern.Split('/');

            return MatchPatternRecursive(pathParts, 0, patternParts, 0);
        }

        private static bool MatchPatternRecursive(string[] path, int pathIdx, string[] pattern, int patternIdx)
        {
            // Base cases
            if (patternIdx >= pattern.Length)
                return pathIdx >= path.Length;

            string patternPart = pattern[patternIdx];

            if (patternPart == "**")
            {
                // ** matches zero or more path segments
                // Try matching rest of pattern at every remaining position
                for (int i = pathIdx; i <= path.Length; i++)
                {
                    if (MatchPatternRecursive(path, i, pattern, patternIdx + 1))
                        return true;
                }
                return false;
            }

            if (pathIdx >= path.Length)
                return false;

            string pathPart = path[pathIdx];

            if (patternPart == "*")
            {
                // * matches exactly one segment (any name)
                return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
            }

            // Check if pattern part contains * as wildcard within the name
            if (patternPart.Contains("*"))
            {
                // Convert to simple wildcard matching (e.g., "Chat*" matches "ChatWindow")
                string regexPattern = "^" + Regex.Escape(patternPart).Replace("\\*", ".*") + "$";
                if (!Regex.IsMatch(pathPart, regexPattern, RegexOptions.IgnoreCase))
                    return false;
            }
            else
            {
                // Exact match (case-insensitive)
                if (!string.Equals(pathPart, patternPart, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
        }

        /// <summary>
        /// Mark metadata as modified (fonts, images, exclusions).
        /// Triggers upload detection on next sync check.
        /// </summary>
        public static void SetMetadataDirty()
        {
            if (!MetadataDirty)
            {
                MetadataDirty = true;
                LogDebug("[Sync] Metadata marked dirty");
            }
        }

        /// <summary>
        /// Reset metadata dirty flag after successful upload.
        /// </summary>
        public static void ResetMetadataDirty()
        {
            MetadataDirty = false;
        }

        /// <summary>
        /// Add a new exclusion pattern. Clears the cache.
        /// </summary>
        public static void AddExclusion(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return;
            pattern = pattern.Trim();

            if (!userExclusions.Contains(pattern))
            {
                userExclusions.Add(pattern);
                userExclusionCache.Clear();
                SetMetadataDirty();
                SaveCache();
                LogDebug($"[Exclusion] Added: {pattern}");
            }
        }

        /// <summary>
        /// Remove an exclusion pattern. Clears the cache.
        /// </summary>
        public static bool RemoveExclusion(string pattern)
        {
            if (userExclusions.Remove(pattern))
            {
                userExclusionCache.Clear();
                SetMetadataDirty();
                SaveCache();
                LogDebug($"[Exclusion] Removed: {pattern}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clear all exclusions.
        /// </summary>
        public static void ClearExclusions()
        {
            userExclusions.Clear();
            userExclusionCache.Clear();
            SaveCache();
            LogDebug("[Exclusion] Cleared all exclusions");
        }

        /// <summary>
        /// Clear the exclusion cache (call on scene change).
        /// </summary>
        public static void ClearUserExclusionCache()
        {
            userExclusionCache.Clear();
        }

        #endregion

        /// <summary>
        /// Check if a component is excluded from translation (mod title, language codes, config values).
        /// </summary>
        public static bool IsOwnUIExcluded(int instanceId) => ownUIExcluded.Contains(instanceId);

        /// <summary>
        /// Check if a component is part of our own UI (registered or in panel hierarchy).
        /// </summary>
        public static bool IsOwnUI(int instanceId) => ownUIExcluded.Contains(instanceId) || ownUITranslatable.Contains(instanceId);

        /// <summary>
        /// Check if a component is part of our own UI (by instance ID or hierarchy).
        /// </summary>
        public static bool IsOwnUI(Component component)
        {
            if (component == null) return false;
            int instanceId = component.GetInstanceID();
            return IsOwnUI(instanceId) || IsOwnUIByHierarchy(component);
        }

        // ── Mod-interface translation: what the translation asks for vs what the user overrides ──

        /// <summary>
        /// Font the translation wants the mod interface rendered with ("_settings.ui_font"), so a
        /// translator shipping a non-Latin UI can name the font that renders it. Travels with the
        /// file; the font FILES themselves come from the author's resources link (fonts/ folder).
        /// </summary>
        public static string TranslationUIFont { get; set; }

        /// <summary>True once the loaded translation contains mod-UI lines (tag "M").</summary>
        public static bool TranslationHasUILines { get; private set; }

        private static string _fontAvailabilityCheckedFor;
        private static bool _fontAvailabilityResult;

        /// <summary>
        /// Interface font in effect: the local choice wins, otherwise what the translation asks for.
        /// </summary>
        public static string EffectiveInterfaceFont
        {
            get
            {
                string local = Config?.interface_font;
                return !string.IsNullOrEmpty(local) ? local : TranslationUIFont;
            }
        }

        /// <summary>
        /// An interface font is required but absent from this machine. Cached: this is read from
        /// translation hot paths, and resolving a font name touches the filesystem.
        /// </summary>
        public static bool InterfaceFontMissing
        {
            get
            {
                string font = EffectiveInterfaceFont;
                if (string.IsNullOrEmpty(font)) return false;

                if (_fontAvailabilityCheckedFor != font)
                {
                    _fontAvailabilityCheckedFor = font;
                    _fontAvailabilityResult = AssetAvailability.IsFontAvailable(font);
                }
                return !_fontAvailabilityResult;
            }
        }

        /// <summary>Forget the cached font-availability verdict (after fonts are (un)installed).</summary>
        public static void InvalidateInterfaceFontAvailability() => _fontAvailabilityCheckedFor = null;

        /// <summary>
        /// Whether the mod's own interface should be translated right now.
        ///
        /// The local setting wins when the user expressed one; otherwise the translation decides
        /// (a file carrying "M" lines was authored with a translated UI).
        ///
        /// Overriding both: if the required font is missing we keep the interface in English. The
        /// alternative is a UI full of boxes — and unlike the game, where boxes prompt the user to
        /// open this very interface and see what resources are missing, an unreadable interface
        /// leaves nowhere to go.
        /// </summary>
        public static bool ShouldTranslateOwnUI
        {
            get
            {
                if (InterfaceFontMissing) return false;
                bool? local = Config?.translate_mod_ui;
                return local ?? TranslationHasUILines;
            }
        }

        /// <summary>
        /// Check if a component should use UI-specific prompt (own UI).
        /// Returns false when the mod interface is not being translated.
        /// Uses hierarchy check if not explicitly registered.
        /// </summary>
        public static bool IsOwnUITranslatable(int instanceId) => ShouldTranslateOwnUI && ownUITranslatable.Contains(instanceId);

        /// <summary>
        /// Check if a component should use UI-specific prompt (own UI).
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool IsOwnUITranslatable(Component component)
        {
            if (!ShouldTranslateOwnUI) return false;
            if (component == null) return false;
            int instanceId = component.GetInstanceID();
            // Check explicit registration first, then hierarchy
            if (ownUITranslatable.Contains(instanceId)) return true;
            // If in hierarchy and NOT explicitly excluded, it's translatable
            if (IsOwnUIByHierarchy(component) && !ownUIExcluded.Contains(instanceId)) return true;
            return false;
        }

        /// <summary>
        /// Check if a component should be skipped for translation entirely.
        /// True if: (1) in construction mode, (2) explicitly excluded, OR (3) own UI but translate_mod_ui is disabled.
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool ShouldSkipTranslation(int instanceId)
        {
            // Skip all translations during panel construction or original-text restore
            if (IsInConstructionMode || SuppressTranslationPatches)
                return true;
            if (ownUIExcluded.Contains(instanceId))
                return true;
            if (ownUITranslatable.Contains(instanceId) && !ShouldTranslateOwnUI)
                return true;
            return false;
        }

        /// <summary>
        /// Check if a component should be skipped for translation entirely.
        /// True if: (1) in construction mode, (2) explicitly excluded, OR (3) own UI but translate_mod_ui is disabled.
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool ShouldSkipTranslation(Component component)
        {
            // Skip all translations during panel construction or original-text restore
            if (IsInConstructionMode || SuppressTranslationPatches)
                return true;
            if (component == null) return false;

            // Check user-defined exclusions (priority - shared via translations.json)
            if (IsUserExcluded(component))
                return true;

            int instanceId = component.GetInstanceID();
            // Explicitly excluded - always skip
            if (ownUIExcluded.Contains(instanceId))
                return true;
            // Explicitly translatable - skip only if the mod interface isn't being translated
            if (ownUITranslatable.Contains(instanceId))
                return !ShouldTranslateOwnUI;
            // Part of our UI by hierarchy but NOT explicitly registered as translatable:
            // WHITELIST for the mod GUI — only RegisterUIText'd chrome (labels/buttons/hints) is
            // ever translated. Everything else in our UI (dropdown VALUES, input-field text,
            // font/language names, tags, user input) is skipped, so translating it can't corrupt
            // the UI. This is GUI-only: game text (not own UI) falls through to `return false`.
            if (IsOwnUIByHierarchy(component))
                return true;
            return false;
        }

        // Security: Maximum text length for AI translation requests (prevents DoS)
        private const int MaxAITextLength = 15000;

        // Marker for skipped translations (text not in expected source language)
        private const string SkipTranslationMarker = Answers.SkipMarker;

        // Placeholder format for extracted numbers: [!v*0], [!v*1], etc.
        // Exotic format to avoid collision with game text (e.g. [v0] used by some games).
        private const string PlaceholderPrefix = "[!v*";
        private const string PlaceholderSuffix = "]";
        // Current engine version for migration support
        private const int CurrentEngineVersion = 1;

        private static readonly Regex NumberPattern = new Regex(
            @"(?<!\[!v\*)(-?\d+(?:[.,]\d+)?%?)",
            RegexOptions.Compiled);

        // Matches any XML/HTML-like tag: <tag>, </tag>, <tag attr="val">, <tag/>, etc.
        private static readonly Regex MarkupTagPattern = new Regex(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private const string TagPlaceholderPrefix = "[!t*";
        private const string TagPlaceholderSuffix = "]";

        /// <summary>
        /// Remove all markup tags from a text — for comparisons against raw values
        /// (e.g. input mirrors: games wrap the typed value in color tags).
        /// </summary>
        public static string StripMarkupTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return MarkupTagPattern.Replace(text, "");
        }

        /// <summary>
        /// Extract markup tags from text, replacing them with [!t*N] placeholders.
        /// Returns the processed text and the list of extracted tags.
        /// </summary>
        public static string ExtractMarkupTags(string text, out List<string> extractedTags)
        {
            extractedTags = new List<string>();
            if (string.IsNullOrEmpty(text))
                return text;

            var matches = MarkupTagPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var result = new StringBuilder(text.Length);
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                // Append text before this tag
                result.Append(text, lastIndex, match.Index - lastIndex);
                // Replace tag with placeholder
                int tagIndex = extractedTags.Count;
                extractedTags.Add(match.Value);
                result.Append(TagPlaceholderPrefix).Append(tagIndex).Append(TagPlaceholderSuffix);
                lastIndex = match.Index + match.Length;
            }

            // Append remaining text after last tag
            result.Append(text, lastIndex, text.Length - lastIndex);
            return result.ToString();
        }

        /// <summary>
        /// Restore [!t*N] placeholders back to their original markup tags.
        /// </summary>
        public static string RestoreMarkupTags(string text, List<string> tags)
        {
            if (string.IsNullOrEmpty(text) || tags == null || tags.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < tags.Count; i++)
            {
                result = result.Replace($"{TagPlaceholderPrefix}{i}{TagPlaceholderSuffix}", tags[i]);
            }
            return result;
        }

        // The placeholder rules moved to UnityGameTranslator.Common.Placeholders: what a game
        // accepts back from a model, and what to say to one that broke it. They were reproduced in
        // the manager to score models, and a reproduction is precisely what must not exist — what
        // the tests measure has to be what a game enforces, down to the sentence sent back on the
        // second attempt.

        public class PatternEntry
        {
            public string OriginalPattern;
            public string TranslatedPattern;
            public Regex MatchRegex;
            public List<int> PlaceholderIndices;
        }

        // Managed id of the Unity main thread, captured in Initialize (which the
        // mod loaders always call on it). Unity APIs are main-thread only; on
        // IL2CPP an off-thread call dies with a native, uncatchable
        // AccessViolationException instead of a managed exception.
        private static int _mainThreadId = -1;

        /// <summary>
        /// True when the current thread is the Unity main thread.
        /// Patches invoked from middleware background threads (e.g. Rewired's
        /// input thread) must check this before touching any Unity API.
        /// </summary>
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Initialize the translation core
        /// </summary>
        public static void Initialize(IModLoaderAdapter adapter)
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Instance = new TranslatorCore();
            Adapter = adapter;

            // The adapter is what names the loader, so the User-Agent can only be complete from
            // here — see ApiClient.RefreshUserAgent. Done before anything can make a call.
            ApiClient.RefreshUserAgent();

            // Catch-all for exceptions that escape from async void methods, raw
            // threads, or anything else that bypasses our explicit try/catch
            // blocks. Without this the host (Unity, BepInEx, MelonLoader) may
            // tear down the process on the first unobserved exception, leaving
            // the user with no diagnostic.
            // SetObserved() prevents the BCL from re-raising the unobserved
            // task exception event, which on some runtimes does crash the app.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Adapter?.LogError($"[Unhandled] {ex?.GetType().Name ?? "?"}: {ex?.Message}\n{ex?.StackTrace}");
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Adapter?.LogError($"[UnobservedTask] {e.Exception?.Flatten().GetBaseException().Message}\n{e.Exception?.StackTrace}");
                e.SetObserved();
            };

            // Use the folder provided by the adapter directly (no subfolder)
            ModFolder = adapter.GetPluginFolder();

            if (!Directory.Exists(ModFolder))
                Directory.CreateDirectory(ModFolder);

            CachePath = Path.Combine(ModFolder, "translations.json");
            ConfigPath = Path.Combine(ModFolder, "config.json");

            LoadConfig();
            DebugMode = Config.debug;

            // Always-on environment snapshot. Logged at LogInfo level (not LogDebug) so it
            // ships in user reports without them having to flip a debug flag first. Cheap
            // to produce and pays for itself the first time we receive a "doesn't work on
            // my machine" log — we already have the Unity version, OS, GPU, driver, RAM
            // and chosen mod loader without a second round-trip.
            LogRuntimeEnvironment();

            // Initialize type resolution (must be before patches and scanning)
            TypeHelper.Initialize();

            // Initialize font manager for non-Latin script support
            FontManager.Initialize();

            // Initialize custom font loader (user-provided SDF fonts)
            CustomFontLoader.Initialize(ModFolder);

            // Initialize image replacer for bitmap text translation
            ImageReplacer.Initialize(ModFolder);

            httpClient = CreateHttpClient(Config);

            // Detect game
            CurrentGame = GameDetector.DetectGame();
            if (CurrentGame != null)
            {
                Adapter.LogInfo($"Detected game: {CurrentGame.name} (Steam: {CurrentGame.steam_id ?? "N/A"})");
            }

            LoadCache();

            // Pre-load configured fallback fonts so they're ready for first use
            FontManager.PreloadConfiguredFallbacks();

            StartTranslationWorker();

            if (Config.preload_model && Config.enable_ai && Config.translation_backend == "llm")
            {
                PreloadModel();
            }

            Adapter.LogInfo($"UnityGameTranslator v{PluginInfo.Version} initialized!");
            if (Config.IsTranslationEnabled)
            {
                string backendName = Config.translation_backend == "llm"
                    ? $"LLM ({Config.ai_model} @ {Sanitize.Url(Config.ai_url)})"
                    : Config.translation_backend == "google"
                        ? "Google Translate"
                        : Config.translation_backend == "deepl"
                            ? $"DeepL ({(Config.deepl_use_free ? "Free" : "Pro")})"
                            : Config.translation_backend;
                Adapter.LogInfo($"Translation: ENABLED - Backend: {backendName}");
            }
            string srcLang = Config.GetSourceLanguage() ?? "auto-detect";
            string tgtLang = Config.GetTargetLanguage();
            Adapter.LogInfo($"Translation: {srcLang} -> {tgtLang}");
            Adapter.LogInfo($"Cache entries: {TranslationCache.Count}, Pattern entries: {PatternEntries.Count}");
        }

        public static void OnSceneChanged(string sceneName)
        {
            lastSeenText.Clear();
            _ownUIHierarchyCache.Clear();
            TranslatorScanner.OnSceneChange();
            TranslatorPatches.ClearCache();

            FontManager.OnSceneChanged();

            // Load/reload image replacements for the new scene
            ImageReplacer.OnSceneChange();

            // Flag variables for refresh on next text request
            // (instances don't exist yet at scene change time)
            VariableManager.MarkNeedsRefresh();

            if (DebugMode)
                Adapter?.LogInfo($"Scene: {sceneName}");
        }

        /// <summary>
        /// Called when a scene is unloaded. Cleans up stale references from the old scene.
        /// </summary>
        public static void OnSceneUnloaded(string sceneName)
        {
            // Clean dead refs immediately (don't wait for periodic cleanup)
            FontManager.CleanDeadComponentRefs();
            TranslatorPatches.CleanDeadRefs();
            TranslatorPatches.ClearTypewritingState();

            if (DebugMode)
                Adapter?.LogInfo($"Scene unloaded: {sceneName}");
        }

        public static void OnShutdown()
        {
            ShuttingDown = true;
            Adapter?.LogInfo("[Shutdown] Starting cleanup...");

            // Hand back any Input System device we took. A game left with its keyboard disabled is
            // unplayable, and nothing else would ever put that right — first, before anything that
            // could fail and skip it.
            try { UniverseLib.Input.InputCapture.ReleaseAll(); } catch { }
            // A game left frozen would be unplayable, and nothing else would put it right.
            try { GamePause.Release(); } catch { }

            // Stop SSE streams (background tasks with HTTP connections)
            try { TranslatorUIManager.StopSyncWatch(); } catch { }
            try { TranslatorUIManager.StopMergeCompletionListener(); } catch { }

            // Closing the game is one of the two legitimate session-end
            // events — clean up the live edit session server-side (bounded
            // wait; must run BEFORE httpClient disposal below)
            try { TranslatorUIManager.EndEditSessionOnShutdown(); } catch { }

            // Stop the LateUpdate coroutine
            try { TranslatorScanner.StopLateUpdateRunner(); } catch { }

            // Remove the Canvas.willRenderCanvases subscription so the callback can't fire
            // against objects Unity is destroying during teardown (native crash on exit).
            try { FontManager.UnsubscribeWillRenderCanvases(); } catch { }

            // Wait briefly for worker thread to notice ShuttingDown flag and exit
            if (workerRunning)
            {
                for (int i = 0; i < 20 && workerRunning; i++)
                    Thread.Sleep(10);
            }

            // Dispose HttpClient (cancels in-flight requests)
            try { httpClient?.Dispose(); } catch { }

            // A retranslation the worker never got to had its line taken out of the cache to make
            // room for an answer that will now never arrive. The save below writes the WHOLE cache,
            // so leaving it out here is how the line would vanish from the file — restored before,
            // not after.
            RestoreOutstandingRetranslations();

            if (cacheModified)
            {
                try { SaveCache(); } catch { }
            }

            Adapter?.LogInfo($"Session: {translatedCount} translations, {cacheHitCount} cache hits, {aiTranslationCount} AI calls");
            Adapter?.LogInfo($"Skipped: {skippedAlreadyTranslated} (reverse cache)");
            Adapter?.LogInfo("[Shutdown] Cleanup complete");
        }

        /// <summary>
        /// Has anybody agreed to what this mod does to this game yet?
        ///
        /// Until they have, the mod may keep its own plumbing alive but must not act on the game:
        /// no scanning, no translating, no writing the cache back. Two doors open this latch and
        /// they write the same key, so the mod cannot tell them apart and does not need to:
        /// the first-run wizard on Finish, and the Manager — but only once its settings answer
        /// every question the wizard asks (GameConfigWriter, "not a preference, it is a latch").
        ///
        /// Deliberately NOT "is anything configured": the mod stayed quiet during the wizard only
        /// because nothing had been set up yet, which is an accident, not a decision. Someone who
        /// installs a community translation through the Manager arrives with fonts and images
        /// already configured — and those would have been applied while the wizard was still
        /// asking for permission.
        /// </summary>
        public static bool SetupCompleted => Config != null && Config.first_run_completed;

        /// <summary>
        /// The three ways this mod alters a game, each behind its own switch AND behind the latch.
        /// Read these — never <c>Config.enable_*</c> — anywhere the answer decides whether the game
        /// is touched. The raw flags stay for the screens that show and set them.
        ///
        /// ⚠ The latch is not redundant with the switches: all three default to TRUE while
        /// first_run_completed defaults to false. What kept the mod quiet during the wizard was
        /// never a decision, only the fact that a fresh install has an empty cache and no backend —
        /// and that stops being true the moment a translation is installed alongside the mod
        /// (the Manager's ordinary job) or the config alone goes missing. Observed: a game whose
        /// config.json was renamed still showed 62 translations from its cache and had its font
        /// swapped, with the wizard on screen asking for permission.
        /// </summary>
        public static bool TranslationsActive
            => SetupCompleted && Config.enable_translations;

        /// <inheritdoc cref="TranslationsActive"/>
        public static bool FontReplacementActive
            => SetupCompleted && Config.enable_font_replacement;

        /// <inheritdoc cref="TranslationsActive"/>
        public static bool ImageReplacementActive
            => SetupCompleted && Config.enable_image_replacement;

        public static void OnUpdate(float currentTime)
        {
            // Feed the scanner's adaptive frame-time budget on every frame.
            // The scanner uses recent frame-time variance to size its per-frame work budget.
            TranslatorScanner.RecordFrameTime();

            // Keep variable values (seeds, player names...) in sync with live game state
            VariableManager.OnUpdate(currentTime);

            if (cacheModified && currentTime - lastSaveTime > 30f)
            {
                lastSaveTime = currentTime;
                SaveCache();
            }
        }

        /// <summary>
        /// Toggle debug logging at runtime. DebugMode is a cached mirror of Config.debug
        /// (set at config load), so a live toggle must update both. Caller persists via SaveConfig.
        /// </summary>
        public static void SetRuntimeDebug(bool on)
        {
            if (Config != null) Config.debug = on;
            DebugMode = on;
        }

        #region Public Logging (for use by TranslatorPatches/TranslatorScanner)

        public static void LogInfo(string message) => Adapter?.LogInfo(message);
        public static void LogWarning(string message) => Adapter?.LogWarning(message);
        public static void LogError(string message) => Adapter?.LogError(message);
        /// <summary>
        /// Log only when debug mode is enabled (config.debug=true or debug.txt exists).
        /// Use for verbose/diagnostic messages that normal users don't need.
        /// </summary>
        public static void LogDebug(string message) { if (DebugMode) Adapter?.LogInfo(message); }

        /// <summary>
        /// Open a URL in the system browser, restricted to http/https. URLs reaching
        /// this point can come from the server (verification_uri, merge preview links,
        /// GitHub release assets): a hostile response must not be able to launch
        /// file://, UNC paths or custom protocol handlers via the OS shell.
        /// </summary>
        public static void OpenUrlSafe(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                UnityEngine.Application.OpenURL(url);
            }
            else
            {
                LogWarning($"[Security] Blocked attempt to open non-http(s) URL: {Sanitize.Url(url)}");
            }
        }

        /// <summary>
        /// Starts a program on this machine. Returns false when it did not start, so the caller can
        /// offer something else rather than leave a button that did nothing.
        /// </summary>
        /// <remarks>
        /// 🔴 **Separate from <see cref="OpenUrlSafe"/> on purpose, and narrower.** That one refuses
        /// anything but http(s) precisely so a URL arriving from the network or from a config file
        /// can never become a command. This one runs a program, so the rule has to be stricter, and
        /// it lives in the CALLER: the only path ever passed here is one we found ourselves on this
        /// machine — a record the Manager wrote in its own folder, or a process already running
        /// under this user. Nothing that reaches us over the network, and nothing from config.json,
        /// may be handed to this. See ManagerLink, which is its only caller.
        ///
        /// ⚠ No arguments, ever. A program is started, never given instructions — an argument list
        /// is where a path turns into a command line.
        /// </remarks>
        public static bool LaunchSafe(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return false;

            try
            {
                using (System.Diagnostics.Process.Start(path)) { }
                return true;
            }
            catch (Exception ex)
            {
                // The boundary with the operating system, which is allowed to refuse: a policy, an
                // antivirus, a runtime that stripped the class. Logged rather than swallowed, and
                // answered by the caller offering the download page instead.
                LogWarning($"[Manager] Could not start {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Config persistence

        /// <summary>
        /// Names of token fields on ModConfig that are encrypted via [JsonConverter(typeof(EncryptedTokenConverter))].
        /// Used in LoadConfig to inspect raw JSON and detect decryption failures or legacy plaintext that needs re-encryption.
        /// Keep in sync with the [JsonConverter] annotations on ModConfig properties.
        /// </summary>
        private static readonly string[] EncryptedTokenFieldNames = new[]
        {
            "api_token",
            "ai_api_key",
            "google_api_key",
            "deepl_api_key",
            "proxy_password",
        };

        /// <summary>
        /// Log a single snapshot of the runtime environment at mod startup. Every value
        /// is fetched defensively because some IL2CPP-on-exotic-Windows configurations
        /// throw on SystemInfo properties that look harmless. We never want this method
        /// to take the mod down — it's diagnostic.
        ///
        /// Captured here:
        ///  - Mod info: version, mod loader type
        ///  - Unity: version, platform, runtime version
        ///  - OS: name, processor, RAM
        ///  - GPU: name, memory, API/driver version, maxTextureSize
        /// </summary>
        private static void LogRuntimeEnvironment()
        {
            string Safe(Func<string> fn)
            {
                try { return fn() ?? "(null)"; }
                catch (Exception ex) { return $"(error: {ex.GetType().Name}: {ex.Message})"; }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Env] === Runtime environment ===");
            sb.AppendLine($"[Env]  Mod: UnityGameTranslator on {Safe(() => Adapter?.ModLoaderType ?? "(no adapter)")}");
            sb.AppendLine($"[Env]  Unity: version={Safe(() => UnityEngine.Application.unityVersion)}  platform={Safe(() => UnityEngine.Application.platform.ToString())}");
            sb.AppendLine($"[Env]  .NET: runtime={Safe(() => System.Environment.Version.ToString())}  desc={Safe(() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)}");
            sb.AppendLine($"[Env]  OS: {Safe(() => UnityEngine.SystemInfo.operatingSystem)}");
            sb.AppendLine($"[Env]  Device: {Safe(() => UnityEngine.SystemInfo.deviceModel)}  CPU: {Safe(() => UnityEngine.SystemInfo.processorType)} ×{Safe(() => UnityEngine.SystemInfo.processorCount.ToString())}");
            sb.AppendLine($"[Env]  RAM: {Safe(() => UnityEngine.SystemInfo.systemMemorySize.ToString())} MB system");
            sb.AppendLine($"[Env]  GPU: {Safe(() => UnityEngine.SystemInfo.graphicsDeviceName)}  VRAM={Safe(() => UnityEngine.SystemInfo.graphicsMemorySize.ToString())} MB  api={Safe(() => UnityEngine.SystemInfo.graphicsDeviceVersion)}");
            sb.AppendLine($"[Env]  maxTextureSize={Safe(() => UnityEngine.SystemInfo.maxTextureSize.ToString())}  supportsAlpha8={Safe(() => UnityEngine.SystemInfo.SupportsTextureFormat(UnityEngine.TextureFormat.Alpha8).ToString())}  supportsRGBA32={Safe(() => UnityEngine.SystemInfo.SupportsTextureFormat(UnityEngine.TextureFormat.RGBA32).ToString())}");
            sb.AppendLine($"[Env]  Culture: {Safe(() => System.Globalization.CultureInfo.CurrentCulture.Name)}  Encoding: {Safe(() => System.Text.Encoding.Default.WebName)}");
            sb.Append("[Env] ============================");
            LogInfo(sb.ToString());
        }

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                string defaultConfig = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, defaultConfig);
                Adapter.LogInfo("Created default config file");
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath);

                // Parse raw JSON for inspection BEFORE deserialization. The deserialization
                // step decrypts encrypted fields via [JsonConverter(typeof(EncryptedTokenConverter))],
                // so we lose visibility into what was on disk. We need the raw values to detect:
                //   - decryption failures (raw had value, in-memory becomes null)
                //   - legacy plaintext / unprefixed tokens that need re-encryption on next save
                //   - missing newly-introduced fields that should be materialized on disk
                JObject rawJson;
                try
                {
                    rawJson = JObject.Parse(json);
                }
                catch (Exception parseEx)
                {
                    // Malformed JSON: fall through to deserialization which will raise a clearer error
                    Adapter.LogWarning($"Config JSON is malformed for inspection ({parseEx.Message}); proceeding without raw inspection");
                    rawJson = new JObject();
                }

                Config = JsonConvert.DeserializeObject<ModConfig>(json) ?? new ModConfig();

                bool needsResave = false;

                // Inspect each encrypted-token field. The converter has already populated Config
                // with plaintext (or null on failure); the raw JSON tells us what was on disk.
                foreach (var fieldName in EncryptedTokenFieldNames)
                {
                    string rawValue = rawJson[fieldName]?.Value<string>();
                    if (string.IsNullOrEmpty(rawValue))
                    {
                        continue;
                    }

                    string inMemoryValue = GetTokenFieldValue(Config, fieldName);

                    if (string.IsNullOrEmpty(inMemoryValue))
                    {
                        // Decryption failed (machine identity changed, corrupted ciphertext, key algo bumped).
                        // Converter has already returned null; we resave to clear the bad ciphertext from disk.
                        Adapter.LogWarning($"Failed to decrypt {fieldName} - clearing it");
                        needsResave = true;
                    }
                    else if (TokenProtection.NeedsReEncryption(rawValue))
                    {
                        // Legacy plaintext (ugt_ prefix) or unprefixed token: in-memory holds it as-is,
                        // next save will wrap it with the ENCRYPTED: prefix via the converter.
                        LogDebug($"Migrated legacy/unencrypted {fieldName} to encrypted storage");
                        needsResave = true;
                    }
                }

                // Security: Invalidate API token if the issuing server URL changed (replay attack prevention).
                // Runs after the converter has decrypted the token; we compare against the URL stored at issue time.
                if (!string.IsNullOrEmpty(Config.api_token))
                {
                    string currentApiUrl = Config.api_base_url ?? PluginInfo.ApiBaseUrl;
                    // Normalized comparison: a trailing slash or case difference is the same
                    // server and must not wipe the token (false-positive invalidation)
                    if (!string.IsNullOrEmpty(Config.api_token_server) &&
                        !string.Equals(Config.api_token_server.TrimEnd('/'), currentApiUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        Adapter.LogWarning($"[Security] API URL changed from {Sanitize.Url(Config.api_token_server)} to {Sanitize.Url(currentApiUrl)} - invalidating token to prevent replay attacks");
                        Config.api_token = null;
                        Config.api_user = null;
                        Config.api_token_server = null;
                        needsResave = true;
                    }
                }

                // Security: warn when the AI endpoint is a remote host over plain http —
                // the ai_api_key would travel unencrypted on the network. Loopback (Ollama,
                // LM Studio, etc.) is the normal local case and stays silent.
                if (Config.enable_ai && !string.IsNullOrEmpty(Config.ai_api_key) &&
                    Uri.TryCreate(Config.ai_url, UriKind.Absolute, out var aiUri) &&
                    aiUri.Scheme == Uri.UriSchemeHttp && !aiUri.IsLoopback)
                {
                    Adapter.LogWarning($"[Security] ai_url points to a remote server over plain http ({Sanitize.Url(Config.ai_url)}) - your AI API key is sent unencrypted. Use https for remote AI servers.");
                }

                // The "check on start" switch became a frequency. false meant "never
                // look", true meant "look on every connection" — which is what the
                // permanent stream did. Existing users land on the new default rather
                // than keeping a stream open for the whole session; the choice is
                // theirs to change, and the reason is in the option's help text.
                if (Config.sync != null && Config.sync.check_update_on_start.HasValue)
                {
                    Config.sync.update_check_frequency = Config.sync.check_update_on_start.Value
                        ? UpdateCheckFrequency.Hourly
                        : UpdateCheckFrequency.Never;
                    Config.sync.check_update_on_start = null;
                    LogDebug($"[Config] Migrated check_update_on_start -> update_check_frequency={Config.sync.update_check_frequency}");
                    needsResave = true;
                }

                // 🔴 **One setting became two** (2026-08-20). The frequency decided BOTH the rhythm
                // and whether to keep a stream open, so the contributions a Main receives arrived
                // in real time — waking the game to recount its branches every time anybody sent
                // one. The stream is now its own switch, about one's OWN line only.
                //
                // ⚠ Read from the value as stored, before Normalize() folds "auto" and "realtime"
                // into a rhythm: those two are precisely the ones that asked for a connection, and
                // once folded there is no way left to tell they did. Every other value never
                // opened one, so it answers no — handing somebody a permanent connection they had
                // declined is the one outcome this migration must not produce.
                //
                // ⚠ The RAW json decides, not the property: it defaults to true for a new install
                // (which never reaches this code — LoadConfig returns after writing the file), so
                // the property alone cannot tell an absent field from a deliberate yes.
                if (Config.sync != null && rawJson["sync"]?["realtime_own_translation"] == null)
                {
                    string stored = Config.sync.update_check_frequency;

                    Config.sync.realtime_own_translation =
                        UpdateCheckFrequency.AskedForRealtime(stored);
                    Config.sync.update_check_frequency = UpdateCheckFrequency.Normalize(stored);

                    LogDebug($"[Config] Split update_check_frequency={stored} -> "
                             + $"{Config.sync.update_check_frequency} + realtime_own_translation="
                             + $"{Config.sync.realtime_own_translation}");
                    needsResave = true;
                }

                if (Config._configMigrated)
                {
                    LogDebug($"[Config] Migrated old Ollama config -> AI config (enable_ai={Config.enable_ai}, ai_url={Sanitize.Url(Config.ai_url)}, ai_model={Config.ai_model})");
                    needsResave = true;
                }

                // Materialize newly-introduced fields so users can see them in config.json on first load.
                // When a new ModConfig field is added, deserialization fills it with its default value
                // but the existing file does not contain the JSON property; saving once writes it.
                if (rawJson["max_text_detection_latency_seconds"] == null)
                {
                    needsResave = true;
                }

                if (needsResave)
                {
                    SaveConfig();
                }

                LogDebug($"Loaded config (enable_translations={Config.enable_translations}, backend={Config.translation_backend}, ai_url={Sanitize.Url(Config.ai_url)}, ai_model={Config.ai_model})");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load config: {e.Message}");
            }
        }

        /// <summary>
        /// Token-field accessor used by LoadConfig's raw-JSON inspection loop.
        /// Centralized to avoid drift if the field names change.
        /// </summary>
        private static string GetTokenFieldValue(ModConfig config, string fieldName)
        {
            switch (fieldName)
            {
                case "api_token":      return config.api_token;
                case "ai_api_key":     return config.ai_api_key;
                case "google_api_key": return config.google_api_key;
                case "deepl_api_key":  return config.deepl_api_key;
                case "proxy_password": return config.proxy_password;
                default:
                    Adapter?.LogWarning($"[Config] Unknown encrypted token field: {fieldName}");
                    return null;
            }
        }

        /// <summary>
        /// Drop the API session locally: forget the token, the account it belonged to and the
        /// server state derived from it. Used both by the Logout button and when the server
        /// refuses our token (revoked from the website, or deleted along with a ban) — in that
        /// case keeping a "signed in" account would only produce actions that silently fail.
        /// The caller refreshes the UI; translations and local work are untouched.
        /// </summary>
        /// <summary>
        /// Forget this account locally. The server is not told, and for a good reason at each of
        /// the two call sites: one has just been refused by it, the other calls
        /// <see cref="SignOut"/> instead.
        /// </summary>
        public static void ClearApiSession()
        {
            Config.api_token = null;
            Config.api_user = null;
            Config.api_token_server = null;
            SaveConfig();
            ApiClient.SetAuthToken(null);
            ServerState = null;
        }

        /// <summary>
        /// Signing out on purpose: hand the access back to the server, and forget it here.
        /// </summary>
        /// <remarks>
        /// 🔴 Local first, always. Signing out cannot be made to wait on a network call, or a site
        /// that is down would leave somebody signed in on a machine they are trying to leave.
        ///
        /// ⚠ Without this the access simply stays in the account's list for ever: nothing else ever
        /// removes it, since the site cannot tell a forgotten token from a quiet one. That is why
        /// the failure is reported rather than swallowed — the way out is to cut it from the
        /// account's own "Linked devices" screen.
        /// </remarks>
        /// <param name="serverAnswered">
        /// Called off the main thread with whether the server took the revocation.
        /// </param>
        public static void SignOut(Action<bool> serverAnswered = null)
        {
            string token = Config.api_token;

            ClearApiSession();

            if (string.IsNullOrEmpty(token))
            {
                serverAnswered?.Invoke(true);
                return;
            }

            Task.Run(async () =>
            {
                bool revoked = await ApiClient.RevokeToken(token);
                serverAnswered?.Invoke(revoked);
            });
        }

        public static void SaveConfig()
        {
            try
            {
                // Token fields with [JsonConverter(typeof(EncryptedTokenConverter))] are encrypted
                // on serialization automatically. No manual field-by-field copy is needed —
                // adding a new ModConfig field will be persisted on the next save without changing this method.
                string json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
                LogDebug("Config saved");
            }
            catch (Exception e)
            {
                Adapter?.LogError($"Failed to save config: {e.Message}");
            }
        }

        #endregion

        private static void LoadCache()
        {
            // Reset server state (will be populated by check-uuid if online). Kept aside first:
            // a reload that lands on the SAME lineage has learned nothing new about the server, and
            // dropping what we knew made the panel fall back to "Not shared yet" until the next
            // check-uuid answered — the translation appeared to leave the server and come back after
            // validating a comparison. Only a different UUID is genuinely unknown territory.
            var previousServerState = ServerState;
            string previousUuid = FileUuid;
            ServerState = null;

            // Re-derived from the file being loaded (a reload may drop what a previous file carried)
            TranslationHasUILines = false;
            // Game settings are written only when they leave their default, so a
            // file WITHOUT the section means "everything default" — not "keep
            // whatever the previous file set". Only ui_font was being reset here,
            // so after loading a translation that disabled nothing, typewriting
            // and concat detection stayed off from the previous one. Same
            // reasoning as MetadataDirty/LocalChangesCount just below.
            ApplyGameSettingsSection(null);
            // Both are written only when non-default, so their ABSENCE from the file means "clean".
            // Without resetting them here the parse below simply never assigns, and the in-memory
            // value survives the reload: after downloading the server's copy — which carries
            // neither key — the mod still claimed unsynced settings and local changes, with no way
            // for the user to clear it.
            MetadataDirty = false;
            LocalChangesCount = 0;

            if (!File.Exists(CachePath))
            {
                // Generate UUID for new translation file
                FileUuid = Guid.NewGuid().ToString();
                Adapter.LogInfo($"No cache file found, starting fresh with UUID: {FileUuid}");
                SaveCache(); // Save immediately to persist UUID
                return;
            }

            try
            {
                string json = File.ReadAllText(CachePath);
                // Normalize line endings to prevent key mismatches (Windows editors may add \r\n)
                json = json.Replace("\r\n", "\n");

                // Parse as JObject to handle metadata
                var parsed = JObject.Parse(json);
                TranslationCache = new Dictionary<string, TranslationEntry>();
                // Fresh cache: allow own-UI labels that failed once to be submitted again.
                lock (lockObj) { ownUISubmitted.Clear(); }

                // Track saved _game.steam_id to compare with current detection
                string savedSteamId = null;
                int engineVersion = 0;

                // Extract metadata and translations
                foreach (var prop in parsed.Properties())
                {
                    if (prop.Name == "_engine_version")
                    {
                        engineVersion = prop.Value.Value<int>();
                    }
                    else if (prop.Name == "_uuid")
                    {
                        FileUuid = prop.Value.ToString();
                    }
                    else if (prop.Name == "_local_changes")
                    {
                        LocalChangesCount = prop.Value.Value<int>();
                    }
                    else if (prop.Name == "_metadata_dirty")
                    {
                        MetadataDirty = prop.Value.Value<bool>();
                    }
                    else if (prop.Name == "_source" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load source info for sync detection
                        var source = prop.Value as JObject;
                        LastSyncedHash = source?["hash"]?.Value<string>();
                        LastMergedMainHash = source?["main_hash"]?.Value<string>();
                        SourceSiteId = source?["site_id"]?.Value<int?>();
                    }
                    else if (prop.Name == "_forked_from" && prop.Value.Type == JTokenType.Object)
                    {
                        var origin = prop.Value as JObject;
                        ForkedFromSiteId = origin?["site_id"]?.Value<int?>();
                        ForkedFromHash = origin?["hash"]?.Value<string>();
                        ForkedFromResolvedLines = origin?["resolved_lines"]?.Value<int?>();
                        // ⚠ Absent from a file forked before this key existed. Left null, which
                        // reads as "we cannot tell" — see ForkIsStillTheCopy.
                        ForkedFromContentHash = origin?["content_hash"]?.Value<string>();
                    }
                    else if (prop.Name == "_game" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load saved steam_id for comparison with current detection
                        var game = prop.Value as JObject;
                        savedSteamId = game?["steam_id"]?.Value<string>();
                    }
                    else if (prop.Name == "_exclusions" && prop.Value.Type == JTokenType.Array)
                    {
                        userExclusions.Clear();
                        userExclusions.AddRange(ParseExclusionsSection(prop.Value));
                        LogDebug($"[LoadCache] Loaded {userExclusions.Count} user exclusions");
                    }
                    else if (prop.Name == "_image_replacements")
                    {
                        ImageReplacer.LoadFromJson(prop.Value);
                    }
                    else if (prop.Name == "_variables")
                    {
                        VariableManager.LoadFromJson(prop.Value);
                    }
                    else if (prop.Name == "_fonts" && prop.Value.Type == JTokenType.Object)
                    {
                        // Loading a FILE replaces the map wholesale, inventory
                        // included: the file is the whole truth at startup, and
                        // the inventory rebuilds itself as the player plays.
                        // (Replacing this section on the player's request is a
                        // different move — see ApplyFontsSectionPreservingInventory.)
                        FontSettingsMap.Clear();
                        foreach (var kvp in ParseFontsSection(prop.Value))
                        {
                            FontSettingsMap[kvp.Key] = kvp.Value;
                        }
                        LogDebug($"[LoadCache] Loaded {FontSettingsMap.Count} font settings");
                    }
                    else if (prop.Name == "_font_overrides" && prop.Value.Type == JTokenType.Array)
                    {
                        fontOverrides.Clear();
                        fontOverrideCache.Clear();
                        fontOverrides.AddRange(ParseFontOverridesSection(prop.Value));
                        LogDebug($"[LoadCache] Loaded {fontOverrides.Count} font override rules");
                    }
                    else if (prop.Name == "_settings" && prop.Value.Type == JTokenType.Object)
                    {
                        ApplyGameSettingsSection(prop.Value);
                        LogDebug($"[LoadCache] Loaded settings: DisableEventSystemOverride={DisableEventSystemOverride}, TW={TypewritingDetection}, Concat={ConcatDetection}");
                    }
                    else if (!prop.Name.StartsWith("_"))
                    {
                        // Normalize key line endings for cross-platform consistency
                        string normalizedKey = NormalizeLineEndings(prop.Name);

                        // Handle both new format (object with v/t/i) and legacy format (string)
                        TranslationEntry newEntry;
                        if (prop.Value.Type == JTokenType.Object)
                        {
                            // New format: {"v": "value", "t": "A", "i": 123}
                            var obj = prop.Value as JObject;
                            newEntry = new TranslationEntry
                            {
                                // Normalize value line endings too
                                Value = NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                                Tag = obj?["t"]?.ToString() ?? "A",
                                Index = ParseTranslationIndex(obj?["i"])
                            };
                        }
                        else if (prop.Value.Type == JTokenType.String)
                        {
                            // Legacy format: string value - convert to AI tag
                            newEntry = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(prop.Value.ToString()),
                                Tag = "A"  // Default to AI for legacy data
                            };
                            cacheModified = true;  // Will save in new format
                        }
                        else
                        {
                            continue;
                        }

                        // Handle duplicates after normalization (e.g., "LB\r\n" and "LB\n" become same key)
                        if (TranslationCache.TryGetValue(normalizedKey, out var existingEntry))
                        {
                            // Tag priority: H > V > A (Human > Validated > AI)
                            int GetPriority(string tag) => tag == "H" ? 3 : tag == "V" ? 2 : 1;

                            if (GetPriority(newEntry.Tag) > GetPriority(existingEntry.Tag))
                            {
                                TranslationCache[normalizedKey] = newEntry;
                                cacheModified = true;
                            }
                            // Otherwise keep existing (higher or same priority)
                        }
                        else
                        {
                            TranslationCache[normalizedKey] = newEntry;
                        }

                        // "M" lines mean this file was authored with a translated mod interface —
                        // that's what decides for users who never set the option themselves.
                        if (newEntry.Tag == "M") TranslationHasUILines = true;

                        // Mark modified if key was normalized
                        if (normalizedKey != prop.Name)
                        {
                            cacheModified = true;
                        }
                    }
                }

                // Generate UUID if not present
                if (string.IsNullOrEmpty(FileUuid))
                {
                    FileUuid = Guid.NewGuid().ToString();
                    cacheModified = true;
                    LogDebug($"Legacy cache file, generated UUID: {FileUuid}");
                }

                // Capture-order index: recompute the counter from the file, then
                // backfill entries without one (legacy files, older mod versions,
                // web-editor-created keys) in ALPHABETICAL key order — deterministic,
                // so every device produces identical indices from the same file.
                // "i" is excluded from the content hash on both mod and website,
                // so this never affects sync/update detection.
                long highestIndex = 0;
                List<string> keysWithoutIndex = null;
                foreach (var kvp in TranslationCache)
                {
                    if (kvp.Value.Index.HasValue)
                    {
                        if (kvp.Value.Index.Value > highestIndex)
                            highestIndex = kvp.Value.Index.Value;
                    }
                    else
                    {
                        if (keysWithoutIndex == null)
                            keysWithoutIndex = new List<string>();
                        keysWithoutIndex.Add(kvp.Key);
                    }
                }
                long nextIndex = highestIndex + 1;
                if (keysWithoutIndex != null)
                {
                    keysWithoutIndex.Sort(StringComparer.Ordinal);
                    foreach (var key in keysWithoutIndex)
                    {
                        TranslationCache[key].Index = nextIndex++;
                    }
                    cacheModified = true;
                    LogDebug($"[LoadCache] Backfilled capture-order index on {keysWithoutIndex.Count} entries");
                }
                lock (lockObj)
                {
                    nextTranslationIndex = nextIndex;
                }

                // Update _game.steam_id if we detected one but file didn't have it
                if (CurrentGame != null && !string.IsNullOrEmpty(CurrentGame.steam_id))
                {
                    if (string.IsNullOrEmpty(savedSteamId) || savedSteamId != CurrentGame.steam_id)
                    {
                        cacheModified = true;
                        LogDebug($"[LoadCache] Detected steam_id ({CurrentGame.steam_id}) differs from saved ({savedSteamId ?? "null"}), will update file");
                    }
                }

                // Load ancestor cache if exists (for 3-way merge support)
                LoadAncestorCache();

                // Migrate old placeholder format [vN] → [!v*N] if needed
                if (engineVersion < CurrentEngineVersion)
                {
                    int migrated = MigratePlaceholderFormat(TranslationCache);
                    if (migrated > 0)
                    {
                        LogDebug($"[LoadCache] Migrated {migrated} entries from [vN] to [!v*N] format (v{engineVersion} → v{CurrentEngineVersion})");
                        cacheModified = true; // Will trigger save
                    }
                }

                // Recalculate LocalChangesCount based on actual differences (always, even if no ancestor)
                int claimedByTheFile = LocalChangesCount;
                RecalculateLocalChanges();

                // ⚠ And when the file was wrong, mark it to be rewritten. Recounting at save time
                // stops the number going stale from here on, but it repairs nothing already on
                // disk: a file left claiming published changes would go on claiming them until
                // something else happened to trigger a save, which for a finished translation could
                // be never. Anything reading it from outside — the installer's list, for one — would
                // keep showing work waiting to be shared that was shared long ago.
                //
                // Measured on a real game before this existed: 6334 lines, ancestor identical, no
                // deletions, and the file said one change was unpublished.
                if (claimedByTheFile != LocalChangesCount)
                {
                    cacheModified = true;
                    LogDebug($"[LoadCache] The file claimed {claimedByTheFile} local change(s) and "
                             + $"there are {LocalChangesCount}; it will be rewritten.");
                }

                // Build reverse cache: all translated values (NORMALIZED for comparison)
                // Values must be normalized the same way as incoming text in TranslateTextWithTracking
                // ALSO trim trailing whitespace/newlines because TMP often strips them when displaying
                translatedTexts.Clear();
                readbackTranslations.Clear();
                presentedToLogical.Clear();
                _readbackSkipLogCount = 0;
                foreach (var kv in TranslationCache)
                {
                    if (kv.Key != kv.Value.Value && !string.IsNullOrEmpty(kv.Value.Value))
                    {
                        string normalizedValue = NormalizeLineEndings(kv.Value.Value);
                        if (Config.normalize_numbers)
                        {
                            normalizedValue = ExtractNumbersToPlaceholders(normalizedValue, out _);
                        }
                        normalizedValue = normalizedValue.TrimEnd();
                        translatedTexts.TryAdd(normalizedValue, 0);
                        IndexReadbackTranslation(kv.Key, kv.Value.Value);
                    }
                }

                BuildPatternEntries();
                // Same lineage as before the reload: keep what we already knew about the server
                // instead of claiming the translation isn't shared until check-uuid answers again.
                // The hash it carries may be stale, which is harmless — sync detection re-reads it
                // from the next check — whereas a null reads as "never uploaded".
                if (previousServerState != null && !string.IsNullOrEmpty(previousUuid)
                    && string.Equals(previousUuid, FileUuid, StringComparison.OrdinalIgnoreCase))
                {
                    ServerState = previousServerState;
                }

                // Audit: NAME any entry carrying presentation forms. The write doors refuse them
                // now, but a file polluted before those doors existed (it happened once) would
                // otherwise sit silent — and its entries re-apply at every startup. This is how
                // such lines are identified for cleaning.
                int shapedKeys = 0, shapedValues = 0, shapedNamed = 0;
                foreach (var kvp in TranslationCache)
                {
                    bool badKey = TextShaping.RtlText.ContainsPresentationForms(kvp.Key);
                    bool badValue = kvp.Value?.Value != null && TextShaping.RtlText.ContainsPresentationForms(kvp.Value.Value);
                    if (badKey) shapedKeys++;
                    if (badValue) shapedValues++;
                    if ((badKey || badValue) && shapedNamed < 5)
                    {
                        shapedNamed++;
                        string k = kvp.Key.Length > 40 ? kvp.Key.Substring(0, 40) + "…" : kvp.Key;
                        Adapter.LogWarning($"[Cache audit] Presentation forms in {(badKey ? "KEY" : "value")}: '{k}' — display output saved as data; remove or re-enter this line (see issue #24).");
                    }
                }
                if (shapedKeys + shapedValues > 0)
                    Adapter.LogWarning($"[Cache audit] {shapedKeys} shaped key(s), {shapedValues} shaped value(s) in translations.json — these should not exist and will not sync cleanly.");

                Adapter.LogInfo($"Loaded {TranslationCache.Count} cached translations, {translatedTexts.Count} reverse entries, {readbackTranslations.Count} decoration-insensitive, UUID: {FileUuid}");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load cache: {e.Message}");
                TranslationCache = new Dictionary<string, TranslationEntry>();
                // Fresh cache: allow own-UI labels that failed once to be submitted again.
                lock (lockObj) { ownUISubmitted.Clear(); }
                FileUuid = Guid.NewGuid().ToString();
                lock (lockObj)
                {
                    nextTranslationIndex = 1;
                }
            }
        }

        private static void LoadAncestorCache()
        {
            string ancestorPath = CachePath + ".ancestor";
            if (!File.Exists(ancestorPath))
            {
                AncestorCache = new Dictionary<string, TranslationEntry>();
                AncestorSettings = null;
                return;
            }

            try
            {
                string ancestorJson = File.ReadAllText(ancestorPath);
                // Normalize line endings (consistency with main cache)
                ancestorJson = ancestorJson.Replace("\r\n", "\n");
                var ancestorParsed = JObject.Parse(ancestorJson);
                AncestorCache = new Dictionary<string, TranslationEntry>();
                // An ancestor written before settings travelled with it carries
                // none: that is "unknown", not "empty", so it stays null
                var ancestorSettings = TranslationSettings.FromFile(ancestorParsed);
                AncestorSettings = ancestorSettings.HasAny() ? ancestorSettings : null;

                foreach (var prop in ancestorParsed.Properties())
                {
                    if (!prop.Name.StartsWith("_"))
                    {
                        // Normalize key line endings for cross-platform consistency
                        string normalizedKey = NormalizeLineEndings(prop.Name);

                        if (prop.Value.Type == JTokenType.Object)
                        {
                            // New format
                            var obj = prop.Value as JObject;
                            AncestorCache[normalizedKey] = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                                Tag = obj?["t"]?.ToString() ?? "A"
                            };
                        }
                        else if (prop.Value.Type == JTokenType.String)
                        {
                            // Legacy format
                            AncestorCache[normalizedKey] = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(prop.Value.ToString()),
                                Tag = "A"
                            };
                        }
                    }
                }

                Adapter.LogInfo($"Loaded {AncestorCache.Count} ancestor entries for merge support");
            }
            catch (Exception ae)
            {
                Adapter.LogWarning($"Failed to load ancestor cache: {ae.Message}");
                AncestorCache = new Dictionary<string, TranslationEntry>();
                AncestorSettings = null;
            }
        }

        /// <summary>
        /// Reload the cache from disk. Call this after downloading a translation
        /// to apply it immediately without requiring a game restart.
        /// </summary>
        public static void ReloadCache()
        {
            LogDebug("[TranslatorCore] Reloading cache from disk...");

            // Snapshot the outgoing cache first: any text still displayed with an
            // old translation after the reload can then be recognized and refreshed
            // instead of being queued for AI (which would cache the old translated
            // text as a key)
            BuildStaleTranslationSnapshot();

            // Restore all displayed text to originals BEFORE loading new cache,
            // so the scanner doesn't see stale translated text from the old JSON
            // and try to re-translate it
            TranslatorScanner.RestoreAllOriginals();

            LoadCache();

            // Clear processing caches so scanner re-evaluates all text with new translations
            ClearProcessingCaches();
        }

        // ── Upstream ancestor (branches only) ────────────────────────────────
        // The Main as it stood at the last merge from it. SEPARATE from
        // AncestorCache on purpose: that one is this translation's own last synced
        // state, and feeding it to a Main→branch merge would make every key the
        // branch owns (present locally and in its ancestor, absent from the Main)
        // look like a remote deletion. See analyse/main-to-branch-sync.md §2.

        private static string MainAncestorPath => CachePath + ".mainancestor";

        /// <summary>
        /// The Main's content at the last merge from it, or an EMPTY dictionary
        /// when unknown. Empty is the safe answer, not a degraded one: with no
        /// ancestor entries the merger can never conclude to a deletion, so the
        /// merge becomes purely additive.
        /// </summary>
        public static Dictionary<string, TranslationEntry> LoadMainAncestor()
        {
            var result = new Dictionary<string, TranslationEntry>();

            try
            {
                if (!File.Exists(MainAncestorPath)) return result;

                string json = File.ReadAllText(MainAncestorPath).Replace("\r\n", "\n");
                var parsed = JObject.Parse(json);

                foreach (var prop in parsed.Properties())
                {
                    if (prop.Name.StartsWith("_")) continue;

                    string key = NormalizeLineEndings(prop.Name);
                    if (prop.Value.Type == JTokenType.Object)
                    {
                        var obj = prop.Value as JObject;
                        result[key] = new TranslationEntry
                        {
                            Value = NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                            Tag = obj?["t"]?.ToString() ?? "A"
                        };
                    }
                    else if (prop.Value.Type == JTokenType.String)
                    {
                        result[key] = new TranslationEntry
                        {
                            Value = NormalizeLineEndings(prop.Value.ToString()),
                            Tag = "A"
                        };
                    }
                }

                LogDebug($"Loaded {result.Count} upstream ancestor entries");
            }
            catch (Exception e)
            {
                // Unreadable: an empty ancestor is safe, a wrong one is not
                Adapter.LogWarning($"Failed to load upstream ancestor ({e.Message}) - merging additively");
                return new Dictionary<string, TranslationEntry>();
            }

            return result;
        }

        /// <summary>
        /// The online settings this translation can be put back to, or null when there are none
        /// to go back to.
        ///
        /// Exists because declining a replacement — or simply configuring things locally — leaves
        /// no way back: the player has settings that no longer match the version everyone else
        /// downloads, and nothing in the mod could tell them so, let alone undo it.
        ///
        /// Deliberately stateless: it does not remember that a download was declined, it COMPARES
        /// what we hold with what the online side holds. So it stays right across restarts, and
        /// after the player changes their mind twice.
        ///
        /// Source order matters. A branch answers to its Main, so the Main's settings win when we
        /// have them; otherwise the reference is our own last synced version. Null when neither
        /// exists — a purely local translation has no online settings to restore, and ancestors
        /// written before settings were stored carry none (nothing to offer, and inventing one
        /// would be worse).
        /// </summary>
        public static SettingsReference GetOnlineSettingsReference()
        {
            var ours = TranslationSettings.FromCurrentState();

            var mainSettings = LoadMainAncestorSettings();
            if (mainSettings != null)
            {
                string who = ServerState != null && !string.IsNullOrEmpty(ServerState.Uploader)
                    ? "@" + ServerState.Uploader + "'s version"
                    : "the original translation";
                return SettingsReference.Build(mainSettings, who, ours);
            }

            if (AncestorSettings != null)
            {
                return SettingsReference.Build(AncestorSettings, "your published version", ours);
            }

            return null;
        }

        /// <summary>
        /// The Main's SETTINGS at the last merge from it, or null when unknown.
        /// Same rule as AncestorSettings: null means "no common baseline", which
        /// makes the mod ask rather than decide.
        /// </summary>
        public static TranslationSettings LoadMainAncestorSettings()
        {
            try
            {
                if (!File.Exists(MainAncestorPath)) return null;

                string json = File.ReadAllText(MainAncestorPath).Replace("\r\n", "\n");
                var settings = TranslationSettings.FromFile(JObject.Parse(json));
                return settings.HasAny() ? settings : null;
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to read upstream ancestor settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Remember the Main exactly as it was merged, so the NEXT merge can tell
        /// what upstream changed instead of asking about everything again.
        /// </summary>
        public static void SaveMainAncestor(Dictionary<string, TranslationEntry> mainContent, string mainHash,
            TranslationSettings mainSettings = null)
        {
            try
            {
                var output = new JObject();
                foreach (var kvp in mainContent)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }

                // Only record what we saw of the Main's settings (see
                // SaveAncestorFromRemote): an invented baseline is worse than none
                if (mainSettings != null)
                {
                    mainSettings.WriteInto(output);
                }

                File.WriteAllText(MainAncestorPath, output.ToString(Formatting.Indented));
                LastMergedMainHash = mainHash;
                LogDebug($"Saved upstream ancestor with {output.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save upstream ancestor: {e.Message}");
            }
        }

        /// <summary>
        /// Save the current cache as ancestor (for 3-way merge)
        /// Call this after downloading from website before any local changes
        /// </summary>
        public static void SaveAncestorCache()
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in TranslationCache)
                {
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }

                // Settings travel with the ancestor too: without them there is no
                // way to tell "the other side changed this" from "I changed this",
                // and every difference would have to be asked about
                AncestorSettings = TranslationSettings.FromCurrentState();
                AncestorSettings.WriteInto(output);

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Copy to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in TranslationCache)
                {
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value.Value,
                        Tag = kvp.Value.Tag
                    };
                }

                LocalChangesCount = 0;
                LogDebug($"Saved ancestor cache with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor cache: {e.Message}");
            }
        }

        /// <summary>
        /// Save remote translations as ancestor (for use after merge).
        /// This sets the ancestor to the server version, so LocalChangesCount reflects local additions.
        /// </summary>
        /// <param name="remoteTranslations">Remote translations (legacy string format, will be converted to entries with AI tag)</param>
        public static void SaveAncestorFromRemote(Dictionary<string, string> remoteTranslations)
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value,
                        ["t"] = "A"  // Default to AI for legacy format
                    };
                }

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Convert to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value,
                        Tag = "A"
                    };
                }

                LogDebug($"Saved ancestor from remote with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor from remote: {e.Message}");
            }
        }

        /// <summary>
        /// Save remote translations as ancestor (new format with tags).
        /// </summary>
        public static void SaveAncestorFromRemote(Dictionary<string, TranslationEntry> remoteTranslations,
            TranslationSettings remoteSettings = null)
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }

                // Only record settings we actually saw. Guessing them (say, from
                // our own state) would claim a common baseline that never
                // existed, and the next comparison would trust it.
                AncestorSettings = remoteSettings;
                if (remoteSettings != null)
                {
                    remoteSettings.WriteInto(output);
                }

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Copy to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value.Value,
                        Tag = kvp.Value.Tag
                    };
                }

                LogDebug($"Saved ancestor from remote with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor from remote: {e.Message}");
            }
        }

        /// <summary>
        /// Recalculate LocalChangesCount based on actual differences between TranslationCache and AncestorCache.
        /// Call this after loading caches or after a merge.
        /// </summary>
        /// <summary>
        /// Migrate old placeholder format [vN] to new format [!v*N] in all cache entries.
        /// Returns the number of entries migrated.
        /// </summary>
        private static int MigratePlaceholderFormat(Dictionary<string, TranslationEntry> cache)
        {
            var oldPattern = new Regex(@"\[v(\d+)\]");
            var toMigrate = new List<KeyValuePair<string, TranslationEntry>>();

            foreach (var kv in cache)
            {
                bool keyHasOld = oldPattern.IsMatch(kv.Key);
                bool valHasOld = kv.Value?.Value != null && oldPattern.IsMatch(kv.Value.Value);
                if (keyHasOld || valHasOld)
                    toMigrate.Add(kv);
            }

            foreach (var kv in toMigrate)
            {
                string newKey = oldPattern.Replace(kv.Key, "[!v*$1]");
                string newVal = kv.Value?.Value != null ? oldPattern.Replace(kv.Value.Value, "[!v*$1]") : kv.Value?.Value;

                // Remove old key if it changed
                if (newKey != kv.Key)
                    cache.Remove(kv.Key);

                cache[newKey] = new TranslationEntry
                {
                    Value = newVal,
                    Tag = kv.Value.Tag,
                    Index = kv.Value.Index
                };
            }

            return toMigrate.Count;
        }

        public static void RecalculateLocalChanges()
        {
            if (AncestorCache.Count == 0)
            {
                // No ancestor = all entries are local changes
                LocalChangesCount = TranslationCache.Count;
                return;
            }

            int changes = 0;
            foreach (var kvp in TranslationCache)
            {
                // Skip metadata keys
                if (kvp.Key.StartsWith("_")) continue;

                // New key or different value/tag = local change
                if (!AncestorCache.TryGetValue(kvp.Key, out var ancestorEntry) ||
                    ancestorEntry.Value != kvp.Value.Value ||
                    ancestorEntry.Tag != kvp.Value.Tag)
                {
                    changes++;
                }
            }

            // Entries the ancestor had and we no longer do. Walking only the local cache made
            // deletions invisible: the count stayed at zero, so "in sync" was judged true while the
            // file no longer matched the server. The mod then read the divergence as a SERVER update
            // and offered to download — which would have silently restored what the user deleted.
            int removed = 0;
            foreach (var kvp in AncestorCache)
            {
                if (kvp.Key.StartsWith("_")) continue;
                if (!TranslationCache.ContainsKey(kvp.Key)) removed++;
            }
            changes += removed;

            LocalChangesCount = changes;
            LogDebug($"[LocalChanges] Recalculated: {changes} local changes ({removed} deleted)");
        }

        /// <summary>
        /// Parse JSON content into Dictionary of TranslationEntry.
        /// Handles both new format ({"v": "value", "t": "tag"}) and legacy format (string).
        /// </summary>
        /// <param name="jsonContent">Raw JSON string from file or API</param>
        /// <returns>Dictionary with translation entries including tags</returns>
        public static Dictionary<string, TranslationEntry> ParseTranslationsFromJson(string jsonContent)
        {
            var result = new Dictionary<string, TranslationEntry>();

            try
            {
                // Normalize line endings for consistent key handling
                jsonContent = jsonContent.Replace("\r\n", "\n");
                var parsed = JObject.Parse(jsonContent);

                foreach (var prop in parsed.Properties())
                {
                    // Skip metadata keys
                    if (prop.Name.StartsWith("_")) continue;

                    if (prop.Value.Type == JTokenType.Object)
                    {
                        // New format: {"v": "value", "t": "A", "i": 123}
                        var obj = prop.Value as JObject;
                        result[prop.Name] = new TranslationEntry
                        {
                            Value = obj?["v"]?.ToString() ?? "",
                            Tag = obj?["t"]?.ToString() ?? "A",
                            Index = ParseTranslationIndex(obj?["i"])
                        };
                    }
                    else if (prop.Value.Type == JTokenType.String)
                    {
                        // Legacy format: string value - default to AI tag
                        result[prop.Name] = new TranslationEntry
                        {
                            Value = prop.Value.ToString(),
                            Tag = "A"
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to parse translations from JSON: {e.Message}");
            }

            return result;
        }

        /// <summary>
        /// Compute SHA256 hash of the translation content (same format as upload).
        /// Used to detect if local content differs from server version.
        /// IMPORTANT: Must match PHP Translation::computeHash() exactly.
        /// </summary>
        public static string ComputeContentHash()
        {
            try
            {
                // ⚠ The rule lives in the shared library now, and it was NOT simply moved: running
                // the two side by side, on synthetic cases and on five real game files, showed the
                // copy that used to be here disagreeing with the website on one point. It hashed
                // lines whose key starts with an underscore; the website excludes them — and so
                // does LoadCache below, which drops every unknown underscore key. So such a line
                // could never survive a reload anyway, while the hash computed before that reload
                // said the file differed from the server. The library follows the website, which
                // is what issues file_hash and therefore what decides.
                var lines = new List<KeyValuePair<string, TranslationLine>>(TranslationCache.Count);
                foreach (var kvp in TranslationCache)
                {
                    // The cache always carries a tag; an entry without one is machine output, which
                    // is what it was before tags existed. The VALUE is passed as it stands — null
                    // included — because the website keeps a null there rather than emptying it.
                    lines.Add(new KeyValuePair<string, TranslationLine>(
                        kvp.Key, new TranslationLine(kvp.Value.Value, kvp.Value.Tag ?? "A")));
                }

                return ContentHash.Of(lines, FileUuid);
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Hash] Failed to compute content hash: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Everything this translation IS, fingerprinted without the lineage identifier — the whole
        /// of what somebody made, so that "have I written anything of my own yet" has an answer.
        ///
        /// 🔴 **Not a file_hash, and never to be sent as one.** ComputeContentHash above hashes the
        /// uuid alongside the lines, which is right: it answers "is this the same translation as
        /// the server's". This one answers a different question — "is this still the file I
        /// copied" — across a change of uuid, which is exactly what a fork is.
        ///
        /// ⚠ **The settings sections take part.** Somebody who is not a translator can take a
        /// translation and rework its fonts and its images; that is work, and a file carrying
        /// replacements the original never had is not the same file. The lines alone would call
        /// that person's fork a copy and grey the one button they came for.
        ///
        /// ⚠ **Never compared to the server's content_hash**, which answers the same question its
        /// own way. Each side only ever compares its values to its own — which is what lets this
        /// one serialise floats and unicode however Newtonsoft does, with no cross-language byte
        /// agreement to maintain (a size multiplier of 1.0 alone would break one).
        ///
        /// ⚠ Property names sorted, list order kept. A section is rebuilt from dictionaries whose
        /// order is not promised across insertions, while a font-rule list is applied in sequence —
        /// so order is noise in one and content in the other.
        /// </summary>
        private static string ComputeContentFingerprint()
        {
            try
            {
                var lines = new List<KeyValuePair<string, TranslationLine>>(TranslationCache.Count);
                foreach (var kvp in TranslationCache)
                {
                    lines.Add(new KeyValuePair<string, TranslationLine>(
                        kvp.Key, new TranslationLine(kvp.Value.Value, kvp.Value.Tag ?? "A")));
                }

                var document = new StringBuilder(ContentHash.Of(lines, string.Empty));

                foreach (var section in SettingsSection.All)
                {
                    // Null when the section is empty — which is what SaveCache writes, so an
                    // emptied section and one that never existed fingerprint alike.
                    var token = BuildSettingsSection(section);
                    var container = token as JContainer;
                    if (container == null || !container.HasValues) continue;

                    document.Append('|').Append(section).Append(':').Append(Canonical(token));
                }

                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(document.ToString()));
                    var hex = new StringBuilder(digest.Length * 2);
                    foreach (var b in digest) hex.Append(b.ToString("x2"));
                    return hex.ToString();
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Hash] Failed to fingerprint content: {e.Message}");
                return null;
            }
        }

        /// <summary>One JSON token, written the same way every time. See ComputeContentFingerprint.</summary>
        private static string Canonical(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "null";

            var obj = token as JObject;
            if (obj != null)
            {
                var names = new List<string>();
                foreach (var property in obj.Properties()) names.Add(property.Name);
                names.Sort(StringComparer.Ordinal);

                var sb = new StringBuilder("{");
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonConvert.ToString(names[i])).Append(':').Append(Canonical(obj[names[i]]));
                }
                return sb.Append('}').ToString();
            }

            var array = token as JArray;
            if (array != null)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Canonical(array[i]));
                }
                return sb.Append(']').ToString();
            }

            var value = token as JValue;
            return value == null ? "null" : JsonConvert.ToString(value.Value);
        }

        public static void BuildPatternEntries()
        {
            // Build into a NEW list, then swap atomically.
            // TryPatternMatch iterates PatternEntries on the main thread while
            // this can be called from the worker thread (via AddToCache).
            var newEntries = new List<PatternEntry>();

            // Snapshot to avoid "Collection was modified" if AddToCache runs concurrently
            KeyValuePair<string, TranslationEntry>[] cacheSnapshot;
            try
            {
                var list = new List<KeyValuePair<string, TranslationEntry>>(TranslationCache);
                cacheSnapshot = list.ToArray();
            }
            catch { return; } // Collection changed during snapshot — next call will succeed

            foreach (var kv in cacheSnapshot)
            {
                // Skip if key equals value (no translation)
                if (kv.Key == kv.Value.Value) continue;

                var matchRegex = BuildPatternRegex(kv.Key, out var placeholderIndices, compiled: true);
                if (matchRegex == null) continue;

                newEntries.Add(new PatternEntry
                {
                    OriginalPattern = kv.Key,
                    TranslatedPattern = kv.Value.Value,
                    MatchRegex = matchRegex,
                    PlaceholderIndices = placeholderIndices
                });
            }

            // Atomic swap — main thread sees either the old or the new list, never a half-built one
            PatternEntries = newEntries;

            if (DebugMode)
                Adapter?.LogInfo($"Built {PatternEntries.Count} pattern entries");
        }

        // Matches [!v*N] and captures its index
        private static readonly Regex PlaceholderIndexPattern = new Regex(@"\[!v\*(\d+)\]", RegexOptions.Compiled);

        /// <summary>
        /// Build a regex matching a placeholder pattern against text with concrete numbers.
        /// Each [!v*N] becomes a number-capture group; capture group i+1 corresponds to
        /// placeholderIndices[i]. Works on original keys AND on translated values (which
        /// may reorder the placeholders). Returns null if the pattern has no placeholders.
        /// </summary>
        private static Regex BuildPatternRegex(string patternText, out List<int> placeholderIndices, bool compiled = false)
        {
            placeholderIndices = new List<int>();
            if (string.IsNullOrEmpty(patternText)) return null;

            var matches = PlaceholderIndexPattern.Matches(patternText);
            if (matches.Count == 0) return null;

            try
            {
                string pattern = Regex.Escape(patternText);
                foreach (Match match in matches)
                {
                    placeholderIndices.Add(int.Parse(match.Groups[1].Value));
                    string placeholder = Regex.Escape(match.Value);
                    // Replace one occurrence at a time so capture group order
                    // follows appearance order even with duplicated indices
                    int idx = pattern.IndexOf(placeholder, StringComparison.Ordinal);
                    if (idx < 0) return null;
                    pattern = pattern.Substring(0, idx) + @"(-?\d+(?:[.,]\d+)?%?)"
                        + pattern.Substring(idx + placeholder.Length);
                }
                return new Regex("^" + pattern + "$", compiled ? RegexOptions.Compiled : RegexOptions.None);
            }
            catch { return null; }
        }

        #region In-Game Text Editor support

        /// <summary>
        /// Result of resolving a live displayed text back to its translation cache entry.
        /// </summary>
        public class DisplayedTextResolution
        {
            /// <summary>Cache key: normalized source text, may contain [!v*N] placeholders.
            /// When Entry is null the key does not exist in the cache yet.</summary>
            public string Key;
            /// <summary>Cache entry when the text matched one, otherwise null.</summary>
            public TranslationEntry Entry;
            /// <summary>Live numbers captured from the displayed text, by placeholder index.</summary>
            public Dictionary<int, string> CapturedNumbers = new Dictionary<int, string>();
        }

        /// <summary>
        /// Resolve a live displayed text (original or translated, with concrete numbers)
        /// back to its cache entry. Applies the same normalization as the translation
        /// pipeline so texts with dynamic numbers resolve to their [!v*N] pattern key
        /// instead of a frozen-number key.
        /// </summary>
        public static DisplayedTextResolution ResolveDisplayedText(string displayedText)
        {
            if (string.IsNullOrEmpty(displayedText)) return null;

            // A component may be showing the RTL pipeline's PRESENTED form — shaped codepoints,
            // reordered runs. Nothing below could ever match it (and the fallback key would be
            // the shaped text itself, one Save away from a D8 breach): recover the logical truth
            // first and resolve THAT.
            string presentedLogical = TryGetPresentedLogical(displayedText);
            if (presentedLogical != null) displayedText = presentedLogical;

            var result = new DisplayedTextResolution();

            string normalized = NormalizeLineEndings(displayedText);
            List<string> liveNumbers = null;
            if (Config.normalize_numbers)
                normalized = ExtractNumbersToPlaceholders(normalized, out liveNumbers);

            // ExtractNumbersToPlaceholders numbers placeholders in appearance order
            if (liveNumbers != null)
                for (int i = 0; i < liveNumbers.Count; i++)
                    result.CapturedNumbers[i] = liveNumbers[i];

            // Displayed text is a source text (untranslated, or shown before translation)
            if (TranslationCache.TryGetValue(normalized, out var directEntry))
            {
                result.Key = normalized;
                result.Entry = directEntry;
                return result;
            }
            string trimmed = normalized.TrimEnd();
            if (trimmed != normalized && TranslationCache.TryGetValue(trimmed, out directEntry))
            {
                result.Key = trimmed;
                result.Entry = directEntry;
                return result;
            }

            // Displayed text is a translated value (placeholders in source appearance order).
            // try/catch: the AI worker can mutate TranslationCache during this iteration
            // (same reason BuildPatternEntries snapshots) — fall through to pattern matching.
            try
            {
                foreach (var kvp in TranslationCache)
                {
                    string value = kvp.Value?.Value;
                    if (value == null) continue;
                    if (value == normalized || value.TrimEnd() == trimmed)
                    {
                        result.Key = kvp.Key;
                        result.Entry = kvp.Value;
                        return result;
                    }
                }
            }
            catch { }

            // Translated value whose placeholders were reordered by the translation:
            // match the displayed text against each pattern's translated form
            var patterns = PatternEntries;
            if (patterns != null)
            {
                foreach (var pe in patterns)
                {
                    var reverseRegex = BuildPatternRegex(pe.TranslatedPattern, out var groupPlaceholders);
                    if (reverseRegex == null) continue;
                    var m = reverseRegex.Match(trimmed);
                    if (!m.Success) continue;

                    result.CapturedNumbers.Clear();
                    for (int g = 0; g < groupPlaceholders.Count; g++)
                        result.CapturedNumbers[groupPlaceholders[g]] = m.Groups[g + 1].Value;

                    result.Key = pe.OriginalPattern;
                    TranslationCache.TryGetValue(pe.OriginalPattern, out var patternEntry);
                    result.Entry = patternEntry;
                    return result;
                }
            }

            // Unknown text: the normalized form is the key a future entry must use
            result.Key = normalized;
            return result;
        }

        /// <summary>
        /// Validate that an edited translation keeps every frozen token of its key
        /// ([!v*N], [!t*N], [!STR*N], [!nl]). Returns null when valid, otherwise a
        /// short error message listing the problem tokens.
        /// </summary>
        public static string ValidateEditedPlaceholders(string key, string newValue)
        {
            var keyTokens = Placeholders.Tally(key ?? "");
            var valueTokens = Placeholders.Tally(newValue ?? "");

            var problems = new List<string>();
            foreach (var kv in keyTokens)
            {
                valueTokens.TryGetValue(kv.Key, out int found);
                if (found < kv.Value)
                    problems.Add($"missing {kv.Key}");
            }
            foreach (var kv in valueTokens)
            {
                if (!keyTokens.ContainsKey(kv.Key))
                    problems.Add($"unknown {kv.Key}");
            }

            return problems.Count == 0 ? null : string.Join(", ", problems);
        }

        /// <summary>
        /// List the placeholder tokens an AI answer contains but its source text never had.
        /// Returns null when nothing was invented. A token absent from the source can only be
        /// a hallucination — small models have answered a bare "[!STR*0]", or appended one to
        /// an otherwise correct sentence — and such an entry would both replace the text on
        /// screen and be shared with the community on upload.
        /// </summary>
        private static string FindInventedPlaceholders(string source, string answer)
        {
            // Fast path: no token syntax at all in the answer (the overwhelming majority).
            if (string.IsNullOrEmpty(answer) || answer.IndexOf("[!", StringComparison.Ordinal) < 0)
                return null;

            List<string> invented = null;
            foreach (string token in Placeholders.Tokens(answer))
            {
                if (source != null && source.Contains(token)) continue;
                if (invented == null) invented = new List<string>();
                if (!invented.Contains(token)) invented.Add(token);
            }
            return invented == null ? null : string.Join(", ", invented);
        }

        /// <summary>
        /// Create or update a translation entry from the in-game text editor.
        /// Keeps the reverse cache in sync, rebuilds pattern entries when the key contains
        /// placeholders, and persists the cache.
        /// </summary>
        /// <param name="tag">
        /// Who wrote what is being saved. "H" for anything typed by the person at the keyboard.
        ///
        /// ⚠ "A" when they are accepting an AI proposal verbatim, and that distinction is not
        /// bookkeeping: the tag drives the quality score, the validation gesture (A → V) and what
        /// the community sees on upload. Filing a machine sentence as human work claims a review
        /// nobody performed.
        /// </param>
        public static void SetTranslationFromEditor(string key, string newValue, string tag = "H")
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(newValue)) return;
            if (string.IsNullOrEmpty(tag)) tag = "H";

            // 🔴 D8 at the last door: a key in Arabic presentation forms is the RTL pipeline's
            // DISPLAY output read back — it can never match any source text again and pollutes
            // the shared file irreversibly. It happened once (an editor row resolved to a shaped
            // key before the presented→logical map existed, and a save filed it); the editor now
            // refuses it upstream too, but this is the door every writer goes through.
            if (TextShaping.RtlText.ContainsPresentationForms(key))
            {
                LogWarning($"[Editor] REFUSED to save: the key is in presentation forms (display output, not a source text): '{(key.Length > 40 ? key.Substring(0, 40) + "…" : key)}'");
                return;
            }
            // Same door, VALUE side: shaped text in a value writes presentation forms into the
            // shared file (D8) — it is almost always the RTL clipboard trap (a paste carrying the
            // display order). The person must paste logical text; guessing an unshaping here
            // would be wrong half the time.
            if (TextShaping.RtlText.ContainsPresentationForms(newValue))
            {
                LogWarning("[Editor] REFUSED to save: the value is display-shaped text — paste the logical form (see the RTL clipboard trap).");
                return;
            }

            if (TranslationCache.TryGetValue(key, out var existing))
            {
                existing.Value = newValue;
                existing.Tag = tag;
                // Editing never changes the capture order; only entries that
                // somehow have no index yet get one (defensive — LoadCache
                // backfills everything)
                if (!existing.Index.HasValue)
                {
                    existing.Index = NextOrderIndex();
                }
            }
            else
            {
                TranslationCache[key] = new TranslationEntry { Value = newValue, Tag = tag, Index = NextOrderIndex() };
            }

            // Reverse cache sync so the new value isn't detected as untranslated text
            string normalizedTranslation = NormalizeLineEndings(newValue);
            if (Config.normalize_numbers)
                normalizedTranslation = ExtractNumbersToPlaceholders(normalizedTranslation, out _);
            translatedTexts.TryAdd(normalizedTranslation.TrimEnd(), 0);
            IndexReadbackTranslation(key, newValue);

            if (key.Contains(PlaceholderPrefix))
                BuildPatternEntries();

            RecalculateLocalChanges();
            SaveCache();
        }

        /// <summary>
        /// Whether a key exists in the translation cache. Guard for the
        /// browser-requested retranslation: the key travels from the browser
        /// through the site verbatim, and only texts already in OUR file may
        /// ever be queued to the player's AI backend.
        /// </summary>
        public static bool HasTranslationKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return TranslationCache.ContainsKey(key);
        }

        /// <summary>
        /// The tag currently carried by a key ("A", "H", "V", "S", "M"), or null when the key is
        /// unknown. Read by the editors, which do not offer the same gesture on a line a human
        /// wrote as on one the machine produced.
        /// </summary>
        public static string GetTranslationTag(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return TranslationCache.TryGetValue(key, out var entry) ? (entry.Tag ?? "A") : null;
        }

        /// <summary>
        /// The value currently stored for a key, or null when the key has no entry at all.
        ///
        /// ⚠ Returns the stored form, placeholders and all — not what a player sees. An editor
        /// asking "has this been changed?" must compare against THIS, and never against a
        /// remembered copy: the file also moves under it (a retranslation, a browser save), and a
        /// baseline captured when the row was drawn would answer for a version nobody is looking at.
        /// </summary>
        public static string GetTranslationValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return TranslationCache.TryGetValue(key, out var entry) ? entry.Value : null;
        }

        #endregion

        #region Retranslation (asking again for a line the human did not like)

        /// <summary>What became of a retranslation the human asked for.</summary>
        public enum RetranslateOutcome
        {
            /// <summary>A different translation came back and is now in the file.</summary>
            Replaced,
            /// <summary>The backend kept answering the same thing. Nothing changed.</summary>
            Unchanged,
            /// <summary>Nothing usable came back. The previous translation was put back.</summary>
            Failed
        }

        /// <summary>
        /// Fired when a retranslation ends, WHATEVER the outcome — the editors show a waiting state
        /// and a silence would leave it spinning forever, which is the very complaint this whole
        /// path exists to answer.
        ///
        /// ⚠ Raised from the worker thread: a handler touching Unity objects must hop through
        /// TranslatorUIManager.RunOnMainThread itself.
        /// </summary>
        public static Action<string, string, RetranslateOutcome> OnRetranslateFinished;

        /// <summary>
        /// A line whose translation was taken out to make room for a new answer. Everything needed
        /// to put it back exactly as it was is carried here — the value, its tag, and its capture
        /// order, which the file's editors sort on and which a plain re-add would send to the end.
        /// </summary>
        private sealed class RetranslateRequest
        {
            public string Key;
            public bool HadEntry;
            public string PreviousValue;
            public string PreviousTag;
            public long? PreviousIndex;
            public bool IsOwnUI;

            /// <summary>
            /// Whether the answer replaces the entry, or is merely handed back for a human to
            /// accept. Proposing is the rule everywhere a person is looking at the result: nothing
            /// in this mod applies itself, everything waits for Apply.
            ///
            /// ⚠ The browser is the exception, and not by preference: a proposal has no way to
            /// reach the page. What travels between the mod and a live edit session IS the
            /// translation file — there is no channel for "here is something you might want". So a
            /// request coming from there writes, and the page's own undo covers the rest.
            /// </summary>
            public bool StoreResult;
        }

        // Keyed on the text handed to the queue, which is the cache key itself.
        private static readonly Dictionary<string, RetranslateRequest> retranslateRequests =
            new Dictionary<string, RetranslateRequest>();

        // How many times a retranslation asks again when the backend returns the text that was
        // already there, and how warm each draw is: Config.AttemptsAllowed and
        // Config.TemperatureRetranslate. Same number of requests as a placeholder repair is
        // allowed, on purpose — there is no reason for the two to differ, and one setting is one
        // thing to understand.

        // System.Random, not UnityEngine.Random: this runs on the worker thread, where Unity's
        // static generator is not allowed to be touched.
        private static readonly System.Random retranslateRandom = new System.Random();

        /// <summary>
        /// Ask the backend for another translation of a line somebody did not like — same
        /// instructions, different draw (see the retranslation temperature).
        ///
        /// <paramref name="storeResult"/> false PROPOSES: the file is not touched at all, the
        /// answer comes back through <see cref="OnRetranslateFinished"/> and it is up to whoever
        /// asked to keep it. Nothing can be lost this way, which is why it is the mode used by the
        /// in-game editor.
        ///
        /// true REPLACES, which is what the browser needs: the entry is taken out (AddToCache
        /// refuses to overwrite a key), so this is the one path able to LOSE a translation.
        /// Nothing is thrown away before the queue has accepted the request, and the previous
        /// value goes back if nothing usable comes out of it.
        ///
        /// Returns false when the request could not even be submitted (translation switched off,
        /// backend offline); the file is untouched in that case.
        /// </summary>
        public static bool RemoveTranslationForRetranslate(string key, bool storeResult = true)
        {
            if (string.IsNullOrEmpty(key)) return false;

            // Asked before anything is removed, and not left to the queue: capture-only mode DOES
            // accept a queued text, and would store this line as an empty human entry — the line
            // would come back blank, from a button that promised a better translation.
            if (!Config.IsTranslationEnabled)
            {
                Adapter?.LogWarning("[Retranslate] Refused: translation is switched off");
                return false;
            }

            RetranslateRequest request;
            lock (lockObj)
            {
                // Already asked and not yet answered. Recording a second request would capture the
                // cache as it stands NOW — with the entry already taken out — and the line's real
                // translation, held only by the first request, would become unrecoverable. The
                // queue deduplicates the text anyway, so there is nothing to gain and a value to
                // lose. The editors grey the button for this; this is the part that cannot be
                // clicked around.
                if (retranslateRequests.ContainsKey(key))
                {
                    LogDebug("[Retranslate] Already pending for this line, second request ignored");
                    return true;
                }

                bool hadEntry = TranslationCache.TryGetValue(key, out var previous);
                request = new RetranslateRequest
                {
                    Key = key,
                    HadEntry = hadEntry,
                    PreviousValue = hadEntry ? previous.Value : null,
                    PreviousTag = hadEntry ? (previous.Tag ?? "A") : null,
                    PreviousIndex = hadEntry ? previous.Index : null,
                    // The mod's own interface is translated with its own prompt and its own tag.
                    // The worker normally infers that from the components attached to the request,
                    // and a retranslation attaches none — so it is said here, from the tag the line
                    // already carries, or an M line comes back as game text tagged A.
                    IsOwnUI = hadEntry && previous.Tag == "M",
                    StoreResult = storeResult
                };
                retranslateRequests[key] = request;

                // Proposing changes nothing until a human says so — the entry stays exactly where
                // it is, and the worker is told to skip the cache rather than read it.
                if (storeResult)
                    TranslationCache.Remove(key);
            }

            if (storeResult && key.Contains(PlaceholderPrefix))
                BuildPatternEntries();

            if (QueueForTranslation(key, isOwnUI: request.IsOwnUI))
                return true;

            // Turned away at the door: put the line back exactly as it was, say so, and let the
            // caller tell the human rather than leave them in front of a spinner.
            Adapter?.LogWarning("[Retranslate] Request refused by the queue (translation off, offline, or text too long)");
            RestorePreviousEntry(request);
            FinishRetranslation(request, request.PreviousValue, RetranslateOutcome.Failed);
            return false;
        }

        /// <summary>Take the pending request for a dequeued text, if that text is one.</summary>
        private static RetranslateRequest TakeRetranslateRequest(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            lock (lockObj)
            {
                if (!retranslateRequests.TryGetValue(text, out var request)) return null;
                retranslateRequests.Remove(text);
                return request;
            }
        }

        /// <summary>
        /// Ask again, up to Config.AttemptsAllowed times, until the answer differs from the one the
        /// human rejected. Each round draws a new seed — where the provider honours it, the run is
        /// reproducible; where it ignores it, the temperature alone does the work (see
        /// Negotiation.SendSeed).
        /// </summary>
        private static void RunRetranslation(RetranslateRequest request, string normalizedKey,
            List<string> extractedNumbers, List<KeyValuePair<int, string>> extractedVars,
            string originalText, List<object> componentsToUpdate)
        {
            // The worker re-normalizes what it dequeues, and a cache key is already normalized — so
            // these two are the same string, except when a variable became known in between and the
            // key now reads as [!STR*N]. That is a different question: answering it would file the
            // reply under a key nobody asked about and leave this line with none at all.
            if (!string.Equals(normalizedKey, request.Key, StringComparison.Ordinal))
            {
                Adapter?.LogWarning("[Retranslate] The line changed shape since it was captured, keeping the previous translation");
                RestorePreviousEntry(request);
                FinishRetranslation(request, request.PreviousValue, RetranslateOutcome.Failed);
                return;
            }

            // An explicit request overrides the session's give-up list: that list exists so a line
            // that failed validation is not hammered on every scan, and this is a human asking once.
            validationFailedTexts.TryRemove(normalizedKey, out _);

            string backend = Config.translation_backend;
            bool deterministicBackend = backend == "google" || backend == "deepl";
            string accepted = null;
            bool sameAnswerAgain = false;

            int rounds = deterministicBackend ? 1 : Config.AttemptsAllowed;
            for (int round = 0; round < rounds; round++)
            {
                string candidate;
                if (deterministicBackend)
                {
                    candidate = TranslateWithAPI(normalizedKey, extractedNumbers);
                }
                else
                {
                    // A configured seed is offset by the round, never used as-is: a single fixed
                    // seed would redraw the very answer being rejected, every round, forever.
                    // Left unset, each round draws its own — variation without reproducibility.
                    int seed;
                    if (Config.ai_seed_retranslate.HasValue)
                        seed = unchecked(Config.ai_seed_retranslate.Value + round);
                    else
                        lock (retranslateRandom) { seed = retranslateRandom.Next(1, int.MaxValue); }

                    candidate = TranslateWithAI(normalizedKey, extractedNumbers, request.IsOwnUI,
                        new Variation { Temperature = Config.TemperatureRetranslate, Seed = seed });
                }

                if (string.IsNullOrEmpty(candidate))
                    continue;

                // A refusal marker must never be written over an existing translation: stored as
                // tag S it would replace the line with its own source text, which is a loss
                // dressed up as a decision.
                if (Answers.Read(candidate) != AnswerKind.Translation)
                {
                    Adapter?.LogWarning("[Retranslate] Backend refused the line, keeping the previous translation");
                    continue;
                }

                if (FindInventedPlaceholders(normalizedKey, candidate) != null)
                    continue;

                if (request.HadEntry && string.Equals(candidate, request.PreviousValue, StringComparison.Ordinal))
                {
                    sameAnswerAgain = true;
                    LogDebug($"[Retranslate] Round {round + 1}/{rounds} returned the same text, asking again");
                    continue;
                }

                accepted = candidate;
                break;
            }

            if (accepted == null)
            {
                RestorePreviousEntry(request);
                FinishRetranslation(request, request.PreviousValue,
                    sameAnswerAgain ? RetranslateOutcome.Unchanged : RetranslateOutcome.Failed);
                return;
            }

            // A proposal stops here: the answer goes to whoever asked and the file stays as it is,
            // game screen included. Applying it would be deciding for the human — the very thing
            // the button was reported for.
            if (!request.StoreResult)
            {
                aiTranslationCount++;
                FinishRetranslation(request, accepted, RetranslateOutcome.Replaced);
                return;
            }

            AddToCache(normalizedKey, accepted, request.IsOwnUI ? "M" : "A");

            // Keep the line where it was in the file: the editors sort on the capture order, and a
            // key re-added after a removal would otherwise jump to the end of a list the human is
            // reading top to bottom.
            lock (lockObj)
            {
                if (request.PreviousIndex.HasValue
                    && TranslationCache.TryGetValue(normalizedKey, out var stored))
                    stored.Index = request.PreviousIndex;
            }

            aiTranslationCount++;

            string forComponents = extractedNumbers != null
                ? RestoreNumbersFromPlaceholders(accepted, extractedNumbers)
                : accepted;
            forComponents = VariableManager.RestoreVariables(forComponents, extractedVars);
            OnTranslationComplete?.Invoke(originalText, forComponents, componentsToUpdate);
            PendingVisualRefresh = true;

            FinishRetranslation(request, accepted, RetranslateOutcome.Replaced);
        }

        /// <summary>
        /// Put back every line a retranslation took out and never answered for — the game is
        /// closing, or the worker stopped, and the file is about to be written whole.
        /// </summary>
        private static void RestoreOutstandingRetranslations()
        {
            List<RetranslateRequest> outstanding;
            lock (lockObj)
            {
                if (retranslateRequests.Count == 0) return;
                outstanding = new List<RetranslateRequest>(retranslateRequests.Values);
                retranslateRequests.Clear();
            }

            foreach (var request in outstanding)
            {
                RestorePreviousEntry(request);
                Adapter?.LogWarning("[Retranslate] Unanswered when stopping — previous translation kept");
                // Told, not just repaired: an editor is showing a waiting row for each of these.
                FinishRetranslation(request, request.PreviousValue, RetranslateOutcome.Failed);
            }
        }

        /// <summary>Put back the entry a retranslation took out, tag and capture order included.</summary>
        private static void RestorePreviousEntry(RetranslateRequest request)
        {
            if (request == null || !request.HadEntry) return;
            // A proposal never took it out; putting it "back" would overwrite whatever the human
            // has done to that line in the meantime.
            if (!request.StoreResult) return;

            lock (lockObj)
            {
                TranslationCache[request.Key] = new TranslationEntry
                {
                    Value = request.PreviousValue,
                    Tag = request.PreviousTag,
                    Index = request.PreviousIndex ?? NextOrderIndex()
                };
            }

            if (request.Key.Contains(PlaceholderPrefix))
                BuildPatternEntries();
        }

        private static void FinishRetranslation(RetranslateRequest request, string value, RetranslateOutcome outcome)
        {
            if (request == null) return;
            lock (lockObj) { retranslateRequests.Remove(request.Key); }

            // Only a request that actually wrote has anything to save. A proposal deliberately
            // leaves the file alone, so there is nothing to push to a browser either.
            if (outcome == RetranslateOutcome.Replaced && request.StoreResult)
                SaveCache();

            try
            {
                OnRetranslateFinished?.Invoke(request.Key, value, outcome);
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Retranslate] Notification handler error: {e.Message}");
            }
        }

        #endregion

        #region Placeholders

        /// <summary>
        /// Replace [!v*N] placeholders with live numbers captured by ResolveDisplayedText.
        /// </summary>
        public static string RestoreNumbersFromPlaceholders(string text, IDictionary<int, string> numbersByIndex)
        {
            if (string.IsNullOrEmpty(text) || numbersByIndex == null || numbersByIndex.Count == 0)
                return text;

            string result = text;
            foreach (var kv in numbersByIndex)
                result = result.Replace($"{PlaceholderPrefix}{kv.Key}{PlaceholderSuffix}", kv.Value);
            return result;
        }

        #endregion

        #region Stale Translation Snapshot (post-reload safety net)

        private class StalePatternEntry
        {
            public Regex Regex;                  // matches the OLD translated form with concrete numbers
            public List<int> PlaceholderIndices; // capture group i+1 -> placeholder index
            public string Key;
        }

        // Snapshot of the cache being replaced by ReloadCache. Texts still displayed
        // with an old translation (components RestoreAllOriginals could not reach)
        // are recognized through it and refreshed from the new cache instead of
        // being queued for AI translation.
        private static Dictionary<string, string> _staleValueToKey;
        private static List<StalePatternEntry> _stalePatterns;

        /// <summary>
        /// Snapshot the current cache values before a reload replaces them.
        /// Called by ReloadCache while the outgoing cache is still loaded.
        /// </summary>
        private static void BuildStaleTranslationSnapshot()
        {
            var valueToKey = new Dictionary<string, string>();
            var stalePatterns = new List<StalePatternEntry>();

            // Snapshot first: the AI worker can mutate TranslationCache during
            // this iteration (same reason BuildPatternEntries snapshots)
            KeyValuePair<string, TranslationEntry>[] cacheSnapshot;
            try
            {
                cacheSnapshot = new List<KeyValuePair<string, TranslationEntry>>(TranslationCache).ToArray();
            }
            catch { return; }

            foreach (var kv in cacheSnapshot)
            {
                string value = kv.Value?.Value;
                if (string.IsNullOrEmpty(value) || kv.Key == value) continue;

                string normalizedValue = NormalizeLineEndings(value);
                if (Config.normalize_numbers)
                    normalizedValue = ExtractNumbersToPlaceholders(normalizedValue, out _);
                normalizedValue = normalizedValue.TrimEnd();
                if (!valueToKey.ContainsKey(normalizedValue))
                    valueToKey[normalizedValue] = kv.Key;

                // Values whose placeholders were reordered by the translation can't be
                // matched by the normalized-value lookup — keep a regex for them
                if (value.Contains(PlaceholderPrefix))
                {
                    var regex = BuildPatternRegex(value, out var indices);
                    if (regex == null) continue;
                    bool inAppearanceOrder = true;
                    for (int i = 0; i < indices.Count; i++)
                        if (indices[i] != i) { inAppearanceOrder = false; break; }
                    if (!inAppearanceOrder)
                        stalePatterns.Add(new StalePatternEntry
                        {
                            Regex = regex,
                            PlaceholderIndices = indices,
                            Key = kv.Key
                        });
                }
            }

            _staleValueToKey = valueToKey;
            _stalePatterns = stalePatterns;
            LogDebug($"[StaleSnapshot] {valueToKey.Count} values, {stalePatterns.Count} reordered patterns");
        }

        /// <summary>
        /// Recognize a displayed text that is a translation from the pre-reload cache.
        /// Returns the refreshed translation from the new cache, the input text itself
        /// when the entry no longer exists (marked as translated so it is never queued),
        /// or null when the text is not a stale translation.
        /// </summary>
        private static string TryResolveStaleTranslation(string text, string trimmedNormalized, object component)
        {
            var valueToKey = _staleValueToKey;
            if (valueToKey == null) return null;

            string key = null;
            Dictionary<int, string> capturedNumbers = null;

            if (valueToKey.TryGetValue(trimmedNormalized, out key))
            {
                // Normalized-value match: placeholders are in appearance order,
                // so live numbers map to placeholder indices by position
                if (Config.normalize_numbers)
                {
                    ExtractNumbersToPlaceholders(NormalizeLineEndings(text), out var numbers);
                    if (numbers != null && numbers.Count > 0)
                    {
                        capturedNumbers = new Dictionary<int, string>();
                        for (int i = 0; i < numbers.Count; i++)
                            capturedNumbers[i] = numbers[i];
                    }
                }
            }
            else
            {
                var stalePatterns = _stalePatterns;
                if (stalePatterns == null || stalePatterns.Count == 0) return null;

                string lineNormalized = NormalizeLineEndings(text).TrimEnd();
                foreach (var sp in stalePatterns)
                {
                    var m = sp.Regex.Match(lineNormalized);
                    if (!m.Success) continue;
                    key = sp.Key;
                    capturedNumbers = new Dictionary<int, string>();
                    for (int g = 0; g < sp.PlaceholderIndices.Count; g++)
                        capturedNumbers[sp.PlaceholderIndices[g]] = m.Groups[g + 1].Value;
                    break;
                }
                if (key == null) return null;
            }

            // The displayed text is a stale translation of `key`
            if (TranslationCache.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.Value)
                && entry.Value != key && !entry.IsHumanEmpty && entry.Tag != "S")
            {
                string newText = RestoreNumbersFromPlaceholders(entry.Value, capturedNumbers);
                if (component != null)
                {
                    string originalText = RestoreNumbersFromPlaceholders(key, capturedNumbers);
                    TranslatorScanner.StoreOriginalText(component, originalText);
                    TranslatorPatches.TrackTranslation(TypeHelper.GetInstanceID(component), originalText, newText);
                }
                translatedCount++;
                LogDebug($"[StaleRefresh] Refreshed stale translation for key: {key.Substring(0, Math.Min(40, key.Length))}");
                return newText;
            }

            // Entry removed from the new cache: keep the displayed text and mark it
            // as translated so it is never queued (returning the input unchanged also
            // keeps the caller from tracking it as a new translation)
            translatedTexts.TryAdd(trimmedNormalized, 0);
            LogDebug($"[StaleRefresh] Entry gone from new cache, keeping displayed text for key: {key.Substring(0, Math.Min(40, key.Length))}");
            return text;
        }

        #endregion

        private static bool workerRunning = false;
        /// <summary>
        /// Set to true by the worker thread when new translations are cached.
        /// Consumed by the scanner to trigger a visual refresh on the main thread.
        /// </summary>
        public static volatile bool PendingVisualRefresh = false;
        /// <summary>
        /// Set to true by API translation methods when a rate limit (429) is received.
        /// The worker checks this to re-queue the text and backoff.
        /// </summary>
        private static volatile bool _apiRateLimited = false;

        private static void StartTranslationWorker()
        {
            if (!Config.IsTranslationEnabled && !Config.capture_keys_only)
            {
                Adapter?.LogWarning("[Worker] Cannot start: no translation backend enabled (and capture mode off)");
                return;
            }
            if (workerRunning) return; // Already running

            workerRunning = true;
            LogDebug("[Worker] Starting translation worker thread");
            Thread workerThread = new Thread(TranslationWorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Start();
        }

        /// <summary>
        /// Start the translation worker if AI is enabled and worker isn't running.
        /// Call this after enabling AI in settings.
        /// </summary>
        public static void EnsureWorkerRunning()
        {
            if ((Config.IsTranslationEnabled || Config.capture_keys_only) && !workerRunning)
            {
                LogDebug("[TranslatorCore] Starting translation worker thread...");
                StartTranslationWorker();
            }
        }

        /// <summary>
        /// Clear the translation queue. Called when AI is disabled.
        /// </summary>
        public static void ClearQueue()
        {
            lock (lockObj)
            {
                int count = translationQueue.Count;
                translationQueue.Clear();
                pendingTranslations.Clear();
                pendingComponents.Clear();
                isTranslating = false;
                currentlyTranslating = null;
                if (count > 0)
                {
                    LogDebug($"[TranslatorCore] Cleared {count} items from translation queue");
                }
            }
        }

        private static void PreloadModel()
        {
            try
            {
                Adapter.LogInfo($"Preloading model {Config.ai_model}...");
                var requestBody = new
                {
                    model = Config.ai_model,
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 1,
                    stream = false
                };
                string jsonRequest = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, Endpoints.Resolve(Config.ai_url, "chat/completions"));
                request.Content = content;
                AddAIAuthHeader(request);

                var response = httpClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {
                    Adapter.LogInfo("Model preloaded successfully");
                }
                else
                {
                    Adapter.LogWarning($"Failed to preload model: {response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Error preloading model: {e.Message}");
            }
        }

        // ===========================================================================
        // HttpClient construction & proxy configuration
        // ===========================================================================
        //
        // Some games hook the network stack at the process level (DRM, anti-cheat,
        // EOS bootstrap, etc.) — they may install a runtime HTTP proxy that swallows
        // every outbound request the mod tries to make, so SendAsync hangs until the
        // 5-minute timeout. To stay usable in those games we let the user pick a
        // proxy strategy at runtime: default / system / none / custom.
        // The HttpClient is created here and can be rebuilt on the fly when the
        // user changes the setting in OptionsPanel — the old client is dropped to
        // the GC so requests already in flight finish naturally.

        private static HttpClient CreateHttpClient(ModConfig config)
        {
            // Defense in depth: never let a misconfigured proxy stop the mod from booting.
            // If BuildProxyHandler or the HttpClient constructor throws, fall back to a
            // plain HttpClient so the rest of TranslatorCore.Initialize keeps running.
            try
            {
                var handler = BuildProxyHandler(config);
                var client = handler != null ? new HttpClient(handler) : new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(5);
                LogProxyMode(config);
                return client;
            }
            catch (Exception e)
            {
                Adapter?.LogError($"[HttpClient] Failed to create configured HttpClient, falling back to default: {e.GetType().Name}: {e.Message}");
                var fallback = new HttpClient();
                fallback.Timeout = TimeSpan.FromMinutes(5);
                return fallback;
            }
        }

        /// <summary>
        /// Build the HttpClientHandler matching the user's proxy_mode.
        /// Returns null for "default" so that HttpClient uses its plain default
        /// constructor (legacy behavior — inherits WebRequest.DefaultProxy, which
        /// the game may have replaced; that is exactly the situation the other
        /// modes are designed to escape).
        /// </summary>
        private static HttpClientHandler BuildProxyHandler(ModConfig config)
        {
            string mode = (config?.proxy_mode ?? "default").Trim().ToLowerInvariant();
            if (mode == "default")
                return null;

            HttpClientHandler handler;
            try { handler = new HttpClientHandler(); }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[HttpClient] Failed to create HttpClientHandler, falling back to default: {e.Message}");
                return null;
            }

            // IMPORTANT (Mono): MonoWebRequestHandler.set_Proxy(null) throws
            // InvalidOperationException. Never assign null to handler.Proxy; rely on
            // UseProxy=false instead when we want to bypass all proxies.
            switch (mode)
            {
                case "none":
                    try
                    {
                        handler.UseProxy = false;
                        // Don't touch handler.Proxy here.
                    }
                    catch (Exception e)
                    {
                        Adapter?.LogWarning($"[HttpClient] Failed to disable proxy: {e.Message}");
                        return null;
                    }
                    return handler;

                case "system":
                    try
                    {
                        // GetSystemWebProxy() reads from the registry every call, so a runtime
                        // override on WebRequest.DefaultProxy can't poison this instance.
                        var sys = System.Net.WebRequest.GetSystemWebProxy();
                        if (sys == null)
                        {
                            Adapter?.LogWarning("[HttpClient] system proxy is null -> falling back to default");
                            return null;
                        }
                        handler.UseProxy = true;
                        handler.Proxy = sys;
                    }
                    catch (Exception e)
                    {
                        Adapter?.LogWarning($"[HttpClient] system proxy load failed: {e.Message}");
                        return null;
                    }
                    return handler;

                case "custom":
                    if (string.IsNullOrWhiteSpace(config.proxy_url))
                    {
                        Adapter?.LogWarning("[HttpClient] proxy_mode=custom but proxy_url is empty -> falling back to default");
                        return null;
                    }
                    try
                    {
                        var webProxy = new System.Net.WebProxy(config.proxy_url.Trim(), config.proxy_bypass_local);
                        if (!string.IsNullOrEmpty(config.proxy_username))
                        {
                            webProxy.Credentials = new System.Net.NetworkCredential(
                                config.proxy_username,
                                config.proxy_password ?? string.Empty);
                        }
                        handler.UseProxy = true;
                        handler.Proxy = webProxy;
                    }
                    catch (Exception e)
                    {
                        Adapter?.LogWarning($"[HttpClient] custom proxy '{config.proxy_url}' is invalid: {e.Message}");
                        return null;
                    }
                    return handler;

                default:
                    Adapter?.LogWarning($"[HttpClient] Unknown proxy_mode '{mode}' -> falling back to default");
                    return null;
            }
        }

        private static void LogProxyMode(ModConfig config)
        {
            string mode = (config?.proxy_mode ?? "default").Trim().ToLowerInvariant();
            string detail = (mode == "custom" && !string.IsNullOrEmpty(config?.proxy_url))
                ? $" -> {config.proxy_url}"
                : string.Empty;
            Adapter?.LogInfo($"[HttpClient] Proxy mode: {mode}{detail}");
        }

        /// <summary>
        /// Replace the shared HttpClient with a fresh one built from the current
        /// config. Call this after the user applies a proxy setting change.
        /// The previous instance is left for the GC: explicitly disposing it would
        /// cancel any request still in flight (e.g. an SSE stream).
        /// </summary>
        public static void RebuildHttpClient()
        {
            Adapter?.LogInfo("[HttpClient] Rebuilding HttpClient with current proxy settings...");
            httpClient = CreateHttpClient(Config);
        }

        /// <summary>
        /// Add Authorization header for AI API requests if an API key is configured.
        /// </summary>
        private static void AddAIAuthHeader(HttpRequestMessage request, string apiKey = null)
        {
            string key = apiKey ?? Config?.ai_api_key;
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
        }

        /// <summary>
        /// Ask the server to let go of a model we have stopped using.
        ///
        /// Changing model used to leave the previous one loaded. Ollama keeps a model in memory
        /// for five minutes after its last request and only evicts one when the next needs the
        /// room — so for those five minutes two models share the graphics card, with a game
        /// already on it. If they do not both fit, the new one is split with the processor and
        /// every line takes seconds instead of tenths of a second. That is worst exactly when it
        /// is most likely: right after someone switched model because the first felt slow.
        ///
        /// ⚠ ONLY for a server on this machine or this network, and only because Ollama is the
        /// only one where it means anything. vLLM and llama.cpp serve a single model per process,
        /// so there is nothing to free; LM Studio manages its own lifetime and unloads through its
        /// command line rather than its API. A cloud provider has no local memory to reclaim at
        /// all — firing an unknown route at one would be traffic sent to a third party for
        /// nothing, which is reason enough not to.
        ///
        /// ⚠ /api/generate is Ollama's own route, not part of the OpenAI-compatible surface the
        /// rest of the mod speaks. It is used as a favour, never as a dependency: another local
        /// server answers 404 and we are exactly where we were. Nothing waits for the result and
        /// nothing reports it — a model left loaded is a slowdown, not a failure.
        /// </summary>
        public static void ReleaseModel(string baseUrl, string model)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model)) return;
            if (!Endpoints.IsOnYourOwnNetwork(baseUrl)) return;

            // The native API sits at the root, not under the OpenAI-compatible surface — see
            // Endpoints.RootOf, which knows the shapes people paste.
            string url = Endpoints.RootOf(baseUrl) + "/api/generate";
            string modelName = model;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var payload = new JObject
                    {
                        ["model"] = modelName,
                        ["keep_alive"] = 0
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
                    };
                    AddAIAuthHeader(request);

                    httpClient.SendAsync(request).Wait(3000);
                    LogDebug($"[AI] Asked {url} to release {modelName}");
                }
                catch
                {
                    // A server that does not know this route manages its own memory. Silence is
                    // the right answer: nothing the player did has failed.
                }
            });
        }

        /// <summary>
        /// Test connection to AI server via OpenAI-compatible /v1/models endpoint.
        /// </summary>
        /// <param name="url">The server URL to test</param>
        /// <param name="apiKey">Optional API key for authenticated servers</param>
        /// <returns>True if connection successful</returns>
        public static async System.Threading.Tasks.Task<bool> TestAIConnection(string url, string apiKey = null)
        {
            string endpoint = Endpoints.Resolve(url, "models");
            LogDebug($"[AI] Testing connection: GET {endpoint} (proxy_mode={Config?.proxy_mode ?? "default"})");
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                var response = await httpClient.SendAsync(request);
                LogDebug($"[AI] Test response: {(int)response.StatusCode} {response.ReasonPhrase}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[AI] Connection test failed ({endpoint}): {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test Google Translate API connection by translating a sample word.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> TestGoogleConnection(string apiKey)
        {
            try
            {
                var requestObj = new JObject
                {
                    ["q"] = "Hello",
                    ["target"] = "fr",
                    ["format"] = "text"
                };

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2");
                request.Content = httpContent;
                request.Headers.Add("X-Goog-Api-Key", apiKey);

                var response = await httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Google] Connection test failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test DeepL API connection by translating a sample word.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> TestDeepLConnection(string apiKey, bool useFree)
        {
            try
            {
                var requestObj = new JObject
                {
                    ["text"] = new JArray { "Hello" },
                    ["target_lang"] = "FR"
                };

                string endpoint = useFree
                    ? "https://api-free.deepl.com/v2/translate"
                    : "https://api.deepl.com/v2/translate";

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");

                var response = await httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[DeepL] Connection test failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fetch available models from AI server via OpenAI-compatible /v1/models endpoint.
        /// </summary>
        /// <param name="url">The server URL</param>
        /// <param name="apiKey">Optional API key for authenticated servers</param>
        /// <returns>Sorted array of model names, or empty array on failure</returns>
        public static async System.Threading.Tasks.Task<string[]> FetchModels(string url, string apiKey = null)
        {
            string endpoint = Endpoints.Resolve(url, "models");
            LogDebug($"[AI] Fetching models: GET {endpoint} (proxy_mode={Config?.proxy_mode ?? "default"})");
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                var response = await httpClient.SendAsync(request);
                LogDebug($"[AI] FetchModels response: {(int)response.StatusCode} {response.ReasonPhrase}");
                if (!response.IsSuccessStatusCode)
                    return new string[0];

                string json = await response.Content.ReadAsStringAsync();
                var obj = ApiClient.ParseJsonSafe(json);
                var data = obj["data"] as JArray;
                if (data == null)
                    return new string[0];

                var models = new List<string>();
                foreach (var item in data)
                {
                    string id = item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        models.Add(id);
                }
                models.Sort(StringComparer.OrdinalIgnoreCase);
                LogDebug($"[AI] FetchModels parsed {models.Count} model(s)");
                return models.ToArray();
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[AI] Failed to fetch models ({endpoint}): {e.GetType().Name}: {e.Message}");
                return new string[0];
            }
        }

        private static void TranslationWorkerLoop()
        {
            // On IL2CPP, register this thread with the GC to prevent
            // "fatal error in GC: Collecting from unknown thread" crashes.
            // The Boehm GC used by IL2CPP doesn't know about .NET threads.
            if (Adapter?.IsIL2CPP == true)
            {
                try
                {
                    // Find IL2CPP class (Il2CppInterop.Runtime.IL2CPP)
                    Type il2cppType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        il2cppType = asm.GetType("Il2CppInterop.Runtime.IL2CPP");
                        if (il2cppType != null) break;
                    }

                    if (il2cppType != null)
                    {
                        // il2cpp_domain_get() returns the current domain pointer
                        var domainGet = il2cppType.GetMethod("il2cpp_domain_get",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        // il2cpp_thread_attach(domain) attaches current thread to GC
                        var threadAttach = il2cppType.GetMethod("il2cpp_thread_attach",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                        if (domainGet != null && threadAttach != null)
                        {
                            var domain = domainGet.Invoke(null, null);
                            threadAttach.Invoke(null, new object[] { domain });
                            LogDebug("[Worker] Thread attached to IL2CPP GC domain");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Adapter?.LogWarning($"[Worker] Failed to attach thread to IL2CPP GC: {ex.Message}");
                }
            }

            LogDebug("[Worker] Thread started, waiting for translations...");

            while (!ShuttingDown)
            {
                // Stop if AI was disabled (capture-only mode keeps the worker alive)
                if (!Config.IsTranslationEnabled && !Config.capture_keys_only)
                {
                    LogDebug("[Worker] Translation disabled, stopping worker thread");
                    // Nobody is left to answer a retranslation still in the queue: give those lines
                    // their previous translation back rather than let the next save drop them.
                    RestoreOutstandingRetranslations();
                    workerRunning = false;
                    return;
                }

                string textToTranslate = null;
                List<object> componentsToUpdate = null;
                bool queuedAsOwnUI = false;

                lock (lockObj)
                {
                    if (translationQueue.Count > 0)
                    {
                        textToTranslate = translationQueue.Dequeue();

                        // TAKE components (remove from dict) so new queues create fresh entries
                        if (pendingComponents.TryGetValue(textToTranslate, out var comps))
                        {
                            componentsToUpdate = comps; // Take the list directly
                            pendingComponents.Remove(textToTranslate); // Remove NOW
                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] Found {comps.Count} components for text");
                        }
                        else
                        {
                            if (Config.debug_ai)
                                Adapter?.LogWarning($"[Worker] NO components found for text!");
                        }

                        // Remove from pending so same text can be re-queued with new components
                        pendingTranslations.Remove(textToTranslate);

                        // Take the own-UI intent recorded at queue time: a code-owned label is
                        // often queued WITHOUT a component (a fragment concatenated with data, a
                        // help text applied by its own zone), and the components are the only
                        // other way to recognise our own UI.
                        queuedAsOwnUI = pendingOwnUITexts.Remove(textToTranslate);

                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[Worker] Dequeued: {textToTranslate?.Substring(0, Math.Min(30, textToTranslate?.Length ?? 0))}...");
                    }
                }

                if (textToTranslate != null)
                {
                    string originalText = textToTranslate;
                    if (Config.debug_ai)
                    {
                        string workerPreview = textToTranslate.Length > 40 ? textToTranslate.Substring(0, 40) + "..." : textToTranslate;
                        Adapter?.LogInfo($"[Worker] Processing: {workerPreview} (queue remaining: {translationQueue.Count})");
                    }
                    isTranslating = true;
                    currentlyTranslating = textToTranslate.Length > 50 ? textToTranslate.Substring(0, 50) + "..." : textToTranslate;

                    // Check if this text is from our own UI by examining the pending components
                    // Use IsOwnUI (not IsOwnUITranslatable) for tagging - it doesn't depend on translate_mod_ui config
                    // This is more accurate than string-based tracking which caused false positives
                    bool isOwnUI = queuedAsOwnUI;
                    if (!isOwnUI && componentsToUpdate != null && componentsToUpdate.Count > 0)
                    {
                        foreach (var comp in componentsToUpdate)
                        {
                            if (comp is Component component && IsOwnUI(component))
                            {
                                isOwnUI = true;
                                break;
                            }
                        }
                    }
                    currentTextIsOwnUI = isOwnUI;

                    // Declared out here so the catch below can put back what a retranslation took
                    // out: this is the one item in the queue that arrives with something to lose.
                    RetranslateRequest retranslate = null;

                    try
                    {
                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[Worker] Calling AI...{(isOwnUI ? " (UI prompt)" : "")}");

                        // Extract variables then numbers BEFORE sending to AI.
                        // Never on our own GUI: variables hold GAME state (player name, seed…) and
                        // substitution is a plain Replace of their current value. A value that happens
                        // to equal one of our labels would swallow it whole ("Current Translation" →
                        // "[!STR*0]") and poison that cache entry for good.
                        string normalizedOriginal = textToTranslate;
                        List<KeyValuePair<int, string>> workerExtractedVars = null;
                        if (VariableManager.HasVariables && !isOwnUI)
                        {
                            normalizedOriginal = VariableManager.ExtractVariables(normalizedOriginal, out workerExtractedVars);
                        }
                        List<string> extractedNumbers = null;
                        if (Config.normalize_numbers)
                        {
                            normalizedOriginal = ExtractNumbersToPlaceholders(normalizedOriginal, out extractedNumbers);
                        }

                        // A human asked for this line again, having read the answer we already had.
                        // Everything below that shortcuts to a stored or previously refused answer
                        // must be skipped for it — those are exactly the answers being rejected.
                        retranslate = TakeRetranslateRequest(textToTranslate);

                        // Check cache first (another request might have already translated this)
                        string translation = null;
                        if (retranslate == null && TranslationCache.TryGetValue(normalizedOriginal, out var cachedEntry))
                        {
                            if (cachedEntry.Value != normalizedOriginal && !cachedEntry.IsHumanEmpty && cachedEntry.Tag != "S")
                            {
                                translation = cachedEntry.Value;
                                if (Config.debug_ai)
                                    Adapter?.LogInfo($"[Worker] Cache hit for normalized text, skipping AI");

                                // Notify components even for cache hits — the text was re-queued
                                // with different components that didn't get the first Apply.
                                string cachedTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                                    ? RestoreNumbersFromPlaceholders(translation, extractedNumbers)
                                    : translation;
                                cachedTranslation = VariableManager.RestoreVariables(cachedTranslation, workerExtractedVars);
                                OnTranslationComplete?.Invoke(originalText, cachedTranslation, componentsToUpdate);
                            }
                        }

                        // Retranslation: its own loop, because it needs a DIFFERENT answer and has
                        // a previous value to put back if it cannot get one.
                        if (retranslate != null)
                        {
                            RunRetranslation(retranslate, normalizedOriginal, extractedNumbers,
                                workerExtractedVars, originalText, componentsToUpdate);
                        }
                        // Capture keys only mode: store H+empty without calling AI
                        else if (Config.capture_keys_only)
                        {
                            AddToCache(normalizedOriginal, "", "H");
                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] Captured key (no translation): {normalizedOriginal.Substring(0, Math.Min(30, normalizedOriginal.Length))}...");
                        }
                        // Text already failed placeholder validation this session:
                        // don't hammer the backend, it will be retried next launch
                        else if (translation == null && validationFailedTexts.ContainsKey(normalizedOriginal))
                        {
                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] Skipping (failed placeholder validation earlier): {normalizedOriginal.Substring(0, Math.Min(40, normalizedOriginal.Length))}...");
                        }
                        // Only call translation backend if not in cache
                        else if (translation == null)
                        {
                            // Dispatch to the appropriate backend
                            string backend = Config.translation_backend;
                            if (backend == "google" || backend == "deepl")
                            {
                                translation = TranslateWithAPI(normalizedOriginal, extractedNumbers);
                            }
                            else
                            {
                                // LLM backend (default)
                                translation = TranslateWithAI(normalizedOriginal, extractedNumbers, isOwnUI);
                            }

                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] {backend} returned: {(translation == null ? "(null)" : translation.Substring(0, Math.Min(40, translation.Length)))}");

                            // Handle rate limit: re-queue the text and backoff
                            if (translation == null && _apiRateLimited)
                            {
                                _apiRateLimited = false;
                                lock (lockObj)
                                {
                                    if (!pendingTranslations.Contains(originalText))
                                    {
                                        pendingTranslations.Add(originalText);
                                        translationQueue.Enqueue(originalText);
                                    }
                                }
                                float delaySec = Math.Max(0.1f, Config.rate_limit_retry_delay);
                                Adapter?.LogWarning($"[Worker] Rate limited — re-queued, backing off {delaySec:F1}s ({translationQueue.Count} pending)");
                                // Backoff: wait before retrying (in small increments to respond to shutdown)
                                int delayMs = (int)(delaySec * 1000);
                                for (int i = 0; i < delayMs && !ShuttingDown; i += 100)
                                    Thread.Sleep(Math.Min(100, delayMs - i));
                            }

                            // Discard an answer that invented placeholders: treated as no answer at
                            // all, so nothing is cached and nothing reaches the screen.
                            if (!string.IsNullOrEmpty(translation))
                            {
                                string invented = FindInventedPlaceholders(normalizedOriginal, translation);
                                if (invented != null)
                                {
                                    string badPreview = normalizedOriginal.Length > 40
                                        ? normalizedOriginal.Substring(0, 40) + "..."
                                        : normalizedOriginal;
                                    Adapter?.LogWarning($"[Worker] Discarded answer inventing {invented} (absent from source): '{badPreview}'");
                                    translation = null;
                                }
                            }

                            if (!string.IsNullOrEmpty(translation))
                            {
                                // Check if AI returned the skip marker (text not in expected source language)
                                // Note: Google/DeepL don't return skip markers, so this only applies to LLM
                                bool isSkipped = Answers.Read(translation) == AnswerKind.Skip;

                                // Cache with appropriate tag: S=Skipped, M=Mod UI, A=AI-translated
                                string tag = isSkipped ? "S" : (isOwnUI ? "M" : "A");
                                AddToCache(normalizedOriginal, isSkipped ? normalizedOriginal : translation, tag);

                                if (!isSkipped && translation != normalizedOriginal)
                                {
                                    aiTranslationCount++;

                                    // For updating components, restore actual numbers then variables
                                    string translationWithNumbers = translation;
                                    if (extractedNumbers != null)
                                    {
                                        translationWithNumbers = RestoreNumbersFromPlaceholders(translation, extractedNumbers);
                                    }
                                    translationWithNumbers = VariableManager.RestoreVariables(translationWithNumbers, workerExtractedVars);

                                    // Notify mod loader to update components
                                    OnTranslationComplete?.Invoke(originalText, translationWithNumbers, componentsToUpdate);

                                    // Request visual refresh so static text picks up the new translation
                                    PendingVisualRefresh = true;

                                    if (DebugMode || Config.debug_ai)
                                    {
                                        string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                        Adapter?.LogInfo($"[AI] {preview}");
                                    }
                                }
                                else if (isSkipped && Config.debug_ai)
                                {
                                    string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                    Adapter?.LogInfo($"[AI] Skipped (not in source language): {preview}");
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Adapter?.LogWarning($"[AI] Worker error: {e.Message}");

                        // A retranslation in flight had its line taken out of the file. Whatever
                        // just went wrong, the human must not be left with one line fewer than
                        // they started with.
                        if (retranslate != null)
                        {
                            RestorePreviousEntry(retranslate);
                            FinishRetranslation(retranslate, retranslate.PreviousValue, RetranslateOutcome.Failed);
                        }
                    }
                    finally
                    {
                        isTranslating = false;
                        currentlyTranslating = null;
                    }

                    // Note: pendingTranslations and pendingComponents already cleaned at dequeue time

                    // Note: pendingTranslations and pendingComponents already cleaned at dequeue time
                }
                else
                {
                    // Sleep in small increments to respond quickly to shutdown
                    for (int i = 0; i < 10 && !ShuttingDown; i++)
                        Thread.Sleep(10);
                }
            }

            workerRunning = false;
            LogDebug("[Worker] Thread exiting (shutdown)");
        }

        /// <summary>
        /// Detects the type of text for prompt optimization.
        /// </summary>
        // A name-based "is this a thinking model" list used to live here. It was removed with the
        // /no_think hack: reasoning_effort applies to every model, and the list was wrong anyway —
        // it missed models that do reason (Gemma 4), leaving them slow and occasionally answering
        // with nothing at all.

        /// <summary>
        /// Asking the same question expecting a different answer.
        ///
        /// ⚠ Only a retranslation ever passes one of these. Ordinary translation must stay
        /// deterministic: it is cached, shared and merged, and two runs disagreeing about the same
        /// line would show up as a conflict nobody made.
        ///
        /// The instructions are NOT touched — the human rejected a draw, not the brief.
        /// </summary>
        private sealed class Variation
        {
            public double Temperature;
            public int? Seed;
        }

        private static string TranslateWithAI(string textWithPlaceholders, List<string> extractedNumbers,
            bool isOwnUI = false, Variation variation = null)
        {
            // Security: Reject text that's too long (prevents DoS via large requests).
            // QueueForTranslation turns these back at the door, so this is belt and braces for a
            // caller that reaches here another way. It stores NOTHING: caching the refusal wrote
            // the whole text as its own key AND value, and tagged it "S" — a human decision.
            if (textWithPlaceholders.Length > MaxAITextLength)
            {
                Adapter?.LogWarning($"[AI] Text too long ({textWithPlaceholders.Length} chars), skipping");
                return null;
            }

            try
            {
                string textToTranslate = textWithPlaceholders;
                TextType textType = Prompts.Classify(textToTranslate);

                // === PRE-PROCESS text before prompt building ===
                // Replace structural elements with placeholders so the AI only sees translatable text.
                // 1. Line breaks → [!nl]
                string textForAI = textToTranslate.Replace("\n", "[!nl]");
                // 2. Markup tags (<color=...>, </b>, etc.) → [!t*N]
                List<string> extractedTags = null;
                textForAI = ExtractMarkupTags(textForAI, out extractedTags);
                // 3. Trim leading/trailing whitespace (visual padding confuses AI)
                string leadingWS = "";
                string trailingWS = "";
                string trimmed = textForAI.TrimStart();
                if (trimmed.Length < textForAI.Length)
                {
                    leadingWS = textForAI.Substring(0, textForAI.Length - trimmed.Length);
                    textForAI = trimmed;
                }
                trimmed = textForAI.TrimEnd();
                if (trimmed.Length < textForAI.Length)
                {
                    trailingWS = textForAI.Substring(trimmed.Length);
                    textForAI = trimmed;
                }

                if (Config.debug_ai && extractedTags != null && extractedTags.Count > 0)
                    Adapter?.LogInfo($"[AI] Extracted {extractedTags.Count} markup tags from text");

                // Detect which placeholder types are in the PROCESSED text
                bool hasNlPlaceholders = textForAI.Contains("[!nl]");
                bool hasTagPlaceholders = extractedTags != null && extractedTags.Count > 0;
                bool hasNumberPlaceholders = extractedNumbers != null && extractedNumbers.Count > 0;
                // Presence in THIS text, not "variables exist somewhere": announcing a placeholder
                // the text does not contain invites the model to invent one — small models answered
                // "[!STR*0]" alone, or appended it to an otherwise correct translation.
                bool hasVarPlaceholders = textForAI.Contains(VariableManager.Prefix);

                // === BUILD PROMPT based on processed text ===
                // The wording lives in UnityGameTranslator.Common.Prompts, shared with the bench
                // that scores models against these very instructions. Nothing there reads a
                // configuration: what the prompt depends on is handed over, so the same question
                // can be asked outside a running game.
                var markers = new Prompts.Markers
                {
                    LineBreaks = hasNlPlaceholders,
                    Tags = hasTagPlaceholders,
                    Numbers = hasNumberPlaceholders,
                    Variables = hasVarPlaceholders,
                };

                string targetLang = Config.GetTargetLanguage();
                string sourceLang = Config.GetSourceLanguage();

                string systemPrompt = isOwnUI
                    ? Prompts.ForOwnInterface(targetLang, textType, markers)
                    // The game's own name, never the folder's — see GameInfo.product_name.
                    : Prompts.ForGameText(targetLang, sourceLang, CurrentGame?.product_name,
                                          Config.game_context,
                                          Config.strict_source_language, textType, markers);

                if (Config.debug_ai)
                {
                    Adapter?.LogInfo($"[AI] System prompt:\n{systemPrompt}");
                }

                // === BUILD REQUEST ===
                // Reasoning is disabled through the reasoning_effort parameter (see
                // SendChatRequest), never by appending a marker to the text: the model treats such
                // a marker as content and TRANSLATES it, leaving "/inga_tänkningar" style residue
                // glued to the result — measured on every model tested, including ones that do not
                // reason at all. See analyse/no-think-hack-tests.md.
                string userContent = textForAI;
                int maxTokens = Math.Max(200, textToTranslate.Length * 2);

                // Frozen sequences: placeholders + the game's own delimiters around them.
                // Empty when the text has no placeholder → single attempt, no validation.
                var frozenSequences = Placeholders.FrozenSequences(textForAI);
                bool needsValidation = frozenSequences.Count > 0;

                // === ATTEMPTS: initial call + up to 2 validation retries ===
                // temperature 0 is deterministic: an identical retry would return the
                // same broken answer, so each retry must change something.
                // Attempt 1: normal request, temperature 0.
                // Attempt 2: corrective dialogue — failed answer as assistant turn
                //            + compact targeted feedback (context changed → output changes).
                // Attempt 3: fresh request WITHOUT the failed answer (breaks anchoring),
                //            reinforced system prompt + temperature 0.3 to leave the
                //            deterministic basin that failed twice.
                string translation = null;
                List<string> validationErrors = null;
                string failedResponse = null;
                bool isValid = false;

                // A retranslation raises the floor for all three: the whole point is to leave the
                // basin the rejected answer came from, so a placeholder repair must not quietly
                // drop back to a deterministic draw and hand back the same text.
                double baseTemperature = variation != null ? variation.Temperature : Config.TemperatureNormal;
                int? baseSeed = variation != null ? variation.Seed : Config.ai_seed;
                int maxAttempts = Config.AttemptsAllowed;

                for (int attempt = 0; attempt < maxAttempts && !isValid; attempt++)
                {
                    JArray messagesArray;
                    double temperature = baseTemperature;
                    // Attempts past the first are repairs — a job with its own settings, because it
                    // asks a different question: the same translation, correctly marked up.
                    int? seed = attempt == 0 ? baseSeed : (variation != null ? variation.Seed : Config.ai_seed_repair);

                    if (attempt == 0)
                    {
                        messagesArray = new JArray
                        {
                            new JObject { ["role"] = "system", ["content"] = systemPrompt },
                            new JObject { ["role"] = "user", ["content"] = userContent }
                        };
                    }
                    else if (attempt == 1)
                    {
                        string correction = Placeholders.Correction(validationErrors, frozenSequences);
                        messagesArray = new JArray
                        {
                            new JObject { ["role"] = "system", ["content"] = systemPrompt },
                            new JObject { ["role"] = "user", ["content"] = userContent },
                            new JObject { ["role"] = "assistant", ["content"] = failedResponse },
                            new JObject { ["role"] = "user", ["content"] = correction }
                        };
                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[AI] Retry 1 (corrective dialogue):\n{correction}");
                    }
                    else
                    {
                        temperature = Math.Max(Config.TemperatureRepair, baseTemperature);
                        string reinforcedPrompt = systemPrompt + "\n" + Placeholders.MandatorySequences(frozenSequences);
                        messagesArray = new JArray
                        {
                            new JObject { ["role"] = "system", ["content"] = reinforcedPrompt },
                            new JObject { ["role"] = "user", ["content"] = userContent }
                        };
                        if (Config.debug_ai)
                            Adapter?.LogInfo("[AI] Retry 2 (fresh reinforced prompt, temperature 0.3)");
                    }

                    translation = SendChatRequest(messagesArray, temperature, maxTokens, seed);

                    // Transport/HTTP error (incl. 429): retrying here is pointless,
                    // the worker handles re-queueing on rate limit
                    if (translation == null)
                        return null;

                    if (Config.debug_ai)
                    {
                        Adapter?.LogInfo($"[AI Raw] {translation.Substring(0, Math.Min(80, translation.Length))}");
                    }

                    // Refusal, translation, or neither — see Prompts.ReadAnswer. A refusal is the
                    // marker ALONE: the caller keeps the original and tags it "S", which it can
                    // only decide if the answer says nothing else. An answer that translates AND
                    // carries the marker is thrown away rather than guessed at: read as a refusal
                    // it drops a line that was fine, read as a translation it writes the marker
                    // into the game, and neither shows up until someone reads their own text.
                    var kind = Answers.Read(translation);
                    if (kind == AnswerKind.Skip)
                        return translation;

                    if (kind == AnswerKind.Unusable)
                    {
                        Adapter?.LogWarning($"[AI] Answer carries the skip marker without being it, discarded: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                        return null;
                    }

                    if (!needsValidation)
                        break;

                    isValid = Placeholders.Accepts(textForAI, translation, frozenSequences, out validationErrors);
                    if (!isValid)
                    {
                        // Deterministic trailing-[!nl] repair before rejecting — the
                        // repaired candidate must pass the FULL validation itself.
                        string repairedCandidate = Placeholders.RepairTrailingBreaks(textForAI, translation);
                        if (repairedCandidate != null &&
                            Placeholders.Accepts(textForAI, repairedCandidate, frozenSequences, out _))
                        {
                            translation = repairedCandidate;
                            isValid = true;
                            Adapter?.LogInfo($"[AI] Repaired missing trailing [!nl] token(s), validation OK for: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                        }
                    }
                    if (!isValid)
                    {
                        failedResponse = translation;
                        Adapter?.LogWarning($"[AI] Attempt {attempt + 1}/{maxAttempts}: invalid placeholders ({string.Join("; ", validationErrors)}) for: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                    }
                }

                if (needsValidation && !isValid)
                {
                    // Never cache the corruption. In-memory marker only:
                    // left untranslated this session, retried on next launch.
                    validationFailedTexts.TryAdd(textWithPlaceholders, 0);
                    Adapter?.LogWarning($"[AI] Placeholder validation failed after {maxAttempts} attempts, left untranslated: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                    return null;
                }

                if (!string.IsNullOrEmpty(translation))
                {
                    // Restore placeholders in reverse order of extraction:
                    // 1. Markup tags [!t*N] → original tags
                    translation = RestoreMarkupTags(translation, extractedTags);
                    // 2. Line breaks [!nl] → \n
                    translation = translation.Replace("[!nl]", "\n");
                    // 3. Clean AI artifacts (removes quotes, thinking blocks, etc.)
                    translation = Answers.Clean(translation);
                    // 4. Restore leading/trailing whitespace AFTER clean (clean does Trim)
                    if (leadingWS.Length > 0 || trailingWS.Length > 0)
                        translation = leadingWS + translation + trailingWS;
                    if (Config.debug_ai)
                    {
                        Adapter?.LogInfo($"[AI Clean] {translation?.Substring(0, Math.Min(80, translation?.Length ?? 0))}");
                    }
                }

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[AI] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Send a chat/completions request and return the raw assistant content (trimmed).
        /// Negotiates the request body against whatever this provider accepts (see
        /// AdaptToRejection), and handles rate limiting and HTTP errors.
        /// Returns null on transport/HTTP failure.
        /// </summary>
        private static string SendChatRequest(JArray messagesArray, double temperature, int maxTokens, int? seed = null)
        {
            string aiEndpoint = Endpoints.Resolve(Config.ai_url, "chat/completions");
            EnsureProviderQuirks();

            // Built once, then only the negotiated fields are swapped between attempts:
            // re-assigning the same messages array into a fresh JObject would make Json.NET
            // deep-clone it on every try.
            var requestObj = new JObject
            {
                ["model"] = Config.ai_model,
                ["messages"] = messagesArray,
                ["stream"] = false
            };

            // One attempt per thing we can still give up on, plus the successful one
            for (int attempt = 0; attempt < Negotiation.MaxAttempts; attempt++)
            {
                // max_tokens is the field every OpenAI-compatible server understands; OpenAI's
                // reasoning models are the exception and demand max_completion_tokens. Sending the
                // newer name by default would be worse than useless: Ollama accepts it and IGNORES
                // it (measured), silently removing the cap. Never send both — OpenAI rejects that.
                requestObj.Remove(_negotiation.UnusedTokenField);
                requestObj[_negotiation.TokenField] = maxTokens;

                if (_negotiation.SendTemperature) requestObj["temperature"] = temperature;
                else requestObj.Remove("temperature");

                // Sent only when a caller asked for a different draw of an answer it already has.
                // Several servers accept the field and ignore it, silently — which is why the
                // variation rests on the temperature and treats the seed as a bonus.
                if (seed.HasValue && _negotiation.SendSeed) requestObj["seed"] = seed.Value;
                else requestObj.Remove("seed");

                string effort = _negotiation.ReasoningEffort;
                if (effort != null) requestObj["reasoning_effort"] = effort;
                else requestObj.Remove("reasoning_effort");

                var request = new HttpRequestMessage(HttpMethod.Post, aiEndpoint)
                {
                    Content = new StringContent(requestObj.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json")
                };
                AddAIAuthHeader(request);

                var response = httpClient.SendAsync(request).Result;

                if (response.IsSuccessStatusCode)
                {
                    string responseJson = response.Content.ReadAsStringAsync().Result;
                    var responseObj = ApiClient.ParseJsonSafe(responseJson);
                    return responseObj["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();
                }

                int statusCode = (int)response.StatusCode;
                string errorBody = "";
                try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }

                // Only these mean "this body is not acceptable". 401/404/429/5xx say nothing about
                // our parameters and must not make us give any of them up.
                if (!Negotiation.IsAboutOurRequest(statusCode))
                {
                    if (statusCode == 429) _apiRateLimited = true;
                    Adapter?.LogWarning($"[AI] HTTP {statusCode} {response.StatusCode}: {errorBody}");
                    return null;
                }

                // Adapt the parameter the server actually named, before blaming the reasoning
                // ladder — several of these can be wrong at once on the same model.
                if (_negotiation.Concede(errorBody, out string conceded))
                {
                    Adapter?.LogInfo($"[AI] {conceded}");
                    continue;
                }

                Adapter?.LogWarning($"[AI] HTTP {statusCode} {response.StatusCode}: {errorBody}");
                return null;
            }

            return null;
        }

        /// <summary>
        /// Translate text using Google Translate API v2.
        /// Simpler than LLM: no prompt, no thinking, no artifacts.
        /// Pre-processing (placeholders, tags, whitespace) is done by the caller.
        /// </summary>
        private static string TranslateWithGoogle(string textToTranslate)
        {
            if (string.IsNullOrEmpty(Config.google_api_key))
            {
                Adapter?.LogWarning("[Google] No API key configured");
                return null;
            }

            try
            {
                string targetLang = Config.GetTargetLanguage();
                string targetCode = LanguageHelper.GetGoogleLanguageCode(targetLang);
                if (string.IsNullOrEmpty(targetCode))
                {
                    Adapter?.LogWarning($"[Google] Unsupported target language: {targetLang}");
                    return null;
                }

                var requestObj = new JObject
                {
                    ["q"] = textToTranslate,
                    ["target"] = targetCode,
                    ["format"] = "text"
                };

                // Add source language if specified
                string sourceLang = Config.GetSourceLanguage();
                if (!string.IsNullOrEmpty(sourceLang))
                {
                    string sourceCode = LanguageHelper.GetGoogleLanguageCode(sourceLang);
                    if (!string.IsNullOrEmpty(sourceCode))
                        requestObj["source"] = sourceCode;
                }

                string endpoint = "https://translation.googleapis.com/language/translate/v2";
                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("X-Goog-Api-Key", Config.google_api_key);

                var response = httpClient.SendAsync(request).Result;

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 429)
                        _apiRateLimited = true;
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }
                    Adapter?.LogWarning($"[Google] HTTP {statusCode}: {errorBody}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = ApiClient.ParseJsonSafe(responseJson);
                string translation = responseObj["data"]?["translations"]?[0]?["translatedText"]?.ToString();

                if (Config.debug_ai)
                    Adapter?.LogInfo($"[Google] '{textToTranslate}' -> '{translation}'");

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Google] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Translate text using DeepL API v2.
        /// Simpler than LLM: no prompt, no thinking, no artifacts.
        /// Pre-processing (placeholders, tags, whitespace) is done by the caller.
        /// </summary>
        private static string TranslateWithDeepL(string textToTranslate)
        {
            if (string.IsNullOrEmpty(Config.deepl_api_key))
            {
                Adapter?.LogWarning("[DeepL] No API key configured");
                return null;
            }

            try
            {
                string targetLang = Config.GetTargetLanguage();
                string targetCode = LanguageHelper.GetDeepLLanguageCode(targetLang, isTarget: true);
                if (string.IsNullOrEmpty(targetCode))
                {
                    Adapter?.LogWarning($"[DeepL] Unsupported target language: {targetLang}");
                    return null;
                }

                var requestObj = new JObject
                {
                    ["text"] = new JArray { textToTranslate },
                    ["target_lang"] = targetCode
                };

                // Add source language if specified
                string sourceLang = Config.GetSourceLanguage();
                if (!string.IsNullOrEmpty(sourceLang))
                {
                    string sourceCode = LanguageHelper.GetDeepLLanguageCode(sourceLang, isTarget: false);
                    if (!string.IsNullOrEmpty(sourceCode))
                        requestObj["source_lang"] = sourceCode;
                }

                string endpoint = Config.deepl_use_free
                    ? "https://api-free.deepl.com/v2/translate"
                    : "https://api.deepl.com/v2/translate";

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {Config.deepl_api_key}");

                var response = httpClient.SendAsync(request).Result;

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 429)
                        _apiRateLimited = true;
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }
                    Adapter?.LogWarning($"[DeepL] HTTP {statusCode}: {errorBody}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = ApiClient.ParseJsonSafe(responseJson);
                string translation = responseObj["translations"]?[0]?["text"]?.ToString();

                if (Config.debug_ai)
                    Adapter?.LogInfo($"[DeepL] '{textToTranslate}' -> '{translation}'");

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[DeepL] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Translate text using a translation API (Google or DeepL).
        /// Handles pre/post-processing (placeholders, tags, whitespace) like TranslateWithAI but without prompts.
        /// </summary>
        private static string TranslateWithAPI(string textWithPlaceholders, List<string> extractedNumbers)
        {
            // Same as the AI path: refused at the queue door, nothing stored if we get here anyway
            if (textWithPlaceholders.Length > MaxAITextLength)
            {
                Adapter?.LogWarning($"[API] Text too long ({textWithPlaceholders.Length} chars), skipping");
                return null;
            }

            try
            {
                string textToTranslate = textWithPlaceholders;

                // === PRE-PROCESS: same placeholder extraction as LLM ===
                // 1. Line breaks → [!nl]
                string textForAPI = textToTranslate.Replace("\n", "[!nl]");
                // 2. Markup tags → [!t*N]
                List<string> extractedTags = null;
                textForAPI = ExtractMarkupTags(textForAPI, out extractedTags);
                // 3. Trim whitespace
                string leadingWS = "";
                string trailingWS = "";
                string trimmed = textForAPI.TrimStart();
                if (trimmed.Length < textForAPI.Length)
                {
                    leadingWS = textForAPI.Substring(0, textForAPI.Length - trimmed.Length);
                    textForAPI = trimmed;
                }
                trimmed = textForAPI.TrimEnd();
                if (trimmed.Length < textForAPI.Length)
                {
                    trailingWS = textForAPI.Substring(trimmed.Length);
                    textForAPI = trimmed;
                }

                // Skip empty text after pre-processing
                if (string.IsNullOrWhiteSpace(textForAPI))
                    return null;

                // === CALL THE API ===
                string translation = null;
                switch (Config.translation_backend)
                {
                    case "google":
                        translation = TranslateWithGoogle(textForAPI);
                        break;
                    case "deepl":
                        translation = TranslateWithDeepL(textForAPI);
                        break;
                }

                if (string.IsNullOrEmpty(translation))
                    return null;

                // Structural placeholder validation. No retry here: these APIs take
                // no prompt, so there is nothing to correct — but a broken result
                // must never reach the cache (it would be permanent). The deterministic
                // trailing-[!nl] repair applies before rejecting, same as the AI path.
                var frozenSequences = Placeholders.FrozenSequences(textForAPI);
                if (frozenSequences.Count > 0
                    && !Placeholders.Accepts(textForAPI, translation, frozenSequences, out var apiErrors))
                {
                    string repairedCandidate = Placeholders.RepairTrailingBreaks(textForAPI, translation);
                    if (repairedCandidate != null &&
                        Placeholders.Accepts(textForAPI, repairedCandidate, frozenSequences, out _))
                    {
                        translation = repairedCandidate;
                        Adapter?.LogInfo($"[API] Repaired missing trailing [!nl] token(s), validation OK for: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                    }
                    else
                    {
                        validationFailedTexts.TryAdd(textWithPlaceholders, 0);
                        Adapter?.LogWarning($"[API] Invalid placeholders ({string.Join("; ", apiErrors)}), left untranslated: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                        return null;
                    }
                }

                // === POST-PROCESS: restore placeholders ===
                // Restore markup tags
                if (extractedTags != null && extractedTags.Count > 0)
                {
                    translation = RestoreMarkupTags(translation, extractedTags);
                }
                // Restore [!nl] → \n
                translation = translation.Replace("[!nl]", "\n");
                // Restore whitespace
                translation = leadingWS + translation + trailingWS;

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[API] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Normalize line endings to Unix format (\n).
        /// Converts \r\n (Windows) and \r (old Mac) to \n.
        /// This ensures consistent keys across platforms.
        /// </summary>
        public static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Order is important: first \r\n, then \r
            // Otherwise \r\n would become \n\n
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// Max value for the per-entry capture-order index "i": JavaScript's
        /// Number.MAX_SAFE_INTEGER (2^53 - 1), the web editor being the consumer.
        /// </summary>
        internal const long MaxOrderIndex = 9007199254740991L;

        /// <summary>
        /// Parse the optional capture-order index "i" of a translation entry.
        /// NEVER throws: an invalid or out-of-range value reads as "no index"
        /// (LoadCache's catch-all resets the cache and regenerates the UUID,
        /// so a corrupted download must not be able to trigger it).
        /// </summary>
        private static long? ParseTranslationIndex(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
                return null;

            try
            {
                long value = token.Value<long>();
                return (value >= 1 && value <= MaxOrderIndex) ? value : (long?)null;
            }
            catch
            {
                // Integer beyond long range (BigInteger) — treat as absent
                return null;
            }
        }

        /// <summary>
        /// Reserve the next capture-order index. Lock-protected because entries
        /// are created both from the worker thread (AddToCache) and the main
        /// thread (in-game editor); Monitor is re-entrant, so callers already
        /// holding lockObj are fine.
        /// </summary>
        private static long NextOrderIndex()
        {
            lock (lockObj)
            {
                return nextTranslationIndex++;
            }
        }

        /// <summary>
        /// Re-sync the capture-order counter with the current cache (max+1).
        /// Call after bulk cache replacements (merge apply): the other branch
        /// can bring in indices above our counter, and future captures must
        /// never reuse them. Never lowers the counter.
        /// </summary>
        public static void SyncOrderIndexCounter()
        {
            long highest = 0;
            foreach (var kvp in TranslationCache)
            {
                if (kvp.Value.Index.HasValue && kvp.Value.Index.Value > highest)
                    highest = kvp.Value.Index.Value;
            }
            lock (lockObj)
            {
                if (nextTranslationIndex <= highest)
                    nextTranslationIndex = highest + 1;
            }
        }

        /// <summary>
        /// Add a translation to the cache with an optional tag.
        /// </summary>
        /// <param name="original">Original text (key)</param>
        /// <param name="translated">Translated text (value)</param>
        /// <param name="tag">Tag: A=AI, H=Human, V=Validated (default: A)</param>
        public static void AddToCache(string original, string translated, string tag = "A")
        {
            if (string.IsNullOrEmpty(original))
                return;

            // Allow empty translated value for capture-only mode (H tag with empty value)
            if (string.IsNullOrEmpty(translated) && tag != "H")
                return;

            // Normalize line endings for cross-platform consistency
            string normalizedKey = NormalizeLineEndings(original);
            string normalizedValue = NormalizeLineEndings(translated ?? "");

            lock (lockObj)
            {
                if (TranslationCache.ContainsKey(normalizedKey))
                    return;

                // Last stop before an entry exists: every route that creates one passes here, so this
                // is where the read-back guard finally belongs. Guarding the queue, then the
                // synchronous translate path, each time left another route open — the same
                // target-language key kept coming back. A key we can recognise as our own translation
                // wearing a different decoration must never become an entry, whoever asked for it.
                // The stack is logged once so the caller that got this far is named, not guessed at.
                if (!TranslationCache.ContainsKey(normalizedKey) && IsReadbackOfOwnTranslation(normalizedKey))
                {
                    if (_readbackStoreLogged < 3)
                    {
                        _readbackStoreLogged++;
                        Adapter.LogWarning($"[Readback] Refused to store a re-decorated translation as a new key: '{(normalizedKey.Length > 70 ? normalizedKey.Substring(0, 70) + "..." : normalizedKey)}'\n{Environment.StackTrace}");
                    }
                    return;
                }

                // Re-adding an existing key keeps its original capture order;
                // only genuinely new keys consume a new index
                long? orderIndex;
                if (TranslationCache.TryGetValue(normalizedKey, out var previousEntry) && previousEntry.Index.HasValue)
                    orderIndex = previousEntry.Index;
                else
                    orderIndex = NextOrderIndex();

                var entry = new TranslationEntry
                {
                    Value = normalizedValue,
                    Tag = tag ?? "A",
                    Index = orderIndex
                };

                TranslationCache[normalizedKey] = entry;
                cacheModified = true;
                if (entry.Tag == "M") TranslationHasUILines = true;

                // Track local changes (if different from ancestor or new)
                if (AncestorCache.Count > 0)
                {
                    if (!AncestorCache.TryGetValue(normalizedKey, out var ancestorEntry) ||
                        ancestorEntry.Value != entry.Value ||
                        ancestorEntry.Tag != entry.Tag)
                    {
                        LocalChangesCount++;
                    }
                }
                else
                {
                    // No ancestor = all translations are local
                    LocalChangesCount++;
                }

                // Add to reverse cache (only if value is non-empty and different from key)
                if (normalizedKey != entry.Value && !string.IsNullOrEmpty(entry.Value))
                {
                    string normalizedTranslation = NormalizeLineEndings(entry.Value);
                    if (Config.normalize_numbers)
                    {
                        normalizedTranslation = ExtractNumbersToPlaceholders(normalizedTranslation, out _);
                    }
                    normalizedTranslation = normalizedTranslation.TrimEnd();
                    translatedTexts.TryAdd(normalizedTranslation, 0);
                    IndexReadbackTranslation(normalizedKey, entry.Value);
                }

                // Note: No longer clearing lastSeenText here.
                // OnTranslationComplete updates tracked components directly.
                // New components will be translated on their next scan cycle.

                if (normalizedKey.Contains(PlaceholderPrefix))
                {
                    BuildPatternEntries();
                }

                if (DebugMode)
                    Adapter?.LogInfo($"[Cache+] {normalizedKey.Substring(0, Math.Min(40, normalizedKey.Length))}... [{tag}]");
            }
        }

        public static string ExtractNumbersToPlaceholders(string text, out List<string> extractedNumbers)
        {
            extractedNumbers = new List<string>();

            if (string.IsNullOrEmpty(text))
                return text;

            var matches = NumberPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var numbersWithIndex = new List<Tuple<string, int, int>>();
            foreach (Match match in matches)
            {
                if (!IsPartOfHexColor(text, match.Index, match.Length)
                    && !IsInsidePlaceholder(text, match.Index))
                {
                    numbersWithIndex.Add(Tuple.Create(match.Value, match.Index, match.Length));
                }
            }

            if (numbersWithIndex.Count == 0)
                return text;

            foreach (var num in numbersWithIndex)
            {
                extractedNumbers.Add(num.Item1);
            }

            var result = new StringBuilder(text);
            for (int i = numbersWithIndex.Count - 1; i >= 0; i--)
            {
                var num = numbersWithIndex[i];
                result.Remove(num.Item2, num.Item3);
                result.Insert(num.Item2, $"{PlaceholderPrefix}{i}{PlaceholderSuffix}");
            }

            return result.ToString();
        }

        public static string RestoreNumbersFromPlaceholders(string text, List<string> numbers)
        {
            if (string.IsNullOrEmpty(text) || numbers == null || numbers.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < numbers.Count; i++)
            {
                result = result.Replace($"{PlaceholderPrefix}{i}{PlaceholderSuffix}", numbers[i]);
            }
            return result;
        }

        private static bool IsPartOfHexColor(string text, int index, int length)
        {
            for (int i = index - 1; i >= 0 && i >= index - 8; i--)
            {
                char c = text[i];
                if (c == '#')
                    return true;
                if (!IsHexChar(c))
                    break;
            }
            return false;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>
        /// Check if a number at the given index is inside a [!v*N] placeholder.
        /// </summary>
        private static bool IsInsidePlaceholder(string text, int index)
        {
            // Look backwards for "[!v*" or "[!STR*" patterns
            // Protects numbers inside [!v*0], [!STR*0], [!t*0] from being extracted
            for (int i = index - 1; i >= Math.Max(0, index - 8); i--)
            {
                // Check for [!v* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 'v' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!STR* (6 chars)
                if (i >= 5 && text[i] == '*' && text[i - 1] == 'R' && text[i - 2] == 'T' && text[i - 3] == 'S'
                    && text[i - 4] == '!' && text[i - 5] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!t* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 't' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Normalize text for cache lookup (line endings + number extraction).
        /// Used by typewriting stabilizer to check if text is already cached.
        /// </summary>
        /// <summary>
        /// Quick check if a text has a cached translation (without doing the full translation).
        /// Used to decide whether to apply the clone font before translation.
        /// </summary>
        private static int _dbgTwCacheHit = 0;
        private static int _dbgReverseMiss = 0;

        /// <summary>
        /// Check if a normalized text is a "natural identity" — contains only digits,
        /// punctuation, whitespace, placeholders and rich text tags. Such text is the
        /// same in any language and an identity translation (key==value) is expected,
        /// not an AI failure.
        /// </summary>
        private static bool IsNaturalIdentity(string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText)) return true;

            // Strip placeholders [!v*N] [!STR*N] and rich text tags <...>
            // then check if remaining text has any letters
            var stripped = new System.Text.StringBuilder(normalizedText.Length);
            for (int i = 0; i < normalizedText.Length; i++)
            {
                char c = normalizedText[i];
                if (c == '[' && i + 1 < normalizedText.Length && normalizedText[i + 1] == '!')
                {
                    int end = normalizedText.IndexOf(']', i);
                    if (end > i) { i = end; continue; }
                }
                if (c == '<')
                {
                    int end = normalizedText.IndexOf('>', i);
                    if (end > i) { i = end; continue; }
                }
                stripped.Append(c);
            }

            return IsNumericOrSymbol(stripped.ToString());
        }

        public static bool HasCachedTranslation(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Exact match as key
            if (TranslationCache.TryGetValue(text, out var exact))
            {
                if (exact.IsHumanEmpty || exact.Tag == "S") return false;
                // key==value with tag "A" = AI couldn't translate (source language text).
                // Exception: natural identity (only digits/punctuation/placeholders) is expected
                // to be identical — not an AI failure.
                // key==value with tag "V"/"H" = human validated, intentionally same text.
                if (exact.Value == text && exact.Tag == "A" && !IsNaturalIdentity(text)) return false;
                return true;
            }

            // Normalized match as key
            string normalized = NormalizeForCacheLookup(text);
            if (TranslationCache.TryGetValue(normalized, out var norm))
            {
                if (norm.IsHumanEmpty || norm.Tag == "S") return false;
                if (norm.Value == normalized && norm.Tag == "A" && !IsNaturalIdentity(normalized)) return false;
                return true;
            }

            // Text is already a known translation (reverse cache) — the component
            // already shows translated text and should have the clone font.
            string trimmed = normalized.TrimEnd();
            if (translatedTexts.ContainsKey(trimmed))
                return true;

            return false;
        }

        /// <summary>
        /// Reverse cache lookup: given a translated string a component is displaying, find the SOURCE
        /// it was translated from. Enables restoring a component that received ALREADY-translated text
        /// (e.g. a title's shadow/duplicate layer copied from the main layer) and so never had an
        /// original stored (issue #21: such a component could not revert on disable). O(cache) scan —
        /// call only for the rare untracked-yet-translated component. Returns null if not found.
        /// </summary>
        public static string GetSourceForTranslation(string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText)) return null;
            string norm = NormalizeForCacheLookup(translatedText).TrimEnd();
            foreach (var kv in TranslationCache)
            {
                var entry = kv.Value;
                if (entry == null || entry.IsHumanEmpty || entry.Tag == "S" || string.IsNullOrEmpty(entry.Value))
                    continue;
                if (entry.Value == translatedText || NormalizeForCacheLookup(entry.Value).TrimEnd() == norm)
                    return kv.Key;
            }
            return null;
        }

        public static string NormalizeForCacheLookup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string normalized = NormalizeLineEndings(text);
            // Variables BEFORE numbers (variables may contain digits)
            if (VariableManager.HasVariables)
                normalized = VariableManager.ExtractVariables(normalized, out _);
            if (Config.normalize_numbers)
                normalized = ExtractNumbersToPlaceholders(normalized, out _);
            return normalized;
        }

        /// <summary>
        /// Put a text in front of the translation backend.
        ///
        /// Returns false when the text was turned away at the door — switched off, offline, too
        /// long, already in the target language. Every scanner path ignores that answer (a text
        /// refused here is simply left as it is on screen), but a caller that DELETED something to
        /// make room for the answer must know: see RemoveTranslationForRetranslate, which puts the
        /// previous translation back rather than leave the line with nothing.
        /// </summary>
        public static bool QueueForTranslation(string text, object component = null, bool isOwnUI = false)
        {
            // Capture-only mode needs the queue too: entries are stored as
            // H+empty by the worker without any backend call
            if (!Config.IsTranslationEnabled && !Config.capture_keys_only) return false;

            // 🔴 Presentation forms never enter the queue — so they can never become a cache KEY.
            // Two ways such text reaches a gate: our own composed output read back during the
            // short window where a cache reload emptied the presented→logical table (the
            // registration is gone, the screen still shows shaped text), and a game that ships
            // its own RTL support (RTLTMPro hands the base setter shaped strings). The first is
            // ours and must be dropped; the second is a real source this project cannot
            // translate yet (unshaping is ambiguous — issue #24 scope, §6.4-4): logged so the
            // limitation is visible instead of silent.
            if (text != null && TextShaping.RtlText.ContainsPresentationForms(text))
            {
                if (_shapedQueueRefusals++ < 3)
                    LogWarning($"[Queue] Refused presentation-form text as a source key (own composed output, or a game already shipping shaped RTL — not translatable yet): '{(text.Length > 40 ? text.Substring(0, 40) + "…" : text)}'");
                return false;
            }
            // Google/DeepL require online mode
            if (Config.ActiveBackendRequiresOnline && !Config.online_mode) return false;
            if (string.IsNullOrEmpty(text)) return false;
            if (IsNumericOrSymbol(text)) return false;

            // Longer than any backend will accept. Refused HERE, at the single door, rather than
            // deeper down where the refusal used to be recorded as a cache entry tagged "S".
            //
            // That entry was an aberration twice over. The cache key IS the source text, and the
            // value was the same text again, so a credits or licence blob added some thirty
            // kilobytes to translations.json — a file that is uploaded, hashed, merged and shown.
            // And it recorded a technical give-up under the tag that means "a human decided to
            // keep this as it is", which is the tag the quality score is about to rely on.
            //
            // Nothing is stored now: the line stays untranslated in the game, which is the honest
            // signal, and the check being deterministic on the text itself, the scanner simply
            // turns back here on every pass — nothing queued, nothing sent.
            if (text.Length > MaxAITextLength)
            {
                lock (lockObj)
                {
                    // Once per text: this runs on every scan, and a warning repeated forever is
                    // noise. Silence would be worse — a line that never gets translated has to
                    // say why somewhere.
                    if (tooLongTexts.Add(text))
                        Adapter?.LogWarning($"[Queue] Text too long ({text.Length} chars, limit {MaxAITextLength}), left untranslated");
                }
                return false;
            }
            // Last line of defence, here rather than only at the call sites: this is the single door
            // into the queue, and guarding the two obvious callers still let target-language text
            // through by other routes (a stored entry whose translation was already indexed came back
            // and was translated again, drifting). Own UI is exempt: its labels are source text we
            // produce ourselves, never a read-back of the game's rendering.
            if (!isOwnUI && IsAlreadyTargetText(text)) return false;

            lock (lockObj)
            {
                if (component != null)
                {
                    if (!pendingComponents.ContainsKey(text))
                        pendingComponents[text] = new List<object>();
                    // Same reference, one entry. Without this, a component whose text waits long
                    // in the queue is re-added on every scan cycle — a UI Toolkit element (whose
                    // GetInstanceID is -1) reached 137 strong references for ONE label, i.e. 136
                    // useless apply iterations and that many elements pinned against collection.
                    // (Reference equality: two IL2CPP proxies of one native object still slip
                    // through — bounded by proxy caching, and harmless beyond a wasted slot.)
                    var waiting = pendingComponents[text];
                    if (!waiting.Contains(component)) waiting.Add(component);
                }

                // Record the own-UI intent: the worker also infers it from the components, but a
                // code-owned label may be queued without any (see the dequeue). Never set from
                // game text, so it cannot turn a game string into mod UI by coincidence.
                if (isOwnUI)
                    pendingOwnUITexts.Add(text);

                if (pendingTranslations.Contains(text)) return true;

                pendingTranslations.Add(text);
                translationQueue.Enqueue(text);

                // Log first queued item always, then every 10th
                int queueSize = translationQueue.Count;
                if (DebugMode || Config.debug_ai)
                {
                    string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                    LogDebug($"[Queue] #{queueSize}: {preview}{(isOwnUI ? " (UI)" : "")}");
                }
            }

            return true;
        }

        /// <summary>
        /// Main translation method - translate text from cache or queue for AI.
        /// Treats multiline text as a single unit to preserve context and ensure consistency.
        /// </summary>
        public static string TranslateText(string text)
        {
            // Switched off, or nobody has agreed to any of this yet
            if (!TranslationsActive)
                return text;

            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Third door into translation, alongside the queue and the tracking path: this one
            // translates synchronously and so never met the guard placed on QueueForTranslation.
            // It is how target-language text kept being re-translated after that guard was added.
            if (IsAlreadyTargetText(text))
                return text;

            // No line splitting - treat multiline as single unit for context preservation
            string result = TranslateSingleText(text);
            if (result != text)
            {
                translatedCount++;
                string normalizedResult = NormalizeLineEndings(result);
                if (Config.normalize_numbers)
                    normalizedResult = ExtractNumbersToPlaceholders(normalizedResult, out _);
                normalizedResult = normalizedResult.TrimEnd();
                if (!translatedTexts.ContainsKey(normalizedResult))
                    translatedTexts.TryAdd(normalizedResult, 0);
                // Index straight away: the read-back happens within the same session, often within
                // the same frame, so waiting for the next cache load would miss the whole point.
                IndexReadbackTranslation(text, result);
            }
            return result;
        }

        public static string TranslateSingleText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Normalize line endings FIRST (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Extract string variables BEFORE numbers (variables may contain digits)
            string afterVars = lineNormalized;
            List<KeyValuePair<int, string>> extractedVars = null;
            if (VariableManager.HasVariables)
            {
                afterVars = VariableManager.ExtractVariables(lineNormalized, out extractedVars);
            }

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = afterVars;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);
            }

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out var cachedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedEntry.IsHumanEmpty || cachedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedEntry.Value != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    // Restore numbers first, then variables
                    string result = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                    return VariableManager.RestoreVariables(result, extractedVars);
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            string trimmed = normalizedText.Trim();
            if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out var cachedTrimmedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedTrimmedEntry.IsHumanEmpty || cachedTrimmedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedTrimmedEntry.Value != trimmed)
                {
                    cacheHitCount++;
                    string trimResult = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                        : cachedTrimmedEntry.Value;
                    return VariableManager.RestoreVariables(trimResult, extractedVars);
                }
            }

            // If found in cache with key == value, no translation needed, don't queue
            if (foundInCache)
            {
                return text;
            }

            // Pattern matching (keep for non-number patterns)
            string patternResult = TryPatternMatch(text);
            if (patternResult != null)
            {
                translatedCount++;
                return patternResult;
            }

            if ((Config.IsTranslationEnabled || Config.capture_keys_only) && !string.IsNullOrEmpty(text))
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized + trimmed)
                // TrimEnd because TMP often strips trailing whitespace/newlines when displaying
                string trimmedNormalized = normalizedText.TrimEnd();
                if (IsAlreadyTargetText(text, trimmedNormalized))
                {
                    skippedAlreadyTranslated++;
                    return text;
                }

                // Text may be a translation from the pre-reload cache still displayed
                // (component missed by RestoreAllOriginals) — refresh it, never queue it
                string staleRefreshed = TryResolveStaleTranslation(text, trimmedNormalized, null);
                if (staleRefreshed != null)
                    return staleRefreshed;

                // A never-seen text may miss only because variable values went
                // stale (game assigned a new seed/name this frame). Refresh and
                // retry the whole lookup once — RefreshOnMiss is throttled to
                // once per frame, so the recursion cannot loop.
                if (VariableManager.RefreshOnMiss())
                    return TranslateSingleText(text);

                QueueForTranslation(text);
            }

            return text;
        }

        /// <summary>
        /// Translate with component tracking for async updates.
        /// Treats multiline text as a single unit to ensure proper component tracking.
        /// </summary>
        /// <param name="isOwnUI">If true, use UI-specific prompt for mod interface translation.</param>
        /// <summary>
        /// Translate a DYNAMIC own-UI label synchronously, at the moment the code sets it. For text the
        /// mod rewrites itself — a state button ("Apply (N)" / "Close"), a live counter, a status line —
        /// the async translation pipeline would RACE with the code (two writers on one Text), leaving it
        /// stuck or inconsistent. So the code translates HERE instead: cache hit → returns the translation
        /// immediately (numbers handled as placeholders, e.g. "Apply (NUM)"); cache miss → returns the
        /// English and queues it for next time. Returns English unchanged when translate_mod_ui is off, so
        /// the label follows the current language automatically (no separate restore needed). The label
        /// must be RegisterExcluded so the set_text patch doesn't translate it a second time.
        /// </summary>
        public static string TranslateOwnUIDynamic(string englishText, object component = null)
        {
            if (string.IsNullOrEmpty(englishText) || Config == null || !ShouldTranslateOwnUI)
                return englishText;

            string result = TranslateTextWithTracking(englishText, component, isOwnUI: true);

            // Submit explicitly on a miss. The shared path refuses to queue ANY own-UI text — an
            // anti-loop guard against our own translated writes coming back through the set_text
            // patch — so a code-owned label has no submitter at all: the whitelist refresh only
            // walks RegisterUIText'd components, and these labels are RegisterExcluded by contract.
            // Same direct-enqueue route as TranslatorUIManager.RetriggerOwnUIText.
            //
            // Once per text per session: an answer that yields no cache entry (backend returned
            // nothing) would otherwise leave the miss standing, and a label rewritten every frame
            // — the overlay status line — would re-submit forever.
            if (result == englishText && !IsOwnUITextKnown(englishText))
            {
                bool firstSubmission;
                lock (lockObj) { firstSubmission = ownUISubmitted.Add(englishText); }
                if (firstSubmission)
                    QueueForTranslation(englishText, component, isOwnUI: true);
            }

            return result;
        }

        /// <summary>
        /// True when the cache already holds an entry for this own-UI text, WHATEVER its verdict
        /// (translated, skipped, or "same as the source"). Deliberately not HasCachedTranslation,
        /// which answers "is there a usable translation" and stays false for a skipped or identical
        /// entry — a label written on every refresh would then be re-queued forever.
        /// </summary>
        private static bool IsOwnUITextKnown(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Same key shape as the storage path: line endings, then numbers as placeholders.
            // No variable extraction — own UI never goes through it (see the worker).
            string key = NormalizeLineEndings(text);
            if (Config != null && Config.normalize_numbers)
                key = ExtractNumbersToPlaceholders(key, out _);

            if (TranslationCache.ContainsKey(key)) return true;
            string trimmed = key.Trim();
            return trimmed != key && TranslationCache.ContainsKey(trimmed);
        }

        public static string TranslateTextWithTracking(string text, object component, bool isOwnUI = false, bool skipTypewriting = false, bool skipQueueing = false)
        {
            // Switched off, or nobody has agreed to any of this yet. This is THE bottleneck every
            // translation path goes through — the Harmony patches included, which is what made a
            // cache full of translations show up on screen while the wizard was still open.
            if (!TranslationsActive)
            {
                // Debug: log first time to confirm this check works
                if (_enableTranslationsLogOnce)
                {
                    _enableTranslationsLogOnce = false;
                    LogInfo(SetupCompleted
                        ? "[TranslatorCore] enable_translations=false, skipping translation"
                        : "[TranslatorCore] setup not completed yet, skipping translation until the wizard is done");
                }
                return text;
            }

            if (string.IsNullOrEmpty(text))
                return text;

            // Don't split multiline - treat as single unit for proper component tracking
            // (IsNumericOrSymbol check is in TranslateSingleTextWithTracking — no need to call twice)
            string result = TranslateSingleTextWithTracking(text, component, isOwnUI, skipTypewriting, skipQueueing);
            if (result != text)
            {
                translatedCount++;
                FontManager.EnsureCharsInCloneAtlas(result, component);

                string normalizedResult = NormalizeLineEndings(result);
                if (Config.normalize_numbers)
                    normalizedResult = ExtractNumbersToPlaceholders(normalizedResult, out _);
                normalizedResult = normalizedResult.TrimEnd();
                if (!translatedTexts.ContainsKey(normalizedResult))
                    translatedTexts.TryAdd(normalizedResult, 0);
                // Index straight away: the read-back happens within the same session, often within
                // the same frame, so waiting for the next cache load would miss the whole point.
                IndexReadbackTranslation(text, result);
            }
            return result;
        }

        private static string TranslateSingleTextWithTracking(string text, object component, bool isOwnUI = false, bool skipTypewriting = false, bool skipQueueing = false)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Read-back detection: if the game read translated text and appended
            // untranslated content, reconstruct the source-language text.
            if (component is Component rbComp)
            {
                int rbId = TypeHelper.GetInstanceID(rbComp);
                string reconstructed = TranslatorPatches.DetectReadBack(rbId, text);
                if (reconstructed != null)
                    text = reconstructed;
            }

            // Fast path: check concat assembled cache (runtime only, not JSON)
            // Catches full tooltip texts that were assembled from translated deltas.
            string concatResult = TranslatorPatches.GetConcatCacheResult(text);
            if (concatResult != null)
            {
                translatedCount++;
                return concatResult;
            }
            // Also skip if the text is a known concat translation result (FR text)
            if (TranslatorPatches.IsConcatTranslatedValue(text))
            {
                return text; // already translated, don't re-process
            }

            // Fast path: try exact text lookup BEFORE any normalization (avoids allocations for cache hits)
            if (TranslationCache.TryGetValue(text, out var exactEntry))
            {
                // If this component is in typewriting state, touch the timestamp
                // so the stabilizer doesn't think the typewriting stopped.
                // Cache hits bypass IsTypewritingInProgress, leaving the timestamp stale.
                if (component is Component twComp2)
                {
                    int twId2 = TypeHelper.GetInstanceID(twComp2);
                    TranslatorPatches.TouchTypewritingTimestamp(twId2, text);
                }

                if (exactEntry.IsHumanEmpty || exactEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (exactEntry.Value != text)
                {
                    cacheHitCount++;
                    translatedCount++;
                    // TEMP LOG: detect if typewriting text gets a cache hit before typewriting check
                    if (_dbgTwCacheHit < 20 && component is Component twComp)
                    {
                        int twId = TypeHelper.GetInstanceID(twComp);
                        if (TranslatorPatches.IsInTypewritingState(twId))
                        {
                            _dbgTwCacheHit++;
                            LogDebug($"[TW-CACHEHIT] comp={twId} text='{(text.Length > 40 ? text.Substring(0,40) : text)}' → cache hit BYPASSES typewriting check");
                        }
                    }
                    if (DebugMode && text.Length > 100)
                    {
                        int cId = (component is Component dc) ? TypeHelper.GetInstanceID(dc) : -1;
                        LogDebug($"[CACHE-HIT-LONG] comp={cId}\n  key({text.Length}c)='{text}'\n  val({exactEntry.Value.Length}c)='{exactEntry.Value}'");
                    }
                    if (component != null)
                    {
                        TranslatorScanner.StoreOriginalText(component, text);
                        int trackId = TypeHelper.GetInstanceID(component);
                        TranslatorPatches.TrackTranslation(trackId, text, exactEntry.Value);
                    }
                    return exactEntry.Value;
                }
                // key == value: no translation needed, return as-is
                cacheHitCount++;
                if (DebugMode && text.Length > 100)
                {
                    int cId = (component is Component dc2) ? TypeHelper.GetInstanceID(dc2) : -1;
                    LogDebug($"[CACHE-HIT-SAME] comp={cId} key==val({text.Length}c)='{text}'");
                }
                return text;
            }

            // Normalize line endings (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Extract string variables BEFORE numbers — never on our own GUI (game variables
            // have no meaning there, and a colliding value would eat the label; see the worker).
            string afterVars = lineNormalized;
            List<KeyValuePair<int, string>> extractedVars = null;
            if (VariableManager.HasVariables && !isOwnUI)
            {
                afterVars = VariableManager.ExtractVariables(lineNormalized, out extractedVars);
            }

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = afterVars;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);
            }

            string translation = null;

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out var cachedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedEntry.IsHumanEmpty || cachedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedEntry.Value != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    if (DebugMode && text.Length > 100)
                    {
                        int cId = (component is Component dc3) ? TypeHelper.GetInstanceID(dc3) : -1;
                        LogDebug($"[CACHE-HIT-NORM] comp={cId} orig({text.Length}c) norm→key({normalizedText.Length}c)\n  orig='{text}'\n  norm='{normalizedText}'");
                    }
                    // Restore numbers then variables in the translation
                    string rawTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                    translation = VariableManager.RestoreVariables(rawTranslation, extractedVars);
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            if (translation == null && !foundInCache)
            {
                string trimmed = normalizedText.Trim();
                if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out var cachedTrimmedEntry))
                {
                    foundInCache = true;
                    // H+empty (capture-only) or S (skipped): return original text
                    if (cachedTrimmedEntry.IsHumanEmpty || cachedTrimmedEntry.Tag == "S")
                    {
                        cacheHitCount++;
                        return text;
                    }
                    if (cachedTrimmedEntry.Value != trimmed)
                    {
                        cacheHitCount++;
                        string rawTrimTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                            ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                            : cachedTrimmedEntry.Value;
                        translation = VariableManager.RestoreVariables(rawTrimTranslation, extractedVars);
                    }
                }
            }

            // Pattern matching no longer needed for numbers (normalized lookup handles it)
            // But keep for other patterns that might exist
            if (translation == null)
            {
                string patternResult = TryPatternMatch(text);
                if (patternResult != null)
                {
                    translatedCount++;
                    translation = patternResult;
                }
            }

            // If we found a translation in cache, return it synchronously
            // This prevents the game from reading back translated text and appending to it
            if (translation != null)
            {
                // Store original text for this component (enables runtime toggle restoration)
                if (component != null)
                {
                    TranslatorScanner.StoreOriginalText(component, text);
                    int trackId = (component is Component tc) ? TypeHelper.GetInstanceID(tc) : -1;
                    TranslatorPatches.TrackTranslation(trackId, text, translation);
                }
                return translation;
            }

            // If found in cache with key == value, no translation needed, don't queue
            if (foundInCache)
            {
                return text;
            }

            // No cache hit - queue for AI if enabled (or for capture-only mode)
            if ((Config.IsTranslationEnabled || Config.capture_keys_only) && !string.IsNullOrEmpty(text))
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized + trimmed)
                // TrimEnd because TMP often strips trailing whitespace/newlines when displaying
                string trimmedNormalized = normalizedText.TrimEnd();
                if (IsAlreadyTargetText(text, trimmedNormalized))
                {
                    skippedAlreadyTranslated++;
                    // This component displays an ALREADY-translated string (e.g. a title's shadow/
                    // duplicate layer copied from the main layer) and so never had its source stored —
                    // without it, disabling translation can't revert it (issue #21). Back-fill the
                    // original from the reverse cache so restore works. Guard on "no original yet" to
                    // keep the O(cache) scan to once per such component; StoreOriginalText also no-ops
                    // if an original is already tracked.
                    if (component != null && TranslatorScanner.GetOriginalText(component) == null)
                    {
                        string src = GetSourceForTranslation(text);
                        if (!string.IsNullOrEmpty(src) && src != text)
                            TranslatorScanner.StoreOriginalText(component, src);
                    }
                    return text;
                }

                // Text may be a translation from the pre-reload cache still displayed
                // (component missed by RestoreAllOriginals) — refresh it, never queue it
                string staleRefreshed = TryResolveStaleTranslation(text, trimmedNormalized, component);
                if (staleRefreshed != null)
                    return staleRefreshed;

                // Own UI text that's already translated (displayed result) — don't re-queue
                // The mod UI shows translated text; re-queueing it creates an infinite loop
                if (isOwnUI)
                {
                    return text;
                }

                // Skip invisible components ONLY if they're also in typewriting state
                // (likely an accumulator: hidden component with growing text).
                // Inactive components with STABLE text (tab panels, menus) are allowed
                // through so they get translated before the user opens them.
                if (component is Component visComp)
                {
                    try
                    {
                        if (visComp.gameObject != null && !visComp.gameObject.activeInHierarchy)
                        {
                            int visCompId = TypeHelper.GetInstanceID(visComp);
                            if (TranslatorPatches.IsInTypewritingState(visCompId))
                            {
                                return text;
                            }
                        }
                    }
                    catch { }
                }

                // Typewriting detection: skip queuing if text is growing char by char
                // on the same component. Only for cache MISSES — cache hits are returned above.
                // This prevents partial typewriting text from being sent to AI.
                // Skip for concat deltas (they should be queued immediately, not deferred).
                int compId = (component is Component comp2) ? TypeHelper.GetInstanceID(comp2) : -1;
                if (!skipTypewriting && TranslatorPatches.IsTypewritingInProgress(compId, text))
                {
                    return text;
                }

                // DEBUG LOG: if text contains Latin chars and wasn't caught by reverse cache
                // Log AFTER all skip checks so we only see texts actually queued
                if (_dbgReverseMiss < 20 && text.Length > 5)
                {
                    bool hasLatin = false;
                    foreach (char c in text)
                    {
                        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        { hasLatin = true; break; }
                    }
                    if (hasLatin)
                    {
                        _dbgReverseMiss++;
                        LogDebug($"[REVERSE-MISS] orig({text.Length}c)='{text}'\n  norm({trimmedNormalized.Length}c)='{trimmedNormalized}'");
                    }
                }

                if (!skipQueueing)
                {
                    // Same stale-variables guard as TranslateSingleText: refresh
                    // and retry once before minting a new queue entry (throttled
                    // once per frame — the recursion cannot loop)
                    if (VariableManager.RefreshOnMiss())
                        return TranslateSingleTextWithTracking(text, component, isOwnUI, skipTypewriting, skipQueueing);

                    QueueForTranslation(text, component, isOwnUI);
                }
                // else: concat component — deltas are queued individually, skip full text queue
            }

            return text;
        }

        public static string TryPatternMatch(string text)
        {
            // Quick skip if we already know this text doesn't match any pattern
            if (patternMatchFailures.Contains(text))
                return null;

            foreach (var entry in PatternEntries)
            {
                try
                {
                    var match = entry.MatchRegex.Match(text);
                    if (match.Success)
                    {
                        var capturedValues = new List<string>();
                        for (int i = 1; i < match.Groups.Count; i++)
                        {
                            capturedValues.Add(match.Groups[i].Value);
                        }

                        string result = entry.TranslatedPattern;
                        for (int i = 0; i < entry.PlaceholderIndices.Count && i < capturedValues.Count; i++)
                        {
                            int placeholderIndex = entry.PlaceholderIndices[i];
                            result = result.Replace($"{PlaceholderPrefix}{placeholderIndex}{PlaceholderSuffix}", capturedValues[i]);
                        }

                        return result;
                    }
                }
                catch { }
            }

            // Cache this failure to avoid re-checking all patterns next time
            patternMatchFailures.Add(text);
            return null;
        }

        public static bool IsNumericOrSymbol(string text)
        {
            foreach (char c in text.Trim())
            {
                // char.IsLetter may fail for CJK characters on some IL2CPP runtimes.
                // Explicitly check Unicode ranges for letters and CJK ideographs.
                if (char.IsLetter(c))
                    return false;
                if (c >= 0x2E80 && c <= 0x9FFF)  // CJK radicals, kangxi, ideographs
                    return false;
                if (c >= 0xAC00 && c <= 0xD7AF)  // Korean Hangul syllables
                    return false;
                if (c >= 0x3040 && c <= 0x30FF)  // Japanese Hiragana + Katakana
                    return false;
                if (c >= 0x0400 && c <= 0x04FF)  // Cyrillic
                    return false;
                if (c >= 0x0600 && c <= 0x06FF)  // Arabic
                    return false;
                if (c >= 0x0900 && c <= 0x097F)  // Devanagari (Hindi)
                    return false;
                if (c >= 0x0E00 && c <= 0x0E7F)  // Thai
                    return false;
            }
            return true;
        }

        public static void ClearLastSeenText()
        {
            lastSeenText.Clear();
        }

        /// <summary>
        /// Clear all processing state caches to force re-evaluation of text.
        /// Call this when settings change (enable_translations, enable_ai, etc.)
        /// Does NOT clear the translation cache itself.
        /// </summary>
        public static void ClearProcessingCaches()
        {
            // Clear text tracking
            lastSeenText.Clear();

            // Clear Harmony patch cache
            TranslatorPatches.ClearCache();

            // Clear scanner processed cache
            TranslatorScanner.ClearProcessedCache();

            // Clear pattern match failure cache (in case patterns changed)
            patternMatchFailures.Clear();

            // Give validation-failed texts another chance (model/language may have changed)
            validationFailedTexts.Clear();

            // Clear user exclusion cache (instance IDs change between scenes)
            ClearUserExclusionCache();

            LogDebug("[TranslatorCore] Processing caches cleared - text will be re-evaluated");
        }

        public static bool HasSeenText(int id, string text, out string lastText)
        {
            return lastSeenText.TryGetValue(id, out lastText) && lastText == text;
        }

        public static void UpdateSeenText(int id, string text)
        {
            lastSeenText[id] = text;
        }

        public static void ClearSeenText(int id)
        {
            lastSeenText.Remove(id);
        }

        public static void SaveCache()
        {
            lock (lockObj)
            {
                try
                {
                    // Counted here rather than trusted, so what the file says is true of the file.
                    //
                    // ⚠ _local_changes was going stale on disk, and it took an outside reader to
                    // notice: in-game everything looked right because every panel reads the counter
                    // in memory, while the file still claimed changes that had been published. The
                    // cause was ordering — a save ran, and only afterwards did the ancestor move and
                    // the count drop to zero, with nothing writing the file again.
                    //
                    // Recounting at the moment of writing removes the whole class of mistake: no
                    // caller can leave the number behind, because the number is not carried here. It
                    // also corrects a second, quieter error — the running counter is incremented on
                    // every edit, so editing one line ten times counted ten changes.
                    //
                    // Cost: two walks of the dictionaries, against serialising the whole file, at
                    // most once every thirty seconds.
                    RecalculateLocalChanges();

                    // Create output with metadata first, then sorted translations
                    var output = new JObject();

                    // Metadata
                    output["_engine_version"] = CurrentEngineVersion;
                    output["_uuid"] = FileUuid;

                    if (CurrentGame != null)
                    {
                        output["_game"] = new JObject
                        {
                            ["name"] = CurrentGame.name,
                            ["steam_id"] = CurrentGame.steam_id
                        };
                    }

                    // Save _source with hash for multi-device sync detection, plus the
                    // Main's hash at the last merge from it (branches only)
                    if (!string.IsNullOrEmpty(LastSyncedHash)
                        || !string.IsNullOrEmpty(LastMergedMainHash)
                        || SourceSiteId.HasValue)
                    {
                        var source = new JObject();
                        if (!string.IsNullOrEmpty(LastSyncedHash))
                        {
                            source["hash"] = LastSyncedHash;
                        }
                        if (!string.IsNullOrEmpty(LastMergedMainHash))
                        {
                            source["main_hash"] = LastMergedMainHash;
                        }
                        if (SourceSiteId.HasValue)
                        {
                            source["site_id"] = SourceSiteId.Value;
                        }
                        output["_source"] = source;
                    }

                    // Provenance of a fork. Separate from _source, which an older version reads
                    // and rewrites: this block is unknown to it, so it would be dropped on its
                    // next save — a loss of credit, never a breakage. It stays out of the content
                    // hash (which covers translations plus _uuid), so it can never make two
                    // installs disagree about whether they hold the same file.
                    if (ForkedFromSiteId.HasValue)
                    {
                        var origin = new JObject();
                        origin["site_id"] = ForkedFromSiteId.Value;
                        if (!string.IsNullOrEmpty(ForkedFromHash))
                        {
                            origin["hash"] = ForkedFromHash;
                        }
                        if (ForkedFromResolvedLines.HasValue)
                        {
                            origin["resolved_lines"] = ForkedFromResolvedLines.Value;
                        }
                        if (!string.IsNullOrEmpty(ForkedFromContentHash))
                        {
                            origin["content_hash"] = ForkedFromContentHash;
                        }
                        output["_forked_from"] = origin;
                    }

                    if (LocalChangesCount > 0)
                    {
                        output["_local_changes"] = LocalChangesCount;
                    }

                    if (MetadataDirty)
                    {
                        output["_metadata_dirty"] = true;
                    }

                    // Settings sections, built by the same code that reads and
                    // replaces them (see the "Settings sections" region). An
                    // empty section is omitted: its absence means "nothing set".
                    foreach (var section in SettingsSection.All)
                    {
                        var token = BuildSettingsSection(section);
                        if (token != null)
                        {
                            output[SettingsSection.JsonKey(section)] = token;
                        }
                    }

                    // Sorted translations with new format {"v": "value", "t": "tag", "i": index}
                    var sortedKeys = TranslationCache.Keys.OrderBy(k => k).ToList();
                    foreach (var key in sortedKeys)
                    {
                        var entry = TranslationCache[key];
                        var obj = new JObject
                        {
                            ["v"] = entry.Value,
                            ["t"] = entry.Tag ?? "A"
                        };
                        // Capture-order index — omitted when absent (never "i": null,
                        // the website validation would reject it)
                        if (entry.Index.HasValue)
                        {
                            obj["i"] = entry.Index.Value;
                        }
                        output[key] = obj;
                    }

                    string json = output.ToString(Formatting.Indented);
                    File.WriteAllText(CachePath, json);
                    cacheModified = false;

                    if (DebugMode)
                        Adapter?.LogInfo($"Saved {sortedKeys.Count} cache entries with UUID: {FileUuid}");
                }
                catch (Exception e)
                {
                    Adapter?.LogError($"Failed to save cache: {e.Message}");
                }
            }

            // Live edit session: push the change to the browser editor
            // (debounced + hash-checked by the UI manager, no-op otherwise)
            UI.TranslatorUIManager.NotifyLocalFileChanged();
        }

        /// <summary>
        /// Creates a new fork by generating a new UUID.
        /// This effectively starts a new lineage separate from any existing server translation.
        /// The current translations are preserved but will be treated as a new upload.
        /// Call with languages from ServerState before it's reset (from downloaded translation).
        /// </summary>
        /// <param name="sourceLanguage">Source language of the forked translation</param>
        /// <param name="targetLanguage">Target language of the forked translation</param>
        public static void CreateFork(string sourceLanguage = null, string targetLanguage = null)
        {
            string oldUuid = FileUuid;

            // Store fork context with languages/game BEFORE resetting ServerState
            // This allows UploadPanel to skip UploadSetupPanel since we already know the context
            PendingFork = new ForkContext
            {
                SourceLanguage = sourceLanguage ?? ServerState?.SourceLanguage,
                TargetLanguage = targetLanguage ?? ServerState?.TargetLanguage,
                Game = CurrentGame
            };

            LogDebug($"[Fork] Context saved: {PendingFork.SourceLanguage} -> {PendingFork.TargetLanguage}, game={PendingFork.Game?.name}");

            // Written down BEFORE the reset below wipes the sync state. Detaching the sync is
            // required; erasing where the work came from was a side effect of doing both with the
            // same variables. The count is what we actually received — measured now, because the
            // original goes on growing and the question has no answer afterwards.
            ForkedFromSiteId = SourceSiteId;
            ForkedFromHash = LastSyncedHash;
            ForkedFromResolvedLines = CountResolvedEntries();
            // ⚠ Taken BEFORE the new uuid is generated is not why it works — the fingerprint
            // ignores the uuid, which is the whole point. It is taken here because this is the last
            // instant the cache holds exactly what was copied.
            ForkedFromContentHash = ComputeContentFingerprint();

            // Generate new UUID for the fork
            FileUuid = Guid.NewGuid().ToString();

            // Reset server state - we're starting fresh
            ServerState = new ServerTranslationState();

            // Reset sync tracking - local changes will be counted from this point
            LastSyncedHash = null;
            // A fork is detached: it has no upstream Main any more, so the memory of
            // one would make the mod offer to merge from a lineage it just left
            LastMergedMainHash = null;
            SourceSiteId = null;
            LocalChangesCount = TranslationCache.Count; // All entries are now "local changes"

            // Clear ancestor cache - no longer relevant for the new lineage
            ClearAncestorCache();

            // Save with new UUID
            SaveCache();

            Adapter?.LogInfo($"Created fork: old UUID {oldUuid} -> new UUID {FileUuid}");
        }

        /// <summary>
        /// Entries that hold a settled translation, the same way the website counts them: an
        /// empty capture is work identified, not work done, and mod-UI entries are never counted.
        /// </summary>
        private static int CountResolvedEntries()
        {
            int resolved = 0;
            foreach (var kvp in TranslationCache)
            {
                var entry = kvp.Value;
                if (entry == null || entry.Tag == "M") continue;
                if (entry.IsHumanEmpty || string.IsNullOrEmpty(entry.Value)) continue;
                resolved++;
            }
            return resolved;
        }

        /// <summary>
        /// Clears the ancestor cache file.
        /// </summary>
        private static void ClearAncestorCache()
        {
            try
            {
                string ancestorPath = CachePath.Replace(".json", ".ancestor.json");
                if (File.Exists(ancestorPath))
                {
                    File.Delete(ancestorPath);
                    AncestorCache.Clear();
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to clear ancestor cache: {e.Message}");
            }
        }
    }

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
        public string game_context { get; set; } = "";
        public int timeout_ms { get; set; } = 30000;

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

        /// <summary>Attempts as an actually usable number, whatever the file says.</summary>
        [JsonIgnore]
        public int AttemptsAllowed
        {
            get
            {
                if (ai_max_attempts < 1) return 1;
                if (ai_max_attempts > 10) return 10;
                return ai_max_attempts;
            }
        }

        /// <summary>Temperatures clamped to what an OpenAI-compatible server accepts.</summary>
        [JsonIgnore]
        public double TemperatureNormal => ClampTemperature(ai_temperature);
        [JsonIgnore]
        public double TemperatureRepair => ClampTemperature(ai_temperature_repair);
        [JsonIgnore]
        public double TemperatureRetranslate => ClampTemperature(ai_temperature_retranslate);

        private static double ClampTemperature(double value)
        {
            if (double.IsNaN(value) || value < 0.0) return 0.0;
            return value > 2.0 ? 2.0 : value;
        }

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
        public bool IsTranslationEnabled => enable_ai && translation_backend != "none";

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

                config_version = CurrentConfigVersion;
                _configMigrated = true;
            }
        }

        /// <summary>Config schema version, bumped when a one-shot migration is added above.</summary>
        private const int CurrentConfigVersion = 2;

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

        public string GetTargetLanguage()
        {
            if (string.IsNullOrEmpty(target_language) || target_language.ToLower() == "auto")
            {
                return LanguageHelper.GetSystemLanguageName();
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

    /// <summary>
    /// Server state for current translation (from check-uuid, not persisted to disk)
    /// </summary>
    public class ServerTranslationState
    {
        /// <summary>True if we've checked with the server (even if translation doesn't exist)</summary>
        public bool Checked { get; set; } = false;
        /// <summary>True if translation exists on server</summary>
        public bool Exists { get; set; } = false;
        /// <summary>True if current user owns the translation</summary>
        public bool IsOwner { get; set; } = false;
        /// <summary>Translation ID on server</summary>
        public int? SiteId { get; set; }
        /// <summary>Username of uploader</summary>
        public string Uploader { get; set; }
        /// <summary>File hash on server</summary>
        public string Hash { get; set; }
        /// <summary>Translation type (ai, human, etc.)</summary>
        public string Type { get; set; }
        /// <summary>Translation notes</summary>
        public string Notes { get; set; }
        /// <summary>URL to external resources (fonts, images)</summary>
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// "in_progress" or "complete", as published. Null when unknown — an older server, or a
        /// lineage we do not own — and a caller must then leave it alone rather than pick one.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions — the Main's own decision.
        ///
        /// Null when unknown: an older server, or a lineage nobody has asked about yet. Unknown
        /// is NOT "solo work", and every reader must leave it alone rather than pick one.
        /// </summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// This branch's Main has closed to contributions since: it can no longer be sent, nor
        /// have its details changed.
        ///
        /// 🔴 Nothing inside the game changes when it happens — the file opens, translates and
        /// saves exactly as before — so unless a screen says it, the discovery happens at the
        /// moment of publishing, after the work. Null is unknown, never "all is well".
        /// </summary>
        public bool? BranchFrozen { get; set; }

        /// <summary>User's role for this translation</summary>
        public TranslationRole Role { get; set; } = TranslationRole.None;

        /// <summary>If Branch, the username of the Main owner</summary>
        public string MainUsername { get; set; }

        /// <summary>
        /// Branch whose Main no longer exists — deleted, or its account closed.
        ///
        /// Nobody can ever merge this work: a branch needs a head to be merged into. The way
        /// forward is to publish it as a translation of its own, which the Fork action does.
        /// Null on servers too old to report it, and that absence must read as "unknown" rather
        /// than "the Main is fine".
        /// </summary>
        public bool? MainMissing { get; set; }

        /// <summary>
        /// The Main is still published and the account that owned it has been erased.
        ///
        /// Ends the same way as MainMissing — a branch needs somebody to be merged by, and there is
        /// nobody — but it is the harder of the two to notice: the Main is still listed, still
        /// downloadable, and still says it accepts contributions. Nothing ever fails; the work
        /// simply waits for a reader who does not exist.
        ///
        /// ⚠ Kept apart from MainMissing rather than folded into it, because what somebody has to
        /// understand is not the same: here the translation is still there and still safe to use.
        /// Null on servers too old to report it.
        /// </summary>
        public bool? MainAbandoned { get; set; }

        /// <summary>
        /// The Main was told about this branch, has edited their own file since, and has taken
        /// nothing in. Not the same as silence — that is dormancy — and said once only.
        /// Null on servers too old to report it.
        /// </summary>
        public bool? MainIgnoring { get; set; }

        /// <summary>Lines of this branch the Main has taken in, added up over every merge.</summary>
        public int MergedLinesTotal { get; set; }

        /// <summary>If Main, the number of branches</summary>
        public int BranchesCount { get; set; }

        /// <summary>
        /// Of those branches, how many are actually waiting on their Main: not been through in
        /// their current state, AND holding something a merge would offer.
        ///
        /// 🔴 **This is what a screen shows, not <see cref="BranchesCount"/>.** That one answers
        /// "how many people contribute" — true, and not the question somebody asks when deciding
        /// whether to open the merge screen. Counting a contributor who took the file months ago
        /// and never came back sends their Main to review emptiness.
        ///
        /// ⚠ Null on a server too old to say. Unknown is not zero: a screen falls back to the raw
        /// count rather than announcing that nothing is waiting.
        /// </summary>
        public int? BranchesWithWork { get; set; }

        /// <summary>How many lines those contributions hold, counted once each. Null if unknown.</summary>
        public int? LinesAvailable { get; set; }

        /// <summary>
        /// How many rows need a DECISION — lines the Main does not hold, plus lines both sides hold
        /// differently, the ones it will keep its own on included.
        ///
        /// 🔴 **Not <see cref="LinesAvailable"/>, and neither follows from the other.** That one is
        /// what would be taken; this is what has to be looked at. Measured on a real lineage: 56 and
        /// 38, the 18 in between being two machine translations that differ. One answers "how long
        /// will this take", the other "is there anything here for me".
        ///
        /// ⚠ Null on a server too old to say. Unknown is not zero.
        /// </summary>
        public int? LinesToReview { get; set; }

        /// <summary>
        /// Of those rows, the ones the Main does not hold at all, by the contribution's tag —
        /// because 21 new lines written by hand is not the proposition 21 machine lines are.
        /// </summary>
        public TagTally LinesNew { get; set; }

        /// <summary>Of those rows, the ones both sides hold differently, by the contribution's tag.</summary>
        public TagTally LinesDiffering { get; set; }

        /// <summary>
        /// On a branch: how many lines THIS contribution is still holding for its Main — what was
        /// sent and not taken in. Its author's own business, and nobody else's.
        /// </summary>
        public int? LinesOffered { get; set; }

        /// <summary>
        /// Votes on the PUBLISHED translation of this lineage — count, this player's own vote,
        /// and whether they may vote at all. The server decides that last one: no self-votes,
        /// public only. Null when nothing is published, and on any server too old to report it —
        /// absence reads as "unknown", never as "no votes".
        /// </summary>
        public VoteState Vote { get; set; }

        /// <summary>
        /// If Main, how many branches have never been reviewed or changed since.
        /// The plain count above cannot answer that: it does not move when a
        /// contributor pushes more work to a branch already counted.
        /// </summary>
        public int BranchesPendingReview { get; set; }

        /// <summary>
        /// If Branch, the Main this translation derives from — id and hash, so the
        /// mod can tell that upstream moved without downloading anything.
        /// Null for a Main, for a detached fork, and for any server that does not
        /// report it yet (older site: absence must read as "unknown", not "gone").
        /// </summary>
        public int? MainSiteId { get; set; }
        public string MainHash { get; set; }
        public int MainLineCount { get; set; }

        /// <summary>Source language of the translation (original game language)</summary>
        public string SourceLanguage { get; set; }

        /// <summary>Target language of the translation (translated to)</summary>
        public string TargetLanguage { get; set; }
    }

    /// <summary>
    /// Context for a fork operation. Set before CreateFork() to preserve source translation info.
    /// Cleared after successful upload.
    /// </summary>
    public class ForkContext
    {
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public GameInfo Game { get; set; }
    }

    /// <summary>
    /// User role relative to a translation on the server.
    /// Determined by comparing UUID and user identity.
    /// </summary>
    public enum TranslationRole
    {
        /// <summary>Not yet uploaded / UUID unknown on server</summary>
        None,
        /// <summary>Owner of this translation (same UUID + same user)</summary>
        Main,
        /// <summary>Holding someone else's lineage: same UUID, different user. NOT a Branch — one becomes a Branch by uploading.</summary>
        Branch
    }

    /// <summary>
    /// Type of text being translated, used to optimize prompts.
    /// </summary>


    /// <summary>
    /// A translation entry with value and tag.
    /// JSON format: {"v": "value", "t": "A/H/V", "i": 123}
    /// </summary>
    public class TranslationEntry
    {
        /// <summary>The translated value</summary>
        public string Value { get; set; } = "";

        /// <summary>
        /// Tag indicating the source of this translation.
        /// A = AI generated, H = Human, V = AI Validated by human,
        /// S = Skipped (wrong source language), M = Mod UI.
        /// Null defaults to A.
        /// </summary>
        public string Tag { get; set; } = "A";

        /// <summary>
        /// Capture-order index "i": monotonic number assigned when the text is
        /// first captured, used by the web editors to sort entries in the order
        /// they appeared in-game. Presentation metadata ONLY — excluded from the
        /// content hash (mod and website), ignored by merge comparisons, and
        /// absent on entries written by older mod versions.
        /// </summary>
        public long? Index { get; set; }

        /// <summary>True if this is a Skipped or Mod UI entry (immutable tags)</summary>
        public bool IsImmutableTag => Tag == "S" || Tag == "M";

        /// <summary>True if Value is null or empty</summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        /// <summary>True if this is a Human-tagged empty entry (capture-only placeholder)</summary>
        public bool IsHumanEmpty => Tag == "H" && IsEmpty;

        /// <summary>
        /// Get the priority of this entry for merge conflict resolution.
        /// Higher priority wins: H empty (0) < A (1) < V (2) < H with value (3) < S/M (99)
        /// S and M are immutable and should never be replaced.
        /// </summary>
        /// <summary>
        /// ⚠ The ladder itself lives in <see cref="UnityGameTranslator.Common.Merge.PriorityOf"/>.
        /// It decides who wins a merge with nobody asked, and the manager settles the same lines
        /// from outside a running game — two tables would be two answers about one file.
        /// </summary>
        public int Priority => Common.Merge.PriorityOf(Tag, Value);

        /// <summary>
        /// Create a new TranslationEntry from a string value (defaults to AI tag).
        /// </summary>
        public static TranslationEntry FromValue(string value, string tag = "A")
        {
            return new TranslationEntry { Value = value ?? "", Tag = tag ?? "A" };
        }

        /// <summary>
        /// Check if this entry can replace another entry based on tag hierarchy.
        /// S and M tags are immutable and cannot be replaced.
        /// </summary>
        public bool CanReplace(TranslationEntry other)
        {
            if (other == null) return true;
            // Cannot replace immutable tags (S/M) regardless of priority
            if (other.IsImmutableTag) return false;
            return Priority > other.Priority;
        }

        public override string ToString() => $"{Value} [{Tag}]";
    }

    /// <summary>
    /// Game identification info
    /// </summary>
    public class GameInfo
    {
        public string steam_id { get; set; }
        public string name { get; set; }
        public string folder_name { get; set; }

        /// <summary>
        /// What the game calls itself — Unity's `Application.productName` — or null when it does
        /// not say. ⚠ Deliberately NOT the same as <see cref="name"/>, which falls back to the
        /// folder: `HyperEchelon6vYY3`, `Forsaken.Frontiers.v1510`. The two are only told apart
        /// here, so anything that needs a name a human would recognise reads this one.
        /// </summary>
        public string product_name { get; set; }
        /// <summary>
        /// How the steam_id was detected: "steam_appid.txt", "appmanifest", or null if not detected
        /// </summary>
        public string detection_method { get; set; }
    }

    /// <summary>
    /// Per-font settings for translation control and fallback fonts.
    /// Stored in translations.json as _font_overrides.
    /// Rules are evaluated in order — first match wins.
    /// </summary>
    public class FontOverrideRule
    {
        /// <summary>
        /// Pattern to match. Prefixes: "path:" (hierarchy glob), "font:" (font name), "text:" (content, regex if /.../).
        /// Without prefix: tries path first, then text substring.
        /// </summary>
        public string match { get; set; }

        /// <summary>
        /// Replacement font name. Null = keep current font (only override size).
        /// </summary>
        public string replacement { get; set; }

        /// <summary>
        /// Size multiplier override. 0 = don't override (use global setting).
        /// Example: 1.0 = original size, 1.5 = 150%, 0.7 = 70%.
        /// </summary>
        public float size_multiplier { get; set; } = 0f;

        /// <summary>
        /// Whether this rule is active.
        /// </summary>
        public bool enabled { get; set; } = true;

        /// <summary>
        /// User comment for identifying the rule purpose.
        /// </summary>
        public string comment { get; set; }

        /// <summary>
        /// RTL alignment behaviour for the matched components: null = inherit the font's
        /// setting, "mirror" or "keep". Exists because one game mixes both needs (a description
        /// pane that mirrors fine next to buttons whose boxes were built for one side —
        /// user-arbitrated on the bench).
        /// </summary>
        public string rtl_alignment { get; set; }
    }

    /// <summary>
    /// Per-font settings for translation.
    /// Stored in translations.json as _fonts for sharing with translations.
    /// </summary>
    public class FontSettings
    {
        /// <summary>
        /// Whether to translate text using this font.
        /// Set to false for bitmap fonts that can't display non-Latin characters.
        /// </summary>
        public bool enabled { get; set; } = true;

        /// <summary>
        /// System font name to use as fallback for missing glyphs.
        /// Only applies to TMP fonts that support fallback.
        /// </summary>
        public string fallback { get; set; }

        /// <summary>
        /// How right-to-left text aligns on components using this font: null or "mirror"
        /// (default — left becomes right and vice versa, what an RTL reader expects) or "keep"
        /// (the game's own alignment, for layouts built around one side). Per font and SHARED
        /// with the translation, like every setting in this class: one player fixing a game's
        /// RTL rendering fixes it for everyone who downloads the translation. Refinable per
        /// component through a font override rule.
        /// </summary>
        public string rtl_alignment { get; set; }

        /// <summary>
        /// Font type detected: "TMP", "Unity", "TextMesh", "tk2d"
        /// </summary>
        public string type { get; set; }

        /// <summary>
        /// EFFECTIVE font size multiplier applied to translated text = the materialized product
        /// <c>(scale_auto ? design-scale : 1) × size_percent</c>. Kept materialized so the render
        /// pipeline reads one value AND an older mod (no scale_auto/size_percent support) still
        /// renders the right size. 1.0 = original size.
        /// </summary>
        public float scale { get; set; } = 1.0f;

        /// <summary>
        /// The translator's DELIBERATE size multiplier (the Fonts-tab slider), orthogonal to the
        /// auto design-scale. 1.0 = 100% (native). Kept for cross-script fit/readability (e.g. a
        /// CJK→Latin translation is much longer and may need down/up-sizing vs the HUD). Combines
        /// multiplicatively with the design-scale baseline. Absent in old JSON → migrated from the
        /// legacy <see cref="scale"/> at load (old translations stored the deliberate % there).
        /// </summary>
        public float size_percent { get; set; } = 1.0f;

        /// <summary>
        /// When true, the design-scale (replaced font's faceInfo.scale — matches the game font's
        /// native visual size) is folded into the effective <see cref="scale"/> as a baseline, on
        /// top of which <see cref="size_percent"/> still applies. Set on a freshly DETECTED
        /// TMP-family font (EnsureFontSettings); toggled by the user in the Fonts tab. Default
        /// false so entries loaded from an existing translation (field absent in old JSON) keep
        /// their stored scale — frozen translations are never re-scaled. See
        /// analyse/font-rendering-target-size.md (Phase B).
        /// </summary>
        public bool scale_auto { get; set; } = false;

        /// <summary>
        /// Number of times this font has been used for translation.
        /// Used to sort fonts by usage in the UI.
        /// </summary>
        public int usageCount { get; set; } = 0;

        /// <summary>
        /// Origin of this font: "game", "system", "custom", or null for legacy entries.
        /// Used to distinguish fonts with the same name from different sources.
        /// Null/missing in JSON is treated as legacy (unknown origin).
        /// </summary>
        public string origin { get; set; }
    }
}
