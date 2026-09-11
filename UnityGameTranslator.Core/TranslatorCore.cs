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
// The pure text rules moved to Engine/TextNormalization.cs on 2026-09-08 — placeholders,
// markup, line endings, and the two questions about letters. Imported unqualified so the
// cut stayed a MOVE: not one call site here changed, which is what makes it reviewable.
using static UnityGameTranslator.Core.TextNormalization;

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

        /// <summary>
        /// A thread the mod started is about to run its own loop. Do whatever this runtime needs
        /// before it touches anything.
        ///
        /// 🔴 **On IL2CPP this is not housekeeping, it is the difference between running and a
        /// native abort.** The Boehm collector IL2CPP uses knows only the threads the engine made;
        /// collecting while one of ours holds a reference kills the process with "fatal error in
        /// GC: Collecting from unknown thread" — no exception to catch, no line in the log, and it
        /// happens whenever the collector happens to run.
        ///
        /// 🔴 **Here rather than in the Core, because only the adapter KNOWS.** The Core is
        /// compiled once for both runtimes and cannot reference Il2CppInterop at all, so it did
        /// this by walking every loaded assembly for a type name and reflecting two methods out of
        /// it — a guess that compiles on a runtime where it means nothing, and fails silently on
        /// the one where it matters if either name ever moves. Each IL2CPP adapter already
        /// references that assembly and calls the two functions directly, checked by its compiler.
        ///
        /// ⚠ Mono adapters do nothing here, and that is the whole answer for them: .NET threads
        /// are what their collector already tracks.
        /// </summary>
        void OnWorkerThreadStarted();
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

        /// <summary>
        /// The mod's OWN interface, in its own file. Never the game's text, never uploaded, never
        /// downloaded — see <see cref="Common.ModUi"/> for what that separation buys.
        ///
        /// 🔴 **The two dictionaries never meet.** Which one a text belongs to is decided from the
        /// COMPONENT at the moment it is queued (see <see cref="QueuedText"/>), carried on the
        /// queued item, and turned into a tag by the worker; <see cref="AddToCache"/> then files it
        /// by that tag. So "is this ours" is asked once, where it can be answered, and never
        /// guessed from a string.
        /// </summary>
        public static Dictionary<string, TranslationEntry> ModUiCache { get; private set; } = new Dictionary<string, TranslationEntry>();

        public static List<PatternEntry> PatternEntries { get; private set; } = new List<PatternEntry>();
        public static string CachePath { get; private set; }

        /// <summary>Beside <see cref="CachePath"/>, in the same folder, for the same game.</summary>
        public static string ModUiCachePath { get; private set; }

        public static string ConfigPath { get; private set; }
        public static string ModFolder { get; private set; }
        public static bool DebugMode { get; private set; } = false;
        public static string FileUuid { get => Store.Uuid; private set => Store.Uuid = value; }

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

        public static int LocalChangesCount => Store.LocalChanges;

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
        public static Dictionary<string, TranslationEntry> AncestorCache => Store.Ancestor;

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

        private static TranslationStore _store;

        /// <summary>
        /// The file's own facts — identity, sync stamps, ancestors — and the moments they move at
        /// (Engine/TranslationStore.cs, held by TranslationStoreChecks on real files). One per
        /// translation path, made fresh at every load so nothing of the previous file survives.
        /// The statics around it are its façade: same names, same callers.
        /// </summary>
        public static TranslationStore Store => _store ?? (_store = new TranslationStore(CachePath, LogDebug));

        /// <summary>The settings a stored sections object describes, or null when it describes none (no baseline).</summary>
        private static TranslationSettings SettingsFromSections(JObject sections)
        {
            if (sections == null) return null;
            var settings = TranslationSettings.FromFile(sections);
            return settings.HasAny() ? settings : null;
        }

        /// <summary>The sections a TranslationSettings writes, as the store stores them; null stays null (unknown).</summary>
        private static JObject SectionsOf(TranslationSettings settings)
        {
            if (settings == null) return null;
            var sections = new JObject();
            settings.WriteInto(sections);
            return sections;
        }

        /// <summary>
        /// Hash of the translation at last sync (download or upload).
        /// Used to detect if server has changed since our last sync.
        /// Stored in translations.json as _source.hash
        /// </summary>
        public static string LastSyncedHash { get => Store.SourceHash; set => Store.SourceHash = value; }

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
        public static string LastMergedMainHash { get => Store.MainHash; set => Store.MainHash = value; }

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
        public static int? SourceSiteId { get => Store.SiteId; set => Store.SiteId = value; }

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
        public static int? ForkedFromSiteId => Store.ForkedFromSiteId;
        public static string ForkedFromHash => Store.ForkedFromHash;
        public static int? ForkedFromResolvedLines => Store.ForkedFromResolvedLines;

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
        public static string ForkedFromContentHash => Store.ForkedFromContentHash;

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

        /// <summary>
        /// What a settings screen may ask the runtime to take from the game — mirrors
        /// UniverseLib.Input.InputCapture.CaptureKind, so that a panel (which may name nothing of
        /// UniverseLib, see UI/Components/Handles.cs) can ask for one without naming it.
        /// </summary>
        public enum InputIntent { Keyboard, GameMenus, GameClicks, MouseAxes }

        private static UniverseLib.Input.InputCapture.CaptureKind ToCaptureKind(InputIntent intent)
        {
            switch (intent)
            {
                case InputIntent.GameMenus: return UniverseLib.Input.InputCapture.CaptureKind.GameMenus;
                case InputIntent.GameClicks: return UniverseLib.Input.InputCapture.CaptureKind.GameClicks;
                case InputIntent.MouseAxes: return UniverseLib.Input.InputCapture.CaptureKind.MouseAxes;
                default: return UniverseLib.Input.InputCapture.CaptureKind.Keyboard;
            }
        }

        /// <summary>Whether this game can be made to hand over one kind of input — each strategy
        /// probed itself at startup; this only asks, per intention.</summary>
        public static bool CanCaptureInput(InputIntent intent) => UniverseLib.Input.InputCapture.CanCapture(ToCaptureKind(intent));

        /// <summary>Why <see cref="CanCaptureInput"/> answered no, in words a settings screen can show.</summary>
        public static string WhyInputCaptureUnavailable(InputIntent intent) => UniverseLib.Input.InputCapture.WhyNot(ToCaptureKind(intent));

        /// <summary>
        /// Keep UniverseLib's own copy of the EventSystem-override flag in step with
        /// <see cref="DisableEventSystemOverride"/> — its EventSystem patches read it live, so this
        /// is what makes toggling the option in Options take effect at once instead of only at the
        /// next launch. Kept here (never called as UniverseLib.Config… from a panel) so a panel can
        /// hold handles only.
        /// </summary>
        public static void SyncEventSystemOverrideLive()
        {
            UniverseLib.Config.ConfigManager.Disable_EventSystem_Override = DisableEventSystemOverride;
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

        // 🔴 What two pure files cannot answer on their own, handed to them once — and it cannot
        // be missed: reaching Config or a token forces this type's static initialisation, which is
        // this. Both would otherwise have to name UnityEngine or this class, and neither the
        // config.json contract nor the secret handling could be checked outside a game.
        static TranslatorCore()
        {
            ModConfig.SystemLanguage = LanguageHelper.GetSystemLanguageName;
            TokenProtection.Info = LogInfo;
            TokenProtection.Warning = LogWarning;
        }

        // Which languages this translation is in, and what happens when the file, the machine and
        // the server disagree. ⚠ The sequence lives in LanguageState, with its log sinks handed in,
        // so a restored backup or a late server answer can be replayed without a game.
        private static readonly LanguageState _languages = new LanguageState
        {
            Info = message => Adapter?.LogInfo(message),
            Warning = message => Adapter?.LogWarning(message),
        };

        /// <inheritdoc cref="LanguageState.FileSource"/>
        public static string FileSourceLanguage => _languages.FileSource;

        /// <inheritdoc cref="LanguageState.FileTarget"/>
        public static string FileTargetLanguage => _languages.FileTarget;

        /// <summary>
        /// The languages in force for this translation, resolved from the server, then the file,
        /// then the configuration. Null on either side when nobody has settled it yet.
        /// </summary>
        public static string EffectiveSourceLanguage =>
            _languages.EffectiveSource(ServerState?.SourceLanguage, Config?.source_language);

        /// <inheritdoc cref="EffectiveSourceLanguage"/>
        public static string EffectiveTargetLanguage =>
            _languages.EffectiveTarget(ServerState?.TargetLanguage, Config?.target_language);

        /// <inheritdoc cref="LanguageState.Conflict"/>
        public static string LanguageConflict => _languages.Conflict;

        /// <inheritdoc cref="LanguageState.Locked"/>
        public static bool AreLanguagesLocked =>
            LanguageState.Locked(ServerState != null && ServerState.Exists, TranslationCache.Count);

        /// <summary>Which of the two reasons applies, so the panel can say the right one.</summary>
        public static bool LanguagesLockedByPublishing => ServerState != null && ServerState.Exists;

        /// <inheritdoc cref="LanguageState.AlignFromServer"/>
        public static void AlignLanguagesFromServer()
        {
            if (Config == null) return;

            var write = _languages.AlignFromServer(
                ServerState?.SourceLanguage, ServerState?.TargetLanguage,
                ServerState != null && ServerState.Exists,
                Config.source_language, Config.target_language);

            ApplyLanguageWriteBack(write);
        }

        /// <summary>
        /// Take what the decision settled into the configuration and the translation file.
        ///
        /// ⚠ One place, because forgetting either half is silent: an unsaved configuration reverts
        /// at the next launch, and an unflagged file keeps stating what it no longer holds.
        /// </summary>
        private static void ApplyLanguageWriteBack(LanguageWriteBack write)
        {
            if (write.ConfigChanged)
            {
                Config.source_language = write.Source;
                Config.target_language = write.Target;
                SaveConfig();
            }

            if (_languages.FileChanged)
            {
                cacheModified = true;
                _languages.FileWritten();
            }
        }

        /// <inheritdoc cref="LanguageState.SettleFromFile"/>
        private static void SettleLanguagesFromFile()
        {
            if (Config == null) return;

            bool everPublished = SourceSiteId.HasValue || !string.IsNullOrEmpty(LastSyncedHash);
            ApplyLanguageWriteBack(_languages.SettleFromFile(
                Config.source_language, Config.target_language,
                TranslationCache.Count, everPublished));
        }

        /// <summary>
        /// The translation has just been given its first line — settle what it IS, and write it
        /// down.
        ///
        /// 🔴 **Two halves, and only the first existed** (found on a real install, 2026-09-09). A
        /// configuration set to "auto" resolves here, which is
        /// <see cref="LanguageState.SettleTargetOnFirstLine"/>. But a configuration that already
        /// NAMES a language has nothing to resolve, so that call returns at once — and nothing else
        /// ever told the file. <see cref="LanguageState.SettleFromFile"/> is the rule that adopts
        /// the machine's setting into the file, and it refuses while the file has no line, which is
        /// exactly what a new translation is when it is created. It was asked once, at load, and
        /// never again.
        ///
        /// **What that produced**: a fresh translation whose configuration said English → French
        /// carried neither <c>_source_language</c> nor <c>_target_language</c> — for the whole of
        /// its life, since a file states its languages only if it already states them. It worked on
        /// the machine that made it, because the configuration answered in its place, and nowhere
        /// else: restored, downloaded, or opened after the setting moved, it says nothing about
        /// what it is. That is precisely what the identity work of 2026-09-08 exists to prevent.
        ///
        /// ⚠ The order is the fix: resolve first, state second. A configuration on "auto" becomes a
        /// value in the first call, and the second writes THAT value into the file rather than the
        /// mode. Both are idempotent, and the second carries its own guard — a lineage already
        /// published takes its languages from the server, never from this machine.
        /// </summary>
        private static void SettleTargetLanguageOnFirstLine()
        {
            if (Config == null) return;

            ApplyLanguageWriteBack(_languages.SettleTargetOnFirstLine(
                Config.source_language, Config.target_language, Config.GetTargetLanguage()));

            // The file has a line now, so the rule that was waiting for one applies.
            SettleLanguagesFromFile();
        }

        /// <inheritdoc cref="LanguageState.NoteConflict"/>
        private static void NoteLanguageConflict()
        {
            _languages.NoteConflict(ServerState?.SourceLanguage, ServerState?.TargetLanguage);
        }

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

        /// <summary>How many times the cache-hit normalisation dump has been written this session.</summary>
        private static int _dbgCacheHitNormLog = 0;

        private static float lastSaveTime = 0f;
        private static int translatedCount = 0;
        private static int aiTranslationCount = 0;
        private static int cacheHitCount = 0;
        private static Dictionary<int, string> lastSeenText = new Dictionary<int, string>();
        // The texts waiting for a backend. ⚠ It carries its OWN lock — it used to share lockObj
        // with the caches, the order counter and the retranslation requests; no critical section
        // ever spanned both sides, so the split changes no ordering and lets the queue be replayed
        // on its own.
        private static readonly TranslationQueue _queue = new TranslationQueue();

        // ⚠ What lockObj still guards: the translation caches, the capture-order counter and the
        // retranslation requests. The queue is no longer among them.
        private static object lockObj = new object();
        private static bool cacheModified = false;
        // Next capture-order index "i" to assign (monotonic, per lineage).
        // Recomputed as max(i)+1 at every LoadCache — never persisted as metadata.
        // Assign ONLY via NextOrderIndex() (lock-protected).
        private static long nextTranslationIndex = 1;
        private static HttpClient httpClient;
        private static int skippedAlreadyTranslated = 0;
        private static bool _enableTranslationsLogOnce = true; // Log once when translations disabled

        /// <summary>
        /// "Is this text one of OUR translations coming back?" — four indexes (exact and
        /// decoration-insensitive, one pair per side) plus the presented strings, in
        /// Engine/ReadbackIndex.cs, where the sequences are replayed. The statics below are its
        /// façade: same names, same signatures as before the cut, so no caller moved.
        /// </summary>
        private static readonly ReadbackIndex _readback = new ReadbackIndex { Debug = m => LogDebug(m) };
        private static int _shapedQueueRefusals;
        private static int _readbackStoreLogged;

        /// <inheritdoc cref="ReadbackIndex.RegisterPresented"/>
        internal static void RegisterPresentedText(string presented, string logical)
            => _readback.RegisterPresented(presented, logical);

        /// <inheritdoc cref="ReadbackIndex.PresentedLogical"/>
        internal static string TryGetPresentedLogical(string displayed)
            => _readback.PresentedLogical(displayed);

        /// <inheritdoc cref="ReadbackIndex.Index"/>
        private static void IndexTranslatedValue(string key, string value, bool ownUi)
            => _readback.Index(key, value, ownUi, Config != null && Config.normalize_numbers);

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
        /// <param name="ownUi">
        /// Which side is asking. 🔴 **The two never answer for each other**: a mod-interface
        /// translation that marked a GAME text as already translated would take that line out of
        /// what gets published, invisibly — see <see cref="ReadbackIndex"/>. Defaults to the
        /// game, which is what every gate on a game component wants.
        /// </param>
        public static bool IsAlreadyTargetText(string text, string normalizedTrimmed = null, bool ownUi = false)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string probe = normalizedTrimmed ?? NormalizeForCacheLookup(text).TrimEnd();
            return _readback.IsAlreadyTarget(text, probe, ownUi);
        }

        /// <inheritdoc cref="ReadbackIndex.IsReadback"/>
        public static bool IsReadbackOfOwnTranslation(string text, bool ownUi = false)
            => _readback.IsReadback(text, ownUi);

        // Pattern match failure cache (texts that don't match any pattern)
        private static HashSet<string> patternMatchFailures = new HashSet<string>();

        // Texts whose translation failed placeholder validation after all retries.

        // Callback for updating components when translation completes
        public static Action<string, string, List<object>> OnTranslationComplete;

        // Queue status for UI overlay
        private static bool isTranslating = false;
        private static string currentlyTranslating = null;
        public static int QueueCount => _queue.Count;
        public static bool IsTranslating => isTranslating;
        public static string CurrentText => currentlyTranslating;

        /// <summary>
        /// Which attempt this line is on, and out of how many — 0 while it is on its first.
        ///
        /// 🔴 **A counter only once there is something to count.** The first try is not a retry, so
        /// nothing is shown for it; and it is cleared when a line finishes AND when the next one
        /// starts, so a count left over from the previous text can never be read as this one's.
        ///
        /// ⚠ These are the placeholder repairs — the same question asked again because the answer
        /// came back with the game's slots moved or missing. Not the parameter negotiation beside
        /// them, which changes the QUESTION rather than repeating it, and not a rate-limit backoff,
        /// which puts a different line back in the queue.
        /// </summary>
        public static int RetryAttempt => _retryAttempt;
        public static int RetryTotal => _retryTotal;

        private static volatile int _retryAttempt;
        private static volatile int _retryTotal;

        /// <summary>Say which attempt is running. Attempt 0 is the first try and shows nothing.</summary>
        private static void NoteAttempt(int attempt, int total)
        {
            _retryAttempt = attempt == 0 ? 0 : attempt + 1;
            _retryTotal = attempt == 0 ? 0 : total;
        }
        /// <summary>True while the text being translated belongs to the mod's own interface.
        /// The overlay excerpt is meant to show GAME text; showing our own label there reads as
        /// the mod translating itself ("Translating: Translating:").</summary>
        public static bool CurrentTextIsOwnUI => currentTextIsOwnUI;
        private static bool currentTextIsOwnUI = false;

        // Own UI component tracking (mod interface)
        private static HashSet<int> ownUIExcluded = new HashSet<int>();      // Never translate (title, lang codes, config values)
        private static HashSet<int> ownUITranslatable = new HashSet<int>();  // Translate with UI-specific prompt
        private static HashSet<int> ownUIPanelRoots = new HashSet<int>();    // Root GameObjects of our panels (for hierarchy check)

        // User exclusions (chat windows, player names, etc.) - stored in translations.json as _exclusions.
        // ⚠ The patterns AND what has already been decided for each target — see ExclusionRules for
        // why the memory travels with them rather than sitting here beside them.
        private static readonly ExclusionRules userExclusions = new ExclusionRules();

        /// <summary>
        /// Current user exclusion patterns. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<string> UserExclusions => userExclusions.Patterns;

        // Font overrides (per-pattern font/size rules) - stored in translations.json as _font_overrides.
        // ⚠ The rules, what has been decided per target, AND the per-session verdicts on a pattern
        // that will not compile or will not finish — see FontRules for why they travel together.
        // The log is handed in rather than reached for, so a whole sequence replays without a game.
        private static readonly FontRules fontOverrides = new FontRules { Warn = LogWarning };

        /// <summary>
        /// Current font override rules. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<FontOverrideRule> FontOverrides => fontOverrides.Rules;

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
            return fontOverrides.Find(componentId, gameObjectPath, fontName, textContent);
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
            userExclusions.Forget(id);
            fontOverrides.Forget(id);
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
        /// Replace all font override rules at once (called by Apply in UI).
        /// Marks metadata dirty but does NOT save — caller should save after all changes.
        /// </summary>
        public static void SetFontOverrides(List<FontOverrideRule> rules)
        {
            fontOverrides.Load(rules);
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
                case SettingsSections.Fonts:
                    return BuildFontsSection();
                case SettingsSections.FontRules:
                    return BuildFontOverridesSection();
                case SettingsSections.Images:
                    return ImageReplacer.SaveToJson();
                case SettingsSections.Exclusions:
                    return BuildExclusionsSection();
                case SettingsSections.Variables:
                    return VariableManager.SaveToJson();
                case SettingsSections.GameSettings:
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
            if (!fontOverrides.Any) return null;

            var overridesArray = new JArray();
            foreach (var rule in fontOverrides.Rules)
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
                // Written and read like every other field — a rule field that only lives in
                // memory is gone at the next launch (rtl_alignment was, for a day).
                if (!string.IsNullOrEmpty(rule.rtl_alignment))
                    ruleObj["rtl_alignment"] = rule.rtl_alignment;
                overridesArray.Add(ruleObj);
            }

            return overridesArray;
        }

        private static JArray BuildExclusionsSection()
        {
            if (!userExclusions.Any) return null;

            var exclusionsArray = new JArray();
            foreach (var pattern in userExclusions.Patterns)
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
            // ⚠ No ui_font here any more. It described the MOD's interface, and it lived in the
            // GAME's file — published with it, and taken from whoever wrote that file. It is
            // written in modui-translate.json now (see SaveModUiCache), which is the file it
            // belongs to and the one that never leaves this machine.

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
                    comment = ruleObj["comment"]?.Value<string>(),
                    rtl_alignment = ruleObj["rtl_alignment"]?.Value<string>(),
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
            // ⚠ ui_font is deliberately NOT read here: the mod's interface font comes from the
            // interface file or from config.json, never from a game translation. LoadCache lifts
            // one out of an old file exactly once, on its way to the right place.
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
                case SettingsSections.Fonts:
                    ApplyFontsSectionPreservingInventory(token);
                    break;

                case SettingsSections.FontRules:
                    fontOverrides.Load(ParseFontOverridesSection(token));
                    break;

                case SettingsSections.Images:
                    ImageReplacer.LoadFromJson(token);
                    break;

                case SettingsSections.Exclusions:
                    userExclusions.Load(ParseExclusionsSection(token));
                    break;

                case SettingsSections.Variables:
                    VariableManager.LoadFromJson(token);
                    break;

                case SettingsSections.GameSettings:
                    ApplyGameSettingsSection(token);
                    break;
            }
        }

        /// <summary>
        /// Put one settings section into memory AS LOADING A FILE DOES IT — the single door
        /// LoadCache uses, both to empty a section before reading and to fill it from what it read.
        ///
        /// 🔴 **It exists because loading was the one act that had never joined this region.** The
        /// comment at the top says building and applying live here "in ONE place, so that loading a
        /// file, saving it, and replacing a single section can never drift apart" — and loading did
        /// exactly that: six hand-written branches matching six keys, a second copy of a list
        /// nothing compared to the first. Saving already walks
        /// <see cref="SettingsSections.All"/>, so a seventh section would have been written by
        /// every product and read back by nobody, in silence.
        ///
        /// 🔴 **Fonts are the one real exception, and naming it is the point of this method.** At
        /// LOAD the file is the whole truth, discovery inventory included: what a font is called on
        /// this machine rebuilds itself as the game is played. When the PLAYER replaces the section
        /// from a download the answer is the opposite — see
        /// <see cref="ApplyFontsSectionPreservingInventory"/>. Two acts, deliberately, and the
        /// difference now has a name instead of living in two places that happened to differ.
        ///
        /// ⚠ **A null token empties the section**, which is what every one of the six already did:
        /// each parser is `as JObject` / `as JArray` and yields nothing on anything else, and
        /// <c>Load(null)</c> is <c>Load(empty)</c> for both rule sets. That is also why no type
        /// guard is needed here — a malformed section leaves the section empty, and nothing throws.
        /// Throwing would matter: LoadCache's catch empties the translation cache and mints a new
        /// UUID.
        /// </summary>
        private static void ApplySectionAtLoad(string section, JToken token)
        {
            if (section == SettingsSections.Fonts)
            {
                FontSettingsMap.Clear();
                foreach (var kvp in ParseFontsSection(token))
                {
                    FontSettingsMap[kvp.Key] = kvp.Value;
                }
                return;
            }

            ApplySettingsSection(section, token);
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
            if (!InvalidateForSections(sections, out var changed)) return;

            SetMetadataDirty();

            LogInfo($"[Settings] Replaced sections: {string.Join(", ", changed.ToArray())}");
        }

        /// <summary>
        /// Drop what these sections govern, so the game is re-read against what they now say.
        ///
        /// 🔴 **Separate from <see cref="AfterSettingsSectionsChanged"/> because a RELOAD is not an
        /// edit.** Replacing a section from a screen changes what this install decided, and has to
        /// be recorded as unsynced; loading a different translation file changes the same things
        /// and records nothing — the file IS what it says.
        ///
        /// ⚠ **A reload is every section at once, and it did not say so.** ReloadCache loaded a new
        /// fonts section and dropped only the TEXT caches, so components kept the font the previous
        /// translation asked for. Seen on a real install (2026-09-08): restoring a Thai translation
        /// over a French one loaded its `_fonts` — Alatsi→Tahoma against the French Alatsi→Birch Std
        /// — and nothing in the font machinery ran. The game stayed on Birch Std until the fallback
        /// was toggled by hand in the Fonts tab, which goes through the other door.
        /// </summary>
        /// <returns>False when there was nothing to invalidate.</returns>
        private static bool InvalidateForSections(IEnumerable<string> sections, out HashSet<string> changed)
        {
            changed = new HashSet<string>(sections ?? Enumerable.Empty<string>());
            if (changed.Count == 0) return false;

            if (changed.Contains(SettingsSections.Fonts) || changed.Contains(SettingsSections.FontRules))
            {
                // ⚠ Not the rules moving — Load does that itself. This is the FONTS moving, and a
                // decision that matched on a font name is about a font that has just changed.
                fontOverrides.ForgetAll();
                FontManager.ClearComponentScaleOverrides();
                // Font sizes are read from true originals again
                TranslatorPatches.ClearFontSizeCache();
                // Without this, the "already translated" check returns early and
                // never reaches the font scale
                TranslatorPatches.ClearLastTranslatedCache();
            }

            if (changed.Contains(SettingsSections.Images))
            {
                // 🔴 **Loading a replacement is not wearing it**, and the loader says so itself:
                // "we don't call ApplyToScene() here automatically". The pair that does both is
                // what a scene change runs — and a reload is the same event for these components,
                // since the file that named their images has just been replaced.
                //
                // ⚠ The old ones come off FIRST. A translation replacing fewer images than the one
                // before it would otherwise leave components wearing pictures the file now on disk
                // never mentions — the same shape as the font transition above.
                //
                // ⚠ Seen on a real install (2026-09-08): the definitions were read and the sprite
                // imported, and nothing appeared. Only "Loaded N replacement definitions" and
                // "Loaded N replacement sprites" were in the log; no apply pass followed, because
                // none was asked for.
                ImageReplacer.DropReplacements();
                ImageReplacer.LoadAllReplacements();
                ImageReplacer.ApplyToScene();
            }

            if (changed.Contains(SettingsSections.Variables))
            {
                // The definitions have changed, so the values read through the old ones describe
                // fields this file never named. Flagged rather than read now: the instances may
                // not exist yet — the same reason a scene change flags instead of reading.
                VariableManager.MarkNeedsRefresh();
            }

            ClearProcessingCaches();
            return true;
        }

        #endregion

        /// <summary>
        /// Clear the font override cache (call on scene change).
        /// </summary>
        public static void ClearFontOverrideCache()
        {
            fontOverrides.ForgetAll();
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
            if (component == null || !userExclusions.Any) return false;

            long id = component.GetInstanceID();
            if (TryCachedExclusion(id, out bool cached)) return cached;

            return RememberExclusion(id, GetGameObjectPath(component.gameObject));
        }

        /// <summary>True when any patterns exist at all. Ask before building a path.</summary>
        public static bool HasExclusionPatterns => userExclusions.Any;

        /// <summary>The answer already known for this target, if there is one.</summary>
        public static bool TryCachedExclusion(long id, out bool excluded)
        {
            return userExclusions.TryRecall(id, out excluded);
        }

        /// <summary>
        /// Decide, and remember, whether this path is excluded — and say so in the log.
        ///
        /// ⚠ The log line is what stays HERE: the rule itself has no logger, on purpose, so that a
        /// whole sequence of it can be replayed without a game.
        /// </summary>
        public static bool RememberExclusion(long id, string path)
        {
            bool excluded = userExclusions.Decide(id, path);

            if (excluded)
                LogDebug("[Exclusion] Matched: " + (path ?? ""));

            return excluded;
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
            // ⚠ Only when something was actually written: saving on a duplicate would rewrite the
            // translation and mark the metadata dirty for nothing.
            if (userExclusions.Add(pattern))
            {
                SetMetadataDirty();
                SaveCache();
                LogDebug("[Exclusion] Added: " + pattern.Trim());
            }
        }

        /// <summary>
        /// Remove an exclusion pattern. Clears the cache.
        /// </summary>
        public static bool RemoveExclusion(string pattern)
        {
            if (userExclusions.Remove(pattern))
            {
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
            SaveCache();
            LogDebug("[Exclusion] Cleared all exclusions");
        }

        /// <summary>
        /// Clear the exclusion cache (call on scene change).
        /// </summary>
        public static void ClearUserExclusionCache()
        {
            userExclusions.ForgetAll();
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

        // ── Mod-interface translation: what the interface file asks for vs what the user overrides ──

        private static string _fontAvailabilityCheckedFor;
        private static bool _fontAvailabilityResult;

        /// <summary>
        /// Interface font in effect: the local choice wins, otherwise what the interface file asks
        /// for (<see cref="ModUiFont"/>).
        ///
        /// ⚠ It used to fall back on the GAME translation's `_settings.ui_font` — a mod setting
        /// living in the game's file, published with it and taken from whoever wrote it. The font
        /// now travels with the interface itself, which is the thing it renders.
        /// </summary>
        public static string EffectiveInterfaceFont
        {
            get
            {
                string local = Config?.interface_font;
                return !string.IsNullOrEmpty(local) ? local : ModUiFont;
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
        /// The local setting wins when the user expressed one; otherwise the interface file itself
        /// decides — one already sitting there was translated on this machine, so carry on.
        ///
        /// 🔴 **It used to be decided by the GAME's translation**, which held the interface lines:
        /// downloading somebody's translation both turned this on for a person who never asked and
        /// supplied the words of our own buttons. The answer now comes from a file that is never
        /// downloaded, which is what makes the question safe to ask at all.
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
                return local ?? ModUiHasLines;
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

        // Current engine version for migration support
        private const int CurrentEngineVersion = 1;



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
            ModUiCachePath = Path.Combine(ModFolder, ModUi.FileName);
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
                // 🔴 **Off this thread, because Initialize runs on the game's.** PreloadModel
                // blocks on its answer, and loading a model is not quick: measured at 4.1 s on one
                // machine with a small model and a cold server, and a player with a large one
                // waits far longer — a freeze at startup that everybody rightly blames on the mod,
                // because it IS the mod.
                //
                // ⚠ Fire and forget is safe HERE and nowhere by default: PreloadModel catches
                // everything it can throw and logs it, so this task cannot fault and leave an
                // exception nobody looks at (see SseClient.Connect for the case where it did).
                Task.Run(PreloadModel);
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
            // No try/catch: SaveModUiCache logs its own failure rather than throwing.
            SaveModUiCacheIfDirty();

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

            if ((cacheModified || modUiCacheModified) && currentTime - lastSaveTime > 30f)
            {
                lastSaveTime = currentTime;
                if (cacheModified) SaveCache();
                SaveModUiCacheIfDirty();
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
        // How much of what this game shows is nothing but private-use code points. Counted at the
        // single door, reported once, and read by nobody else.
        private static int _textsSeenAtDoor;
        private static int _privateUseOnlyTexts;
        private static bool _privateUseReported;

        /// <summary>
        /// Say once, out loud, when a game's text turns out to be written in a private area.
        ///
        /// 🔴 **Because that game shows nothing translated and nothing says why.** Some fonts map
        /// a whole alphabet into the private-use area — an older CJK encoding, a game doing its own
        /// Arabic or Indic shaping, a subsetted font that renumbered. Every one of those texts
        /// reads as symbols, is refused at this door, and the player is left with an untouched
        /// screen and no explanation. Never hiding a legitimate failure is the rule; this is what
        /// it costs to keep it here.
        ///
        /// ⚠ It reports, it never decides: the refusal above is unchanged. Recovering such a text
        /// would mean reading the game's own font to learn what its private code points draw —
        /// possible with what this repository already parses, and a piece of work in its own right.
        /// Whether it is worth doing is exactly what this line exists to find out.
        ///
        /// ⚠ A pictogram inside a sentence is not this: that text carries letters and goes through
        /// normally. Only a text of nothing else counts, and only a large share of them is a
        /// signal — a handful is an icon font behaving as icon fonts do.
        /// </summary>
        private static void NotePrivateUseShare(string text)
        {
            if (_privateUseReported) return;

            _textsSeenAtDoor++;
            if (TextNormalization.IsPrivateUseOnly(text)) _privateUseOnlyTexts++;

            // Enough to be a shape rather than a coincidence, and early enough to be read in a
            // log somebody pastes into an issue.
            const int Enough = 200;
            if (_textsSeenAtDoor < Enough) return;

            _privateUseReported = true;

            int percent = _privateUseOnlyTexts * 100 / _textsSeenAtDoor;
            if (percent < 50) return;

            LogWarning($"[Text] {percent}% of the first {Enough} texts this game showed are private-use "
                     + "code points only — its font very likely encodes its own alphabet there. Nothing of "
                     + "that is translatable as it stands: a private code point says which glyph to draw "
                     + "and not which character it is. Please report the game, this is worth measuring.");
        }

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

            // ⚠ Reported because nobody had ever looked. A whole family of decisions — which texts
            // are worth translating, which are ours coming back, what a shaper makes of a syllable
            // — rests on this runtime's character tables, and this project has already met a game
            // shipping a corlib trimmed to half its size. One line, once, settles it per game
            // instead of leaving it a suspicion. See TextNormalization.DescribeUnicodeSupport.
            sb.AppendLine($"[Env]  Unicode tables: {Safe(TextNormalization.DescribeUnicodeSupport)}");
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

                // The two migrations of the sync block that need the raw json — the "check on
                // start" switch that became a frequency, and the frequency that became a rhythm
                // plus a stream switch. The rule is ModConfig.CompleteSyncFromRaw's, held by
                // spec/config's cases; here it only decides whether the file is written back.
                if (Config.CompleteSyncFromRaw(rawJson))
                {
                    LogDebug($"[Config] Sync settings migrated -> update_check_frequency={Config.sync.update_check_frequency}, "
                             + $"realtime_own_translation={Config.sync.realtime_own_translation}");
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

            // Re-derived from the file being loaded, like everything else here: a reload may land
            // on a file that states no language, and keeping the previous one would let a restored
            // backup inherit the languages of the file it replaced. The verdict goes with them —
            // a download that resolves the disagreement must not leave the old one standing.
            _languages.Reset();

            // The mod's own interface lives in its own file and is read first: the game file below
            // may still carry interface lines (written before the split, or arrived with somebody
            // else's translation) and what happens to them depends on what this already holds.
            SaveModUiCacheIfDirty();
            LoadModUiCache();

            // 🔴 **Every section, emptied — because a section a file does not carry means "this
            // translation has none", never "keep the last one's".**
            //
            // Each branch that reads a section only runs when its key is present, so an absent
            // section left the PREVIOUS translation's answer in place, and the next save wrote it
            // into the file that never had it. Only the game settings were reset here; the other
            // five were not.
            //
            // ⚠ Observed on a real install (2026-09-08), and it is what a backup is for: a
            // Chinese→English translation restored over a Chinese→French one came back carrying the
            // French one's replacement image, its two exclusions and its variables. Its own backup
            // has none of the three. The image then showed on a translation nobody had put it in,
            // and an upload would have published all of it.
            //
            // ⚠ Fonts included, inventory and all: the file is the whole truth at load, and what a
            // font is called on this machine rebuilds itself as the game is played.
            //
            // ⚠ **The socle's list, not a copy of it.** These were six calls written out by hand,
            // beside six branches further down that read the same six keys by hand — so emptying
            // and reading could disagree, and a seventh section would have been in neither.
            foreach (string section in SettingsSections.All)
            {
                ApplySectionAtLoad(section, null);
            }
            // Both are written only when non-default, so their ABSENCE from the file means "clean".
            // Without resetting them here the parse below simply never assigns, and the in-memory
            // value survives the reload: after downloading the server's copy — which carries
            // neither key — the mod still claimed unsynced settings and local changes, with no way
            // for the user to clear it.
            MetadataDirty = false;
            // A fresh store: identity, stamps, ancestor — every field at its "absent" value, so
            // nothing of the previous file can survive into this one (the block below says why).
            _store = new TranslationStore(CachePath, LogDebug);

            // 🔴 **Who this translation IS, and what it has to do with the server — re-derived from
            // the file, never inherited from the one loaded before it.**
            //
            // Every block below is written only when it has something to say, so a file that says
            // nothing left the previous file's answer standing: the branches that read them simply
            // never ran. Somebody switching between translations in one session — restoring a
            // backup, downloading somebody else's to look at it, going back to their own — carried
            // one file's identity onto the next.
            //
            // ⚠ **Observed, not deduced** (2026-09-08, on a real install): a published English→French
            // translation, then a never-published English→Thai backup restored over it. The Thai
            // file came out of the mod carrying `_source: { hash: 57881c8a…, site_id: 12 }` — the
            // FRENCH translation's row and content hash — written to its own file on disk. From
            // then on the Thai content was compared against the French translation's server hash,
            // so the sync verdict disagreed permanently and nothing could settle it.
            //
            // ⚠ It only ever bit WITHIN a session: on a fresh launch these start empty, which is
            // why a file could look clean until the moment somebody switched.
            //
            // 🔴 FileUuid included, and it is the worst of them: a file with no `_uuid` — an old
            // one, or one edited by hand — kept the previous LINEAGE's identity and had it written
            // back, instead of the fresh one the block further down exists to give it.
            //
            // The fourth defect of this family (game settings, then _local_changes and
            // _metadata_dirty, then these). See analyse/plan-prealables-couches.md 6r: the shape
            // that ends the family is a record with a value for every field, where "the previous
            // one survives" stops being expressible — which is what the fresh TranslationStore
            // above now is: FileUuid, LastSyncedHash, LastMergedMainHash, SourceSiteId and the
            // four ForkedFrom* read through it and start null.

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
                // ⚠ Emptied HERE and filled at the end, not built in place. The worker thread
                // reads this map while the file is being read, and it has always seen an empty one
                // during that window — leaving the previous translation visible instead would be a
                // change to a concurrency window nobody designed, in the method that already cost
                // data once.
                TranslationCache = new Dictionary<string, TranslationEntry>();
                // Fresh cache: allow own-UI labels that failed once to be submitted again.
                _queue.ForgetOwnUiSubmitted();

                // 🔴 **The whole file, read into a record with a value for every field** —
                // Engine/LoadedFile.cs, where each key's reading is held by cases. Then every
                // field assigned from it, unconditionally: a file that says nothing says "none",
                // and "the previous one survives" is no longer expressible. The reset above still
                // stands for the two paths that never get here: no file, and a file that could
                // not be read.
                var file = LoadedFile.Read(parsed);

                int engineVersion = file.EngineVersion;
                // Identity and stamps, every field, as the file states them.
                Store.TakeIdentity(file);
                // Kept only when the file says something: a file written with "auto" in it — an
                // older mod, or a hand edit — states nothing, and reading it as an answer would
                // let a mode outrank the server. LanguageState judges the value; absent is absent.
                if (file.SourceLanguage != null) _languages.StateSource(file.SourceLanguage);
                if (file.TargetLanguage != null) _languages.StateTarget(file.TargetLanguage);
                MetadataDirty = file.MetadataDirty;
                string savedSteamId = file.SavedSteamId;

                // ui_font described the MOD's interface from inside the GAME's file. Read once,
                // carried to the interface file by the migration below, never written back here.
                string strandedUiFont = file.StrandedUiFont;
                if (!string.IsNullOrEmpty(strandedUiFont)) cacheModified = true;

                // Every section the file carries, through the one door that also emptied them
                // above — the fonts' exception (inventory kept or not) is a named decision there.
                foreach (var section in file.Sections)
                {
                    ApplySectionAtLoad(section.Key, section.Value);

                    // What the section HOLDS once read, not what the file offered: an entry
                    // the owner refused (a rule with no pattern, an image with no sprite) is
                    // the kind of thing worth seeing in a log from a game we do not have.
                    if (DebugMode)
                    {
                        var held = BuildSettingsSection(section.Key);
                        var heldArray = held as JArray;
                        var heldObject = held as JObject;
                        int kept = heldArray != null ? heldArray.Count
                                 : heldObject != null ? heldObject.Count
                                 : 0;
                        LogDebug($"[LoadCache] {SettingsSections.Name(section.Key)}: {kept} kept");
                    }
                }

                var entriesRead = file.Entries;
                TranslationCache = entriesRead.Entries;
                // Interface lines found where they no longer belong — see the migration below.
                Dictionary<string, TranslationEntry> strandedModUi = entriesRead.StrandedModUi;

                // Generate UUID if not present
                if (string.IsNullOrEmpty(FileUuid))
                {
                    FileUuid = Guid.NewGuid().ToString();
                    cacheModified = true;
                    LogDebug($"Legacy cache file, generated UUID: {FileUuid}");
                }

                // Capture-order index: the counter is recomputed from the file and lines without
                // one are given one, deterministically — see TranslationFileEntries for why the
                // order has to be the key's and what it costs in sync (nothing).
                long nextIndex = entriesRead.AssignMissingIndices();
                if (entriesRead.Backfilled > 0)
                    LogDebug($"[LoadCache] Backfilled capture-order index on {entriesRead.Backfilled} entries");

                // Everything reading could change about what this file should hold, taken up once:
                // a legacy shape, a key normalised, an interface line taken out, an index filled in.
                if (entriesRead.NeedsRewrite) cacheModified = true;

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

                // ── The interface lines this file was still carrying ──────────────────
                // The rule is ModUiMigration.Decide — pure, and checked there rather than here.
                //
                // ⚠ **No backup is taken, and that is a decision rather than an omission.** This
                // rewrites translations.json without lines it used to hold, which normally calls
                // for one — but nothing here can be lost: a line that is kept is written to the
                // interface file BELOW, before the game's file is next saved, and a line that is
                // dropped is one the published copy still holds, one the interface file already
                // has, or one with nothing in it. The remaining case — a published line whose
                // server row later disappears — costs one pass of the translator on the mod's own
                // menus, never a word of somebody's work.
                //
                // ⚠ **Runs BEFORE the file adopts a language below, and the order is the fix.** The
                // language it compares must be the one the FILE stated, never one derived from the
                // configuration: a translations.json restored from a Thai-era backup while the
                // machine is set to French would otherwise be read as French and its Thai labels
                // declared a match. That is precisely how 28 of them landed in a French interface.
                AdoptStrandedInterfaceLines(strandedModUi, TranslationFiles.Name);

                // ── The language this file states about itself ────────────────────────
                SettleLanguagesFromFile();

                // 🔴 **The interface file is per LANGUAGE, and until here we did not know which
                // one.** It is read at the top of this method because the migration below needs to
                // know what it already holds — but at that point the target language is still the
                // PREVIOUS translation's, so a reload that changes language put the wrong one in
                // place: the French interface stayed loaded on an English translation, and the
                // English one stayed set aside where it could not be found.
                //
                // ⚠ Asked again rather than moved: both readings are needed, and only the second
                // one can be right about the language.
                if (!ModUiStore.SameLanguage(_modUiLoadedFor, Config?.GetTargetLanguage()))
                {
                    LogDebug($"[ModUI] The file settled on {Config?.GetTargetLanguage() ?? "no language"}; "
                             + $"the interface was read for {_modUiLoadedFor ?? "no language"}");
                    SaveModUiCacheIfDirty();
                    LoadModUiCache();
                }

                // Same move for the font that interface asked for: it described the MOD from
                // inside the GAME's file. Taken over only when nothing local already answers,
                // and never taken from a file that came from the server.
                if (!string.IsNullOrEmpty(strandedUiFont)
                    && string.IsNullOrEmpty(ModUiFont)
                    && string.IsNullOrEmpty(Config?.interface_font)
                    && AncestorCache.Count == 0)
                {
                    _modUi.AdoptFont(strandedUiFont);
                    InvalidateInterfaceFontAvailability();
                    Adapter.LogInfo($"[ModUI] Interface font '{strandedUiFont}' moved out of translations.json.");
                }

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
                _readback.ClearGame();
                foreach (var kv in TranslationCache)
                    IndexTranslatedValue(kv.Key, kv.Value.Value, ownUi: false);
                // The interface's, into ITS OWN index — never the game's. LoadModUiCache fills it
                // too; doing it again here is free and keeps a reload of the game's file from
                // leaving the interface unrecognisable to itself.
                foreach (var kv in ModUiCache)
                    IndexTranslatedValue(kv.Key, kv.Value.Value, ownUi: true);

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

                // Asked once the server state is back in place: a reload lands on a file that may
                // agree with the lineage where the previous one did not, or the reverse.
                NoteLanguageConflict();

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

                // What the migration above took is on disk before anything else runs: the next
                // save of translations.json writes it without those lines, and a crash between
                // the two would otherwise lose them for good.
                SaveModUiCacheIfDirty();

                Adapter.LogInfo($"Loaded {TranslationCache.Count} cached translations, {_readback.TargetCount(false)} reverse entries, {_readback.ReadbackCount(false)} decoration-insensitive, UUID: {FileUuid}");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load cache: {e.Message}");
                TranslationCache = new Dictionary<string, TranslationEntry>();

                // ⚠ The reverse indexes with it. They are built from the cache in the success path
                // and answer "have we already written this line"; kept beside an EMPTY cache they
                // go on answering about a file that could not be read.
                _readback.ClearGame();

                // Fresh cache: allow own-UI labels that failed once to be submitted again.
                _queue.ForgetOwnUiSubmitted();
                FileUuid = Guid.NewGuid().ToString();
                lock (lockObj)
                {
                    nextTranslationIndex = 1;
                }
            }
        }

        private static void LoadAncestorCache()
        {
            try
            {
                Store.LoadAncestor();
                AncestorSettings = SettingsFromSections(Store.AncestorSettings);
            }
            catch (Exception ae)
            {
                Adapter.LogWarning($"Failed to load ancestor cache: {ae.Message}");
                AncestorSettings = null;
            }
        }

        #region The mod's own interface — modui-translate.json

        /// <summary>
        /// The file itself — reading, writing and setting aside live in <see cref="ModUiStore"/>,
        /// which knows nothing of Unity and can therefore be replayed by the checks project.
        ///
        /// 🔴 **That is not tidiness, it is where the data loss was.** The rules about which line
        /// goes where were pure and checkable from the start; what went wrong was a right rule
        /// firing at the wrong MOMENT, and a moment only exists in a sequence. See
        /// tests/UnityGameTranslator.Core.Checks/ModUiStoreChecks.cs.
        /// </summary>
        private static readonly ModUiStore _modUi = new ModUiStore();

        /// <summary>
        /// The language the interface file holds, as it says itself (`_target_language`).
        ///
        /// ⚠ The FILE is the authority, never its name: the name carries a slug that does not
        /// round-trip for the handful of languages the catalogue has no code for.
        /// </summary>
        public static string ModUiLanguage => _modUi.Language;

        /// <summary>
        /// Font the interface file asks to be rendered with (`_settings.ui_font`).
        ///
        /// 🔴 **It moved out of translations.json, where it was a mod setting inside the game's
        /// file.** It travels here so that copying this one file to another game brings the font
        /// that makes it readable with it. <see cref="ModConfig.interface_font"/> still wins: a
        /// local choice beats what a file asks for.
        /// </summary>
        public static string ModUiFont => _modUi.Font;

        /// <summary>True once the interface file holds something to show.</summary>
        public static bool ModUiHasLines => ModUiCache.Count > 0;

        /// <summary>
        /// Read the interface file for the language this game is being played in.
        ///
        /// 🔴 **A file in another language is put away, never overwritten and never used.** An
        /// interface translated into French is noise in a Korean game, and deleting it would throw
        /// away a pass of the translator for somebody trying a language for an evening. Coming back
        /// to that language finds the file again, because the set-aside name is derived from the
        /// language and not remembered anywhere.
        /// </summary>
        // Which target language the interface store was last read for. The file it reads is per
        // language, and inside LoadCache the answer changes halfway through — see the second call.
        private static string _modUiLoadedFor;

        private static void LoadModUiCache()
        {
            _modUi.Info = m => Adapter?.LogInfo(m);
            _modUi.Warn = m => Adapter?.LogWarning(m);

            _modUiLoadedFor = Config?.GetTargetLanguage();
            _modUi.Load(ModFolder, _modUiLoadedFor);
            ModUiCache = _modUi.Entries;

            // Rebuilt from what was just read, exactly as LoadCache does for the game's: a language
            // set aside leaves its translations in here otherwise, answering about a file they left.
            _readback.ClearOwnUi();
            _queue.ForgetOwnUiSubmitted();

            // Its translated forms go into the INTERFACE's reverse index — its own, never the
            // game's. It is the anti-loop device that stops one of our labels, read back from a
            // component, being learnt as a new source; it has no business answering about the
            // game's text, whose register is not this tool's.
            foreach (var kvp in ModUiCache)
                IndexTranslatedValue(kvp.Key, kvp.Value.Value, ownUi: true);

            InvalidateInterfaceFontAvailability();

            if (ModUiCache.Count > 0)
            {
                Adapter?.LogInfo($"[ModUI] Loaded {ModUiCache.Count} interface line(s)"
                    + (string.IsNullOrEmpty(ModUiLanguage) ? "" : $" in {ModUiLanguage}")
                    + (string.IsNullOrEmpty(ModUiFont) ? "" : $", font {ModUiFont}"));
            }
        }

        /// <summary>Write the interface file. See <see cref="ModUiStore.Save"/>.</summary>
        public static void SaveModUiCache()
        {
            lock (lockObj)
            {
                if (_modUi.Save(ModFolder, Config?.GetTargetLanguage(), EffectiveInterfaceFont)
                    && DebugMode)
                {
                    Adapter?.LogInfo($"[ModUI] Saved {ModUiCache.Count} interface line(s)");
                }
            }
        }

        /// <summary>Write it only if the file does not already hold what we have.</summary>
        public static void SaveModUiCacheIfDirty()
        {
            lock (lockObj)
            {
                _modUi.SaveIfDirty(ModFolder, Config?.GetTargetLanguage(), EffectiveInterfaceFont);
            }
        }

        /// <summary>Something changed in the interface that the file does not yet hold.</summary>
        private static bool modUiCacheModified
        {
            get { return _modUi.Modified; }
            set { _modUi.Modified = value; }
        }

        // 🔴 **Where an interface line arriving from the network is stopped, and why there is no
        // function here for it.** Two doors, each already the only way through:
        //
        //  · <see cref="TranslationMerger.MergeWithTags"/> never emits one, so nothing a download,
        //    a Main merge or the browser editor brings back can reach TranslationCache;
        //  · <see cref="LoadCache"/> takes any left in the file on disk and asks
        //    <see cref="ModUiMigration.Decide"/> what to do with it.
        //
        // A third helper doing the same work in a third place would be exactly the parallel path
        // this split exists to remove — and the one nobody would remember to call.

        #endregion

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

            // 🔴 **What the fonts say NOW, because loading is about to overwrite it in silence.**
            // Replacing a font on a live component is a TRANSITION — take the old one off these
            // components, put the new one on — and the only code that performs it is
            // FontManager.UpdateFontSettings, which acts on the difference between what the map
            // holds and what it is being told. A load replaces the map wholesale, so by the time
            // anybody could ask, the difference is gone and nothing ever runs.
            var fontsBefore = new Dictionary<string, FontSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in FontSettingsMap)
            {
                fontsBefore[kvp.Key] = new FontSettings
                {
                    enabled = kvp.Value.enabled,
                    fallback = kvp.Value.fallback,
                    type = kvp.Value.type,
                };
            }

            // Restore all displayed text to originals BEFORE loading new cache,
            // so the scanner doesn't see stale translated text from the old JSON
            // and try to re-translate it
            TranslatorScanner.RestoreAllOriginals();

            // 🔴 **What was queued was queued for the file being replaced.** Every one of those
            // texts was read from a component whose text has just been put back, and would be
            // filed into the translation now loaded — in the previous one's target language, and
            // counted as a local change nobody made. They cost nothing to lose: the scanner sees
            // the same components again on its next pass and asks for whatever the new file does
            // not already answer.
            //
            // ⚠ This settles what has not left yet. What has is settled by the generation the
            // queue stamps on each item — see QueuedText.Generation.
            ClearQueue();

            LoadCache();

            // 🔴 **Every section at once, because that is what a reload is.** The file just read
            // carries its own fonts, rules, images and settings, and what depends on them has to be
            // dropped exactly as it is when one of them is replaced from a screen — this used to
            // clear the text caches alone, so a restored translation kept the previous one's font.
            //
            // ⚠ Through InvalidateForSections and not AfterSettingsSectionsChanged: the second one
            // also records that this install changed something, which a reload has not.
            InvalidateForSections(SettingsSections.All, out _);
            ReapplyFontSettings(fontsBefore);

            // What is on screen still describes the file that was there a moment ago. Said HERE
            // and not by the callers: it was one of five, and the four that forgot included
            // putting a backup back.
            UI.TranslatorUIManager.NotifyTranslationReloaded();
        }

        /// <summary>
        /// Walk the fonts from what they were to what the file just read says, through the one door
        /// that knows how to change a font on components already showing text.
        ///
        /// 🔴 **Why the transition has to be replayed rather than simply applied.**
        /// <see cref="FontManager.UpdateFontSettings"/> takes the OLD replacement off the components
        /// wearing it before putting the new one on, and it works out what to take off from the map
        /// it is about to change. A load overwrites that map first, so the door sees no change and
        /// does nothing at all — the components keep wearing what the previous translation asked
        /// for, for the rest of the session.
        ///
        /// ⚠ Observed on a real install (2026-09-08): a French translation replacing Alatsi with
        /// Birch Std on 171 components, then a Thai one restored over it asking for Tahoma. The log
        /// shows its fonts section loaded and then nothing whatsoever; the game stayed on Birch Std
        /// until the fallback was toggled by hand in the Fonts tab, which goes through this door.
        ///
        /// ⚠ **A font the new file does not mention is a font with no replacement**, not a font to
        /// leave alone: its components are wearing something the translation now on disk never
        /// asked for. Its detected type is carried over — the other player may simply never have
        /// met that font.
        /// </summary>
        private static void ReapplyFontSettings(Dictionary<string, FontSettings> before)
        {
            if (before == null) return;

            var names = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var name in FontSettingsMap.Keys) names.Add(name);

            foreach (var name in names)
            {
                FontSettings was;
                if (!before.TryGetValue(name, out was)) was = null;

                FontSettings now;
                if (!FontSettingsMap.TryGetValue(name, out now)) now = null;

                bool wasEnabled = was == null || was.enabled;
                string wasFallback = was?.fallback;
                bool nowEnabled = now == null || now.enabled;
                string nowFallback = now?.fallback;

                if (wasEnabled == nowEnabled
                    && string.Equals(wasFallback, nowFallback, StringComparison.Ordinal))
                    continue;

                // The door reads the map to know what to undo, so it has to find the old answer
                // there — the load has already put the new one in its place.
                if (now == null)
                {
                    now = new FontSettings { type = was?.type ?? "Unknown" };
                    FontSettingsMap[name] = now;
                }
                now.enabled = wasEnabled;
                now.fallback = wasFallback;

                FontManager.UpdateFontSettings(name, nowEnabled, nowFallback);
            }
        }

        // ── Upstream ancestor (branches only) ────────────────────────────────
        // The Main as it stood at the last merge from it. SEPARATE from
        // AncestorCache on purpose: that one is this translation's own last synced
        // state, and feeding it to a Main→branch merge would make every key the
        // branch owns (present locally and in its ancestor, absent from the Main)
        // look like a remote deletion. See analyse/main-to-branch-sync.md §2.

        private static string MainAncestorPath => TranslationFiles.MainAncestorOf(CachePath);

        /// <summary>The Main as last merged from (TranslationStore.ReadMainAncestor); empty when there is none, or none readable.</summary>
        public static Dictionary<string, TranslationEntry> LoadMainAncestor()
        {
            try
            {
                var result = Store.ReadMainAncestor();
                LogDebug($"Loaded {result.Count} upstream ancestor entries");
                return result;
            }
            catch (Exception e)
            {
                // Unreadable: an empty ancestor is safe, a wrong one is not
                Adapter.LogWarning($"Failed to load upstream ancestor ({e.Message}) - merging additively");
                return new Dictionary<string, TranslationEntry>();
            }
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
        /// The Main's settings as last merged from. Same rule as AncestorSettings: null means "no
        /// common baseline", which makes the next sync ask rather than guess.
        /// </summary>
        public static TranslationSettings LoadMainAncestorSettings()
        {
            try
            {
                return SettingsFromSections(Store.ReadMainAncestorSettings());
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to read upstream ancestor settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Remember the Main exactly as it was merged, so the NEXT merge can tell
        /// what upstream changed instead of asking about everything again. The moment is the
        /// store's (TranslationStore.NoteMainMerged); this only logs what it could not write.
        /// </summary>
        public static void SaveMainAncestor(Dictionary<string, TranslationEntry> mainContent, string mainHash,
            TranslationSettings mainSettings = null)
        {
            try
            {
                Store.NoteMainMerged(mainContent, mainHash, SectionsOf(mainSettings));
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save upstream ancestor: {e.Message}");
            }
        }

        /// <summary>
        /// After a download or an upload: the cache IS what the server holds, so it becomes the
        /// ancestor and nothing is left to publish (TranslationStore.NoteSynced). Callers set
        /// LastSyncedHash and SourceSiteId first, then this, then SaveCache — the ancestor moves
        /// before the file is written, because the count in the file is measured against it.
        /// </summary>
        public static void SaveAncestorCache()
        {
            try
            {
                AncestorSettings = TranslationSettings.FromCurrentState();
                Store.NoteSynced(null, null, TranslationCache, SectionsOf(AncestorSettings));
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor cache: {e.Message}");
            }
        }

        /// <summary>
        /// After a merge: the PUBLISHED lines become the ancestor, never the merged ones, and the
        /// count becomes what the cache has that the published version does not
        /// (TranslationStore.NoteMerged). The published settings go with it only when actually
        /// seen: an invented baseline is worse than none.
        /// </summary>
        public static void SaveAncestorFromRemote(Dictionary<string, TranslationEntry> remoteTranslations,
            TranslationSettings remoteSettings = null)
        {
            try
            {
                AncestorSettings = remoteSettings;
                Store.NoteMerged(null, remoteTranslations, SectionsOf(remoteSettings), TranslationCache);
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor from remote: {e.Message}");
            }
        }

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

        /// <summary>Counted against the ancestor, never trusted — TranslationStore.Recount.</summary>
        public static void RecalculateLocalChanges()
        {
            Store.Recount(TranslationCache);
        }

        /// <summary>
        /// Take somebody else's translation apart, by exactly the rules this mod's own file is read
        /// by — see <see cref="TranslationFileEntries"/>.
        ///
        /// 🔴 **It used to have rules of its own, and that was a regression rather than a choice.**
        /// The line-ending fix of 2025-12-30 was applied to both sides; the remote half lived inline
        /// in the caller, on the string-based reader it used then, and went with it when that reader
        /// was rightly deleted on 2026-07-28. From then on a downloaded key kept its CRLF while the
        /// local cache and the ancestor had theirs collapsed — so a three-way merge read one key as
        /// deleted locally and added remotely, both at once.
        ///
        /// ⚠ And normalising the keys is what MAKES two of them collide, so the collision rule
        /// (a person's line over a review's over a model's) has to arrive with it or the fix opens
        /// the hole it closes. Both come from reading the file the one way it is read.
        ///
        /// ⚠ **The interface lines it finds are put BACK, on purpose.** They have exactly one
        /// door — <see cref="TranslationMerger.MergeWithTags"/>, which drops them from the result
        /// whichever side they came from — and the note above ReloadCache says why there must not
        /// be a second: a suppression here would pre-empt that door silently and make its own
        /// count of what it dropped read zero. What this method owes the merge is the file as it
        /// stands; what to keep is the merge's to decide.
        /// </summary>
        /// <param name="jsonContent">Raw JSON string from file or API</param>
        public static Dictionary<string, TranslationEntry> ParseTranslationsFromJson(string jsonContent)
        {
            try
            {
                // The file's own line endings first; the keys and values inside are normalized by
                // the reader, which is where that rule belongs.
                var read = TranslationFileEntries.ReadAll(
                    JObject.Parse(jsonContent.Replace("\r\n", "\n")));

                if (read.StrandedModUi != null)
                {
                    foreach (var line in read.StrandedModUi) read.Entries[line.Key] = line.Value;
                }

                return read.Entries;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to parse translations from JSON: {e.Message}");
                return new Dictionary<string, TranslationEntry>();
            }
        }

        /// <summary>
        /// Interface lines found in the game translation ON DISK, sent where they belong.
        ///
        /// 🔴 **Only ever this file, and that is the design rather than a limitation.** A line
        /// arriving from the network is stopped by <see cref="TranslationMerger.MergeWithTags"/>,
        /// which drops it from the result whichever side it came from — see the note above
        /// ReloadCache, which names the two doors and warns that a third would be the parallel
        /// path the split exists to remove. This is the second of those two, named and nothing
        /// more; calling it from anywhere else would make it the third.
        ///
        /// ⚠ The language compared is the one the FILE states, never one derived from the
        /// configuration: a translation restored from a Thai-era backup while the machine is set to
        /// French would otherwise be read as French and its Thai labels declared a match. That is
        /// precisely how 28 of them landed in a French interface.
        /// </summary>
        /// <param name="lines">What was found, or null when there was none.</param>
        /// <param name="source">Where they were found, for the line that says what happened.</param>
        internal static void AdoptStrandedInterfaceLines(Dictionary<string, TranslationEntry> lines, string source)
        {
            if (lines == null || lines.Count == 0) return;

            int kept = 0, dropped = 0;
            foreach (var kvp in lines)
            {
                var verdict = ModUiMigration.Decide(
                    inAncestor: AncestorCache.ContainsKey(kvp.Key),
                    alreadyHeld: ModUiCache.ContainsKey(kvp.Key),
                    isEmpty: kvp.Value.IsEmpty,
                    lineLanguage: FileTargetLanguage,
                    interfaceLanguage: ModUiLanguage);

                if (verdict == ModUiMigration.Verdict.Drop)
                {
                    dropped++;
                    continue;
                }

                ModUiCache[kvp.Key] = kvp.Value;
                IndexTranslatedValue(kvp.Key, kvp.Value.Value, ownUi: true);
                kept++;
            }

            if (kept > 0) modUiCacheModified = true;
            Adapter?.LogInfo($"[ModUI] {source} carried {lines.Count} interface line(s): "
                             + $"{kept} moved to {ModUi.FileName}, {dropped} dropped "
                             + "(published, already held, of another language, or empty).");
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

                // Interface lines the published copy still carries count as present — see
                // ModUiMigration.StillCountsAsPublished for why, and for its cases.
                foreach (var kvp in AncestorCache)
                {
                    if (!ModUiMigration.StillCountsAsPublished(kvp.Value?.Tag,
                                                               TranslationCache.ContainsKey(kvp.Key)))
                        continue;

                    lines.Add(new KeyValuePair<string, TranslationLine>(
                        kvp.Key, new TranslationLine(kvp.Value.Value, kvp.Value.Tag)));
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
        /// ⚠ Property names sorted, list order kept — see <see cref="CanonicalJson"/>, where that
        /// rule lives with its cases. A section is rebuilt from dictionaries whose order is not
        /// promised across insertions, while a font-rule list is applied in sequence, so order is
        /// noise in one and content in the other.
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

                foreach (var section in SettingsSections.All)
                {
                    // Null when the section is empty — which is what SaveCache writes, so an
                    // emptied section and one that never existed fingerprint alike.
                    var token = BuildSettingsSection(section);
                    var container = token as JContainer;
                    if (container == null || !container.HasValues) continue;

                    document.Append('|').Append(section).Append(':').Append(CanonicalJson.Of(token));
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

                var matchRegex = NumberPatterns.BuildPatternRegex(kv.Key, out var placeholderIndices, compiled: true);
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
                    var reverseRegex = NumberPatterns.BuildPatternRegex(pe.TranslatedPattern, out var groupPlaceholders);
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
        /// What is wrong with a translation somebody just typed, or null when nothing is.
        ///
        /// 🔴 **The socle's rule, not a looser one of ours.** This checked missing and unknown
        /// tokens and nothing else, so a person could DUPLICATE a placeholder — or drop the
        /// bracket the game wrapped around one — and save it, where a model doing the same was
        /// refused three times and then given up on. The game substitutes at runtime and does not
        /// care who was at the keyboard.
        ///
        /// ⚠ <see cref="Placeholders.AcceptsEdit"/> and not <see cref="Placeholders.Accepts"/>:
        /// the one check left out is the count of brackets over the whole text, which would refuse
        /// "Save" → "Save [F5]". That is somebody's own addition and it breaks nothing.
        /// </summary>
        public static string ValidateEditedPlaceholders(string key, string newValue)
        {
            string source = key ?? "";
            string edited = newValue ?? "";

            if (Placeholders.AcceptsEdit(source, edited, Placeholders.FrozenSequences(source), out var errors))
                return null;

            return string.Join("; ", errors);
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

            // 🔴 The editors only ever show the GAME's lines — the in-game inspector skips our own
            // components, and the browser is handed translations.json. A key that only exists in
            // the interface file therefore cannot legitimately arrive here, and creating it in the
            // game's file is exactly the mixing this split removes.
            if (!TranslationCache.ContainsKey(key) && ModUiCache.ContainsKey(key))
            {
                LogWarning($"[Editor] REFUSED to save: '{(key.Length > 40 ? key.Substring(0, 40) + "…" : key)}' "
                           + $"belongs to the mod's interface ({ModUi.FileName}), not to this game's translation.");
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

            // Reverse cache sync so the new value isn't detected as untranslated text. The GAME's:
            // this door only ever writes the game's lines (it refuses an interface key above).
            IndexTranslatedValue(key, newValue, ownUi: false);

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
        ///
        /// ⚠ The GAME's file, and deliberately not the interface one: the browser is handed
        /// translations.json and nothing else, so a key it sends that only exists in the mod's
        /// interface did not come from anything it was shown.
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

                // Which file the line lives in decides everything downstream: the prompt it is
                // asked with, the tag it comes back as, and the file the answer is written to. A
                // retranslation attaches no component, so this is the only place that can say it.
                //
                // ⚠ The GAME's file first when both hold the key. The editors that offer this
                // gesture — the in-game inspector, the browser — are handed the game's lines and
                // nothing else, so a key they name is the game's even when our interface happens
                // to use the same words.
                bool isOwnUI = !TranslationCache.ContainsKey(key) && ModUiCache.ContainsKey(key);
                var store = isOwnUI ? ModUiCache : TranslationCache;

                bool hadEntry = store.TryGetValue(key, out var previous);
                request = new RetranslateRequest
                {
                    Key = key,
                    HadEntry = hadEntry,
                    PreviousValue = hadEntry ? previous.Value : null,
                    PreviousTag = hadEntry ? (previous.Tag ?? "A") : null,
                    PreviousIndex = hadEntry ? previous.Index : null,
                    IsOwnUI = isOwnUI,
                    StoreResult = storeResult
                };
                retranslateRequests[key] = request;

                // Proposing changes nothing until a human says so — the entry stays exactly where
                // it is, and the worker is told to skip the cache rather than read it.
                if (storeResult)
                    store.Remove(key);
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
            _queue.ForgetRefused(normalizedKey);

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

                if (Placeholders.Invented(normalizedKey, candidate).Count > 0)
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
                // Back where it was taken from — the request remembers which file that is.
                var store = request.IsOwnUI ? ModUiCache : TranslationCache;
                store[request.Key] = new TranslationEntry
                {
                    Value = request.PreviousValue,
                    Tag = request.PreviousTag,
                    // The interface is not in the web editors' ordered list and takes no index.
                    Index = request.IsOwnUI ? request.PreviousIndex : (request.PreviousIndex ?? NextOrderIndex())
                };
                if (request.IsOwnUI) modUiCacheModified = true;
            }

            if (!request.IsOwnUI && request.Key.Contains(PlaceholderPrefix))
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

        #region Stale Translation Snapshot (post-reload safety net)

        /// <summary>
        /// The post-reload safety net — Engine/StaleSnapshot.cs, where a reload can be replayed
        /// without a game. What stays here is the copy of the live cache (the worker may be
        /// writing it) and the host effects a verdict calls for.
        /// </summary>
        private static readonly StaleSnapshot _stale = new StaleSnapshot { Debug = m => LogDebug(m) };

        /// <summary>
        /// Snapshot the current cache values before a reload replaces them.
        /// Called by ReloadCache while the outgoing cache is still loaded.
        /// </summary>
        private static void BuildStaleTranslationSnapshot()
        {
            // Snapshot first: the AI worker can mutate TranslationCache during
            // this iteration (same reason BuildPatternEntries snapshots)
            KeyValuePair<string, TranslationEntry>[] cacheSnapshot;
            try
            {
                cacheSnapshot = new List<KeyValuePair<string, TranslationEntry>>(TranslationCache).ToArray();
            }
            catch { return; }

            _stale.Take(cacheSnapshot, Config.normalize_numbers);
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
        ///
        /// ⚠ Two containers, emptied together, and that is the point: this used to empty three of
        /// four, and the survivor was the set of "these texts are the mod's interface". A GAME text
        /// queued afterwards that happened to equal one of our labels was then filed as interface.
        /// </summary>
        /// <summary>
        /// This text turned out to be a template the game expands in place, not a line anybody
        /// reads. Take it back out of the queue if it is still waiting, and never ask for it again
        /// this session.
        ///
        /// 🔴 **Nothing is deleted.** A translation already in the file belongs to whoever built
        /// that file, and a local observation on one component cannot decide what to remove from a
        /// list keyed by text alone. This only stops a NEW one being made — which is why a file
        /// polluted before the rule existed keeps its lines until its owner cleans them out.
        ///
        /// ⚠ The refusal is in memory only, like every other refusal here: next launch asks again,
        /// and if the game still expands the text in place it will be refused again in the same
        /// second.
        /// </summary>
        public static void ForgetTemplateText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 🔴 The SKELETON, not the text. The game resolves its tokens a few at a time, and each
            // state is its own string — so a refusal recorded on one of them says nothing about the
            // next, nor about the same template appearing on another component in another half-
            // resolved form. Measured: the fully-tokenised state was refused while
            // `…[*White*] Energy, add 2 Strength.` went to the model on the component beside it and
            // came back with the keyword translated, which is exactly what the game cannot expand.
            string skeleton = TextRelations.ExpansionSkeleton(text);
            if (skeleton.Length == 0) return;

            bool known;
            lock (lockObj) { known = !_expandedInPlace.Add(skeleton); }
            if (known) return;

            string key = NormalizeForCacheLookup(text);
            bool withdrawn = _queue.Withdraw(text) || _queue.Withdraw(key);
            _queue.NoteRefused(key);

            LogInfo($"[TW-TEMPLATE] the game expands this in place — {(withdrawn ? "taken out of the queue" : "it was not waiting")}, not asked again, and never written back: '{(text.Length > 60 ? text.Substring(0, 60) : text)}'");
        }

        /// <summary>
        /// Whether this text is one the game expands in place — a template, not a line anybody
        /// reads. Asked at the three moments it matters, because it is one FACT rather than one act.
        ///
        /// 🔴 **Taking it out of the queue is not enough, and saying otherwise was wrong.** The
        /// proof arrives with the expansion, a few hundred milliseconds after the template was
        /// queued, and the worker may have taken it in between — which no amount of reasoning about
        /// how long a model takes can rule out. So the withdrawal is the best case, not the rule:
        /// what makes this deterministic is that once the pair has been seen, the text can never be
        /// queued, never be stored, and above all **never be written back**.
        ///
        /// ⚠ That last one is what protects a file polluted before this rule existed. The line stays
        /// in it — deleting somebody's translation on a local observation is the thing this project
        /// refuses — but it stops reaching the screen, so the game can expand its own text again.
        ///
        /// ⚠ In memory, per session, like every other refusal here.
        /// </summary>
        private static readonly HashSet<string> _expandedInPlace = new HashSet<string>();

        internal static bool IsExpandedInPlace(string text)
        {
            if (_expandedInPlace.Count == 0 || string.IsNullOrEmpty(text)) return false;

            // 🔴 The FINISHED form of the same template goes through, and it must: it is the line
            // the player reads and the one worth translating. Only the states that still carry
            // something for the game to resolve are refused.
            if (!TextRelations.HasUnresolvedTokens(text)) return false;

            lock (lockObj) { return _expandedInPlace.Contains(TextRelations.ExpansionSkeleton(text)); }
        }

        public static void ClearQueue()
        {
            int count = _queue.Clear();
            isTranslating = false;
            currentlyTranslating = null;
            if (count > 0)
                LogDebug($"[TranslatorCore] Cleared {count} items from translation queue");
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
                client.Timeout = CeilingFor(config);
                LogProxyMode(config);
                return client;
            }
            catch (Exception e)
            {
                Adapter?.LogError($"[HttpClient] Failed to create configured HttpClient, falling back to default: {e.GetType().Name}: {e.Message}");
                var fallback = new HttpClient();
                fallback.Timeout = CeilingFor(config);
                return fallback;
            }
        }

        /// <summary>
        /// How long one request may take before we accept that no answer is coming.
        ///
        /// 🔴 **It is a deadlock escape, not a patience limit**, and that is why it is wide. See
        /// ModConfig.timeout_ms: a slow local model answers in minutes and its answer is wanted;
        /// what this exists for is a request that has been swallowed and will never return.
        ///
        /// ⚠ A second is the floor, and it is not a policy: HttpClient refuses zero or negative,
        /// and a typo in a hand-edited file must not stop the mod from starting.
        /// </summary>
        private static TimeSpan CeilingFor(ModConfig config)
        {
            int ms = config?.timeout_ms ?? ModConfig.DefaultTimeoutMs;
            if (ms < 1000)
            {
                Adapter?.LogWarning($"[HttpClient] timeout_ms={ms} is below the one-second floor; using {ModConfig.DefaultTimeoutMs} ms");
                ms = ModConfig.DefaultTimeoutMs;
            }
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>Said once, and again only after an answer has come back.</summary>
        private static volatile bool _backendSilent;

        /// <summary>The whole text of the item in the worker's hand, or null.</summary>
        private static volatile string _inFlightText;

        /// <summary>
        /// Whether the mod still intends to do something about this text — so whoever displays it
        /// must not be written off as handled.
        ///
        /// 🔴 **The queue is the store, and it outlives a scene.** It is emptied only by a cache
        /// reload and by switching translation off — never by a scene change — so a text asked for
        /// in one scene goes on being translated in the next and its answer lands in the cache,
        /// ready for the moment that scene comes back.
        ///
        /// 🔴 **This is the recovery for the one case where a text leaves the queue WITHOUT an
        /// answer**: a request that ran out of time. The item was taken out of both containers at
        /// dequeue and only a rate limit ever put one back, while the scanner had already recorded
        /// the component as handled — the moment the text was QUEUED, not answered — so the next
        /// round returned SAME-HASH and that line stayed in the game's own language for the rest of
        /// the scene. Leaving the component unmarked is what brings it back, and it needs no retry
        /// list of ours: what is on screen and still untranslated is asked for again by itself.
        ///
        /// ⚠ **A refusal is not a debt.** Too long, numeric, our own interface, a language conflict:
        /// nothing more will be done about those, so they ARE marked and stop costing anything. Only
        /// what is waiting, in flight, or held back by a silent server counts.
        /// </summary>
        internal static bool StillOwed(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Nothing is being asked while the server is silent, and everything on screen is owed
            // the moment it answers again.
            if (_backendSilent) return true;

            return string.Equals(_inFlightText, text, StringComparison.Ordinal) || _queue.Holds(text);
        }

        /// <summary>
        /// Send one translation request, and say plainly when no answer came.
        ///
        /// 🔴 **The one door for the three backends**, because a silence is the same event whoever
        /// was asked. Before this, a request that ran out of time surfaced as
        /// `[AI] Worker error: One or more errors occurred.` — the wrapper's own words, naming
        /// neither the cause nor the wait — and the player saw a queue that had simply stopped.
        ///
        /// ⚠ Said ONCE, and again only after something answers. A dead server with fifteen texts
        /// waiting would otherwise put fifteen notices on screen, one per text, which says no more
        /// than one and buries everything else. The flag is cleared by an answer rather than by a
        /// delay: what is being reported is a state, not an instant.
        ///
        /// 🔴 **And the line IS lost, until the scene changes.** Written first as "the scanner meets
        /// it again on its next round", which is false and worth stating plainly: the scanner
        /// records the component as handled the moment the text has been QUEUED — `UpdateSeenText`
        /// and `processedTextHashes` are set in the branch where nothing was applied — so the next
        /// round answers SAME-HASH and the component is never looked at again. The item itself was
        /// taken out of both queue containers by `Take`, and only a rate limit puts one back.
        ///
        /// So a silence costs that line for the rest of the scene. What to do about it is a
        /// decision, not an oversight — see TODO: the queue is not a durable store, the SCREEN is,
        /// and reconciling from it means not marking a component done until its text was actually
        /// handled.
        /// </summary>
        private static HttpResponseMessage SendForTranslation(HttpRequestMessage request)
        {
            try
            {
                var response = httpClient.SendAsync(request).Result;
                _backendSilent = false;
                return response;
            }
            catch (AggregateException agg) when (agg.GetBaseException() is TaskCanceledException)
            {
                if (_backendSilent) return null;
                _backendSilent = true;

                TimeSpan waited = httpClient.Timeout;
                string howLong = waited.TotalMinutes >= 1
                    ? $"{waited.TotalMinutes:0.#} min"
                    : $"{waited.TotalSeconds:0.#} s";
                string message = $"No answer from the translation server after {howLong}.";

                Adapter?.LogWarning($"[AI] {message} The line is left as it is and will be asked for again. "
                                    + "If the model is simply slow, raise timeout_ms in config.json.");
                try
                {
                    UI.TranslatorUIManager.RunOnMainThread(() =>
                        UI.TranslatorUIManager.StatusOverlay?.ShowToast(message,
                            UI.ToastTone.Off));
                }
                catch { }
                return null;
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
            // Whatever this runtime needs before one of our threads touches anything — on IL2CPP,
            // being made known to a collector that would otherwise abort the process on sight.
            // The adapter knows; the Core, compiled once for both runtimes, could only guess.
            Adapter?.OnWorkerThreadStarted();

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

                QueuedText queued = null;
                string textToTranslate = null;
                List<object> componentsToUpdate = null;
                bool queuedAsOwnUI = false;

                // The item carries its targets AND its origin, so nothing has to be looked up
                // from the text — and nothing can be lost by looking up one of the two and
                // forgetting the other, which is what a re-queue used to do.
                queued = _queue.Take();
                if (queued != null)
                {
                    textToTranslate = queued.Text;
                    componentsToUpdate = queued.Targets.Count > 0 ? queued.Targets : null;
                    queuedAsOwnUI = queued.FromOwnUI;

                    if (Config.debug_ai)
                    {
                        Adapter?.LogInfo(componentsToUpdate != null
                            ? $"[Worker] Found {componentsToUpdate.Count} components for text"
                            : "[Worker] NO components found for text!");
                        Adapter?.LogInfo($"[Worker] Dequeued: {textToTranslate?.Substring(0, Math.Min(30, textToTranslate?.Length ?? 0))}...");
                    }
                }

                if (textToTranslate != null)
                {
                    string originalText = textToTranslate;
                    if (Config.debug_ai)
                    {
                        string workerPreview = textToTranslate.Length > 40 ? textToTranslate.Substring(0, 40) + "..." : textToTranslate;
                        Adapter?.LogInfo($"[Worker] Processing: {workerPreview} (queue remaining: {_queue.Count})");
                    }
                    isTranslating = true;
                    currentlyTranslating = textToTranslate.Length > 50 ? textToTranslate.Substring(0, 50) + "..." : textToTranslate;
                    // ⚠ The whole text, beside the shortened one: that one is for a screen, this one
                    // answers "is this exact line still owed an answer" and a prefix cannot.
                    _inFlightText = textToTranslate;
                    // Whatever the line before this one had to be asked twice for is not this
                    // line's business: the counter starts down for every text.
                    NoteAttempt(0, 0);

                    // 🔴 **Whose text this is was settled when it was queued, and nothing re-decides
                    // it here.** The item's identity IS (text, origin) — the game's "Options" and
                    // ours are two entries, asked with two prompts and filed in two files — so
                    // there is nothing left to infer, and inferring anyway is how a shared string
                    // used to end up in whichever file asked last.
                    //
                    // ⚠ The components are deliberately not consulted: a text queued by our
                    // interface with no component at all (a help zone, a label the code writes) is
                    // just as much ours as one that came with fifty.
                    bool isOwnUI = queuedAsOwnUI;
                    currentTextIsOwnUI = isOwnUI;

                    // Declared out here so the catch below can put back what a retranslation took
                    // out: this is the one item in the queue that arrives with something to lose.
                    RetranslateRequest retranslate = null;

                    try
                    {
                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[Worker] Calling AI...{(isOwnUI ? " (UI prompt)" : "")}");

                        // A human asked for this line again, having read the answer we already had.
                        // Everything that shortcuts to a stored or previously refused answer must be
                        // skipped for it — those are exactly the answers being rejected. So it has
                        // its own loop, with a previous value to put back if it cannot get one.
                        retranslate = TakeRetranslateRequest(textToTranslate);
                        if (retranslate != null)
                        {
                            string normalizedOriginal = TextGate.KeyShape(textToTranslate, isOwnUI, GameVariables.Instance,
                                Config.normalize_numbers, out var workerExtractedVars, out var extractedNumbers);
                            RunRetranslation(retranslate, normalizedOriginal, extractedNumbers,
                                workerExtractedVars, originalText, componentsToUpdate);
                        }
                        else
                        {
                            // 🔴 Everything else that happens to one item — cache, capture, refusal,
                            // the backend, a rate limit, an invented token, a stale answer, the
                            // filing, the notification — is TranslationWorker.Process, where the
                            // ORDER is held by cases. What follows is what a verdict does here.
                            var outcome = TranslationWorker.Process(queued, WorkerContextNow(), WorkerHost.Instance);
                            switch (outcome)
                            {
                                case WorkerOutcome.Translated:
                                    aiTranslationCount++;
                                    // Request visual refresh so static text picks up the new translation
                                    PendingVisualRefresh = true;
                                    if (DebugMode || Config.debug_ai)
                                    {
                                        string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                        Adapter?.LogInfo($"[AI] {preview}");
                                    }
                                    break;

                                case WorkerOutcome.Skipped:
                                case WorkerOutcome.Declined:
                                    if (Config.debug_ai)
                                    {
                                        string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                        Adapter?.LogInfo($"[AI] Skipped (not in source language): {preview}");
                                    }
                                    break;
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
                        _inFlightText = null;
                        NoteAttempt(0, 0);
                    }

                    // Nothing to clean up: the item left the pending map at dequeue, and it carries
                    // everything else with it.
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

                // Structure into tokens, padding held back — the same preparation the
                // translation-API path uses, and the order in it is the rule. See Engine/Backends.
                var prepared = Backends.Prepare(textToTranslate);
                if (prepared.NothingToSend) return null;

                string textForAI = prepared.ToSend;
                List<string> extractedTags = prepared.Tags;

                if (Config.debug_ai && extractedTags.Count > 0)
                    Adapter?.LogInfo($"[AI] Extracted {extractedTags.Count} markup tags from text");

                // Detect which placeholder types are in the PROCESSED text
                bool hasNlPlaceholders = textForAI.Contains(Backends.LineBreak);
                bool hasTagPlaceholders = extractedTags.Count > 0;
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
                    // Said before the call, not after it: the wait IS the attempt, and a counter
                    // that appears once the answer is back has nothing left to explain.
                    NoteAttempt(attempt, maxAttempts);

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
                    _queue.NoteRefused(textWithPlaceholders);
                    Adapter?.LogWarning($"[AI] Placeholder validation failed after {maxAttempts} attempts, left untranslated: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                    return null;
                }

                // Markup, line breaks, the model's chatter, then the padding — that order,
                // for the reasons written where it lives.
                translation = Backends.Restore(prepared, translation, AnswerFrom.Model);

                if (Config.debug_ai && !string.IsNullOrEmpty(translation))
                    Adapter?.LogInfo($"[AI Clean] {translation.Substring(0, Math.Min(80, translation.Length))}");

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

                var response = SendForTranslation(request);
                // Nothing came back in time. Said already, and there is nothing to negotiate about:
                // the ladder below reasons on what a server ANSWERED.
                if (response == null) return null;

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

                var response = SendForTranslation(request);
                if (response == null) return null;

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

                var response = SendForTranslation(request);
                if (response == null) return null;

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

                // The same preparation the model path uses, written once. See Engine/Backends.
                var prepared = Backends.Prepare(textToTranslate);
                if (prepared.NothingToSend) return null;

                string textForAPI = prepared.ToSend;

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
                        _queue.NoteRefused(textWithPlaceholders);
                        Adapter?.LogWarning($"[API] Invalid placeholders ({string.Join("; ", apiErrors)}), left untranslated: {textToTranslate.Substring(0, Math.Min(60, textToTranslate.Length))}...");
                        return null;
                    }
                }

                // Same restoration, minus the one step that does not apply: these services take
                // no instructions, so there is no chatter to remove and anything removed is text.
                return Backends.Restore(prepared, translation, AnswerFrom.TranslationApi);
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[API] Translation error: {e.Message}");
                return null;
            }
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
        /// Add a translation, to the file its tag says it belongs in.
        ///
        /// 🔴 **The tag IS the destination.** <see cref="ModUi.Tag"/> means the mod's own interface
        /// and goes to <see cref="ModUiCache"/>; everything else is the game's and goes to
        /// <see cref="TranslationCache"/>. One door, one rule, and it holds whatever route got
        /// here — which is the point: the leaks this replaced were all a caller that knew the
        /// answer and a writer that did not ask.
        /// </summary>
        /// <param name="original">Original text (key)</param>
        /// <param name="translated">Translated text (value)</param>
        /// <param name="tag">Tag: A=AI, H=Human, V=Validated, S=Skipped, M=the mod's interface</param>
        public static void AddToCache(string original, string translated, string tag = "A")
        {
            if (string.IsNullOrEmpty(original))
                return;

            // Allow empty translated value for capture-only mode (H tag with empty value)
            if (string.IsNullOrEmpty(translated) && tag != "H")
                return;

            bool toModUi = tag == ModUi.Tag;

            // Normalize line endings for cross-platform consistency
            string normalizedKey = NormalizeLineEndings(original);
            string normalizedValue = NormalizeLineEndings(translated ?? "");

            lock (lockObj)
            {
                var store = toModUi ? ModUiCache : TranslationCache;

                if (store.ContainsKey(normalizedKey))
                    return;

                // A template the game expands in place. The proof arrives with the expansion, a few
                // hundred milliseconds after the text was queued — which is usually before the
                // model answers, but nothing guarantees it, so the answer is refused HERE as well.
                // That is what makes it deterministic rather than a race the worker usually loses.
                if (!toModUi && IsExpandedInPlace(normalizedKey))
                {
                    LogInfo($"[TW-TEMPLATE] answer discarded, the game expands this in place: '{(normalizedKey.Length > 60 ? normalizedKey.Substring(0, 60) : normalizedKey)}'");
                    return;
                }

                // Last stop before an entry exists: every route that creates one passes here, so this
                // is where the read-back guard finally belongs. Guarding the queue, then the
                // synchronous translate path, each time left another route open — the same
                // target-language key kept coming back. A key we can recognise as our own translation
                // wearing a different decoration must never become an entry, whoever asked for it.
                // The stack is logged once so the caller that got this far is named, not guessed at.
                if (IsReadbackOfOwnTranslation(normalizedKey, toModUi))
                {
                    if (_readbackStoreLogged < 3)
                    {
                        _readbackStoreLogged++;
                        Adapter.LogWarning($"[Readback] Refused to store a re-decorated translation as a new key: '{(normalizedKey.Length > 70 ? normalizedKey.Substring(0, 70) + "..." : normalizedKey)}'\n{Environment.StackTrace}");
                    }
                    return;
                }

                var entry = new TranslationEntry
                {
                    Value = normalizedValue,
                    Tag = tag ?? "A",
                    // The capture order is what the web editors sort by, so it is the GAME's
                    // counter. The interface is not in that list and never will be; numbering it
                    // from the same counter would leave gaps in the game's own sequence.
                    Index = toModUi ? (long?)null : NextOrderIndex()
                };

                // Asked BEFORE the line lands: this is the last instant at which the translation
                // has no line, and a target language that settles "on the first line" has to be
                // settled by the first line rather than by the second.
                bool firstGameLine = !toModUi && TranslationCache.Count == 0;

                store[normalizedKey] = entry;

                if (toModUi)
                {
                    modUiCacheModified = true;
                }
                else
                {
                    cacheModified = true;
                    if (firstGameLine) SettleTargetLanguageOnFirstLine();

                    // The running figure the screens show between two writes; the file recounts.
                    Store.NoteLocalEdit(normalizedKey, entry);
                }

                // Into the reverse index of the side this entry belongs to, never the other's.
                IndexTranslatedValue(normalizedKey, entry.Value, toModUi);

                // Note: No longer clearing lastSeenText here.
                // OnTranslationComplete updates tracked components directly.
                // New components will be translated on their next scan cycle.

                // Patterns are built from the GAME's lines only: a pattern of ours matching a
                // game text is exactly the mixing this split removes.
                if (!toModUi && normalizedKey.Contains(PlaceholderPrefix))
                {
                    BuildPatternEntries();
                }

                if (DebugMode)
                    Adapter?.LogInfo($"[{(toModUi ? "ModUI+" : "Cache+")}] {normalizedKey.Substring(0, Math.Min(40, normalizedKey.Length))}... [{tag}]");
            }
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
        /// Is there a usable translation for this SOURCE text?
        /// </summary>
        /// <param name="ownUI">
        /// True to ask about one of the mod's own labels, which lives in the interface file. The
        /// caller always knows which of the two it is holding — a game component or one of ours —
        /// so this is passed rather than guessed from the string.
        /// </param>
        public static bool HasCachedTranslation(string text, bool ownUI = false)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var store = ownUI ? ModUiCache : TranslationCache;

            // Exact match as key
            if (store.TryGetValue(text, out var exact))
            {
                if (exact.IsEmpty || exact.Tag == "S") return false;
                // key==value with tag "A" = AI couldn't translate (source language text).
                // Exception: natural identity (only digits/punctuation/placeholders) is expected
                // to be identical — not an AI failure.
                // key==value with tag "V"/"H" = human validated, intentionally same text.
                if (exact.Value == text && exact.Tag == "A" && !IsNaturalIdentity(text)) return false;
                return true;
            }

            // Normalized match as key
            string normalized = NormalizeForCacheLookup(text);
            if (store.TryGetValue(normalized, out var norm))
            {
                if (norm.IsEmpty || norm.Tag == "S") return false;
                if (norm.Value == normalized && norm.Tag == "A" && !IsNaturalIdentity(normalized)) return false;
                return true;
            }

            // Text is already a known translation (reverse cache) — the component
            // already shows translated text and should have the clone font.
            string trimmed = normalized.TrimEnd();
            if (_readback.IsTarget(trimmed, ownUI))
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
        public static string GetSourceForTranslation(string translatedText,
                                                     Dictionary<string, TranslationEntry> store = null)
        {
            if (string.IsNullOrEmpty(translatedText)) return null;
            string norm = NormalizeForCacheLookup(translatedText).TrimEnd();
            foreach (var kv in store ?? TranslationCache)
            {
                var entry = kv.Value;
                if (entry == null || entry.IsEmpty || entry.Tag == "S")
                    continue;
                if (entry.Value == translatedText || NormalizeForCacheLookup(entry.Value).TrimEnd() == norm)
                    return kv.Key;
            }
            return null;
        }

        public static string NormalizeForCacheLookup(string text)
        {
            // The key shape — one implementation, in TextGate. ⚠ isOwnUI is false here on purpose:
            // this probe has always lifted the variables out whichever side asked.
            return TextGate.KeyShape(text, isOwnUI: false, GameVariables.Instance, Config.normalize_numbers, out _, out _);
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

            // 🔴 **This file is not in the language its lineage was published in.** Writing more in
            // the file's language grows something that can never be published; writing in the
            // lineage's puts two languages in one file. There is no safe answer, so nothing new is
            // made — while everything the file already holds goes on being applied, so the game
            // stays translated and playable.
            //
            // ⚠ At the single door rather than in the worker: refusing here also stops the capture
            // mode, which would otherwise fill the file with keys belonging to neither language.
            if (LanguageConflict != null)
            {
                if (_languages.ShouldSayRefusal())
                    Adapter?.LogWarning($"[Languages] Not translating: {LanguageConflict}");
                return false;
            }

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

            // ⚠ Counted BEFORE the refusal below, so the tally covers every text this door meets
            // rather than only the ones it lets through. See NotePrivateUseShare.
            NotePrivateUseShare(text);

            if (IsNumericOrSymbol(text)) return false;

            // A template the game expands in place. Refused at this door rather than in the worker,
            // so nothing is queued at all: no line in the notice that says a translation is running,
            // and no call. See IsExpandedInPlace.
            if (IsExpandedInPlace(text)) return false;

            // 🔴 The server has stopped answering. Nothing new goes in — a queue filling behind a
            // dead server is work nobody will get, and every entry would carry its own notice — but
            // ONE at a time is let through, and that one IS the probe: it succeeds and everything
            // resumes, or it runs out of time and we are no worse off.
            //
            // ⚠ No timer anywhere. The cadence of the retry is the ceiling itself, and what lifts
            // the state is an ANSWER. Refusing everything instead would be a deadlock: nothing
            // would ever ask again, so nothing would ever answer.
            if (_backendSilent && (_queue.Count > 0 || isTranslating))
                return false;

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
                // Once per text: this runs on every scan, and a warning repeated forever is
                // noise. Silence would be worse — a line that never gets translated has to say
                // why somewhere.
                if (_queue.NoteTooLong(text))
                    Adapter?.LogWarning($"[Queue] Text too long ({text.Length} chars, limit {MaxAITextLength}), left untranslated");
                return false;
            }
            // Last line of defence, here rather than only at the call sites: this is the single door
            // into the queue, and guarding the two obvious callers still let target-language text
            // through by other routes (a stored entry whose translation was already indexed came back
            // and was translated again, drifting). Own UI is exempt: its labels are source text we
            // produce ourselves, never a read-back of the game's rendering — and its own submitters
            // already refuse a label the interface file knows (see IsOwnUITextKnown).
            //
            // ⚠ The game's index is the one asked, and it is now the game's ALONE: it used to hold
            // the interface's translations too, so a label of ours could declare a game text
            // "already in the target language" and keep it out of the file for good.
            if (!isOwnUI && IsAlreadyTargetText(text)) return false;

            _queue.Submit(text, component, isOwnUI, out bool isNew, out int queueSize);

            if (isNew && (DebugMode || Config.debug_ai))
            {
                string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                LogDebug($"[Queue] #{queueSize}: {preview}{(isOwnUI ? " (UI)" : "")}");
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
                // The GAME's index: this path has no component and no own-UI notion — it serves the
                // localization fallback, which only ever sees a game's own types.
                // Index straight away: the read-back happens within the same session, often within
                // the same frame, so waiting for the next cache load would miss the whole point.
                IndexTranslatedValue(text, result, ownUi: false);
            }
            return result;
        }

        /// <summary>
        /// The game's variables as <see cref="TextGate"/> asks for them. A thin face over the static
        /// <see cref="VariableManager"/>: no resolution here, only the two string transformations.
        /// </summary>
        private sealed class GameVariables : IVariableSubstitution
        {
            public static readonly GameVariables Instance = new GameVariables();
            public bool HasVariables => VariableManager.HasVariables;
            public string Extract(string text, out List<KeyValuePair<int, string>> extracted) => VariableManager.ExtractVariables(text, out extracted);
            public string Restore(string text, List<KeyValuePair<int, string>> extracted) => VariableManager.RestoreVariables(text, extracted);
        }

        /// <summary>What the worker needs to know about this moment, read once per item.</summary>
        private static WorkerContext WorkerContextNow() => new WorkerContext
        {
            CaptureOnly = Config.capture_keys_only,
            NormalizeNumbers = Config.normalize_numbers,
            Debug = Config.debug_ai,
            Backend = Config.translation_backend,
            RateLimitRetryDelay = Config.rate_limit_retry_delay,
            Variables = GameVariables.Instance,
            Cache = TranslationCache,
            Queue = _queue,
        };

        /// <summary>
        /// The worker's host on this runtime — see <see cref="IWorkerHost"/>. The backend is chosen
        /// here, the rate-limit flag the backends raise is read and reset here, and every effect a
        /// verdict calls for lands here.
        /// </summary>
        private sealed class WorkerHost : IWorkerHost
        {
            public static readonly WorkerHost Instance = new WorkerHost();

            public string Translate(string normalized, List<string> numbers, bool ownUi, out bool rateLimited)
            {
                string backend = Config.translation_backend;
                string answer = (backend == "google" || backend == "deepl")
                    ? TranslateWithAPI(normalized, numbers)
                    : TranslateWithAI(normalized, numbers, ownUi);   // LLM backend (default)
                rateLimited = answer == null && _apiRateLimited;
                if (rateLimited) _apiRateLimited = false;
                return answer;
            }

            public void Store(string key, string value, string tag) => AddToCache(key, value, tag);

            public void Notify(string original, string shown, List<object> targets)
                => OnTranslationComplete?.Invoke(original, shown, targets);

            public void Backoff(float seconds)
            {
                // In small increments to respond to shutdown
                int delayMs = (int)(seconds * 1000);
                for (int i = 0; i < delayMs && !ShuttingDown; i += 100)
                    Thread.Sleep(Math.Min(100, delayMs - i));
            }

            public void Debug(string line) => Adapter?.LogInfo(line);
            public void Info(string line) => Adapter?.LogInfo(line);
            public void Warn(string line) => Adapter?.LogWarning(line);
        }

        public static string TranslateSingleText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // 🔴 The same ladder as the tracking path — exact, normalized, trimmed, pattern — in
            // Engine/TextGate.cs, where its order is held by cases. This door has no component and
            // no own-UI notion: it serves the localization fallback, which only ever sees the game.
            // (Until 2026-09-11 it carried its own copy, which had drifted; see the gate's remarks.)
            var look = TextGate.Lookup(text, isOwnUI: false, TranslationCache, Config.normalize_numbers, GameVariables.Instance, TryPatternMatch);
            switch (look.Outcome)
            {
                case GateOutcome.Hit:
                    if (look.Stage != GateStage.Pattern) cacheHitCount++;
                    translatedCount++;
                    return look.Value;
                case GateOutcome.Known:
                    cacheHitCount++;
                    return text;
            }

            // The same miss path as the tracking door, with no component: nothing to hide,
            // nothing to reveal — see TextGate.ResolveMiss for the order.
            var miss = TextGate.ResolveMiss(text, isOwnUI: false, normalizedText: look.NormalizedText,
                gateOpen: Config.IsTranslationEnabled || Config.capture_keys_only,
                skipTypewriting: false, skipQueueing: false,
                readback: _readback, stale: _stale, current: TranslationCache, normalizeNumbers: Config.normalize_numbers,
                host: GateHost.Instance, component: null);
            switch (miss.Kind)
            {
                case MissKind.AlreadyTarget:
                    skippedAlreadyTranslated++;
                    return text;
                case MissKind.Refreshed:
                    translatedCount++;
                    return miss.NewText;
                case MissKind.Gone:
                    _readback.MarkTarget(miss.TrimmedNormalized, ownUi: false);
                    return text;
                case MissKind.Retry:
                    // RefreshOnMiss is throttled to once per frame, so the recursion cannot loop.
                    return TranslateSingleText(text);
                case MissKind.Queue:
                    QueueForTranslation(text);
                    return text;
                default:
                    return text;
            }
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
                firstSubmission = _queue.NoteOwnUiSubmitted(englishText);
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

            if (ModUiCache.ContainsKey(key)) return true;
            string trimmed = key.Trim();
            return trimmed != key && ModUiCache.ContainsKey(trimmed);
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

                // Into the index of the side this text came from — see ReadbackIndex.
                // Index straight away: the read-back happens within the same session, often within
                // the same frame, so waiting for the next cache load would miss the whole point.
                IndexTranslatedValue(text, result, isOwnUI);
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

            // 🔴 Tell a reveal in flight what this component now shows, BEFORE any lookup can
            // answer and return. Every exit below means "this text is known", and each one that
            // forgot to say so left the reveal holding a fragment it then sent to the model — see
            // TranslatorPatches.NoteTextSeen for what that cost, measured.
            if (component is Component seenComp)
                TranslatorPatches.NoteTextSeen(TypeHelper.GetInstanceID(seenComp), text);

            // 🔴 A template the game expands in place is never written back, whatever the cache
            // holds. This is what makes the rule deterministic — the withdrawal from the queue is
            // only the best case — and what keeps a file polluted before the rule from breaking the
            // game's own expansion. Nothing is deleted; the line simply stops reaching the screen.
            //
            // ⚠ Before every lookup, since it is a lookup ANSWERING that does the damage. The
            // Count == 0 test inside costs nothing on the games that never do this.
            if (IsExpandedInPlace(text))
                return text;

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

            // 🔴 Which file this text is looked up in, decided once and used for every lookup
            // below. The mod's own labels and the game's text never see each other's entries —
            // that is the whole separation, and asking the same question three times is how a
            // branch ends up asking a fourth way.
            var store = isOwnUI ? ModUiCache : TranslationCache;

            // 🔴 The ladder — exact, normalized, trimmed, pattern — lives in Engine/TextGate.cs,
            // where its order is held by cases. What follows is what a verdict DOES on this host:
            // the counters, the bounded debug lines, and the tracking a hit needs.
            var look = TextGate.Lookup(text, isOwnUI, store, Config.normalize_numbers, GameVariables.Instance, TryPatternMatch);

            if (look.Outcome == GateOutcome.Hit)
            {
                if (look.Stage != GateStage.Pattern) cacheHitCount++;
                translatedCount++;

                if (look.Stage == GateStage.Exact)
                {
                    // ⚠ The reveal was told at the top of this method, for every exit at once — this
                    // used to be said HERE, on the exact-key hit alone, which is the defect.

                    // The canary for the defect NoteTextSeen was written for: a text recognised on
                    // a component whose reveal is still in flight. It is normal — recognition
                    // arrives before the last character, since the numbers are lifted out — and it
                    // is only harmless because the reveal was told at the top of this method.
                    if (_dbgTwCacheHit < 20 && component is Component twComp)
                    {
                        int twId = TypeHelper.GetInstanceID(twComp);
                        if (TranslatorPatches.IsInTypewritingState(twId))
                        {
                            _dbgTwCacheHit++;
                            LogDebug($"[TW-CACHEHIT] comp={twId} text='{(text.Length > 40 ? text.Substring(0,40) : text)}' → known while a reveal is in flight");
                        }
                    }
                    if (DebugMode && text.Length > 100)
                    {
                        int cId = (component is Component dc) ? TypeHelper.GetInstanceID(dc) : -1;
                        LogDebug($"[CACHE-HIT-LONG] comp={cId}\n  key({text.Length}c)='{text}'\n  val({look.Value.Length}c)='{look.Value}'");
                    }
                }
                else if (look.Stage == GateStage.Normalized)
                {
                    // 🔴 **Said a few times, then not again.** This dumps the whole text TWICE —
                    // original and normalised, newlines and markup included — on every cache hit
                    // over a hundred characters. On a game whose long tooltips are on screen
                    // continuously that is 780 dumps in one session: a log nobody can read, in
                    // which a real warning is invisible, written by the thing being diagnosed.
                    //
                    // ⚠ Bounded like [TW-TOUCH] beside it rather than removed: what it shows —
                    // which text produced which key — is exactly what a normalisation defect looks
                    // like, and it is worth seeing once.
                    if (DebugMode && text.Length > 100 && _dbgCacheHitNormLog < 10)
                    {
                        _dbgCacheHitNormLog++;
                        int cId = (component is Component dc3) ? TypeHelper.GetInstanceID(dc3) : -1;
                        LogDebug($"[CACHE-HIT-NORM] comp={cId} orig({text.Length}c) norm→key({look.NormalizedText.Length}c)\n  orig='{text}'\n  norm='{look.NormalizedText}'");
                    }
                }

                // Return it synchronously — this prevents the game from reading back translated
                // text and appending to it. Store the original for this component (enables the
                // runtime toggle restoration) and track the pair.
                if (component != null)
                {
                    TranslatorScanner.StoreOriginalText(component, text);
                    TranslatorPatches.TrackTranslation(TypeHelper.GetInstanceID(component), text, look.Value);
                }
                return look.Value;
            }

            if (look.Outcome == GateOutcome.Known)
            {
                // Nothing in it (a capture, or a key nobody filled in), S, or key == value:
                // the source is what to show, and nothing is queued.
                cacheHitCount++;
                if (look.Stage == GateStage.Exact && DebugMode && text.Length > 100)
                {
                    int cId = (component is Component dc2) ? TypeHelper.GetInstanceID(dc2) : -1;
                    LogDebug($"[CACHE-HIT-SAME] comp={cId} known as shown ({text.Length}c)='{text}'");
                }
                return text;
            }

            // 🔴 The miss path — reverse index, own UI, stale snapshot, visibility, reveal, concat,
            // variables, queue — is TextGate.ResolveMiss, where its order is held by cases. What
            // follows is what each verdict DOES on this host.
            var miss = TextGate.ResolveMiss(text, isOwnUI, look.NormalizedText,
                gateOpen: Config.IsTranslationEnabled || Config.capture_keys_only,
                skipTypewriting: skipTypewriting, skipQueueing: skipQueueing,
                readback: _readback, stale: _stale, current: TranslationCache, normalizeNumbers: Config.normalize_numbers,
                host: GateHost.Instance, component: component);
            switch (miss.Kind)
            {
                case MissKind.AlreadyTarget:
                    skippedAlreadyTranslated++;
                    // This component displays an ALREADY-translated string (e.g. a title's shadow/
                    // duplicate layer copied from the main layer) and so never had its source stored —
                    // without it, disabling translation can't revert it (issue #21). Back-fill the
                    // original from the reverse cache so restore works. Guard on "no original yet" to
                    // keep the O(cache) scan to once per such component; StoreOriginalText also no-ops
                    // if an original is already tracked.
                    if (component != null && TranslatorScanner.GetOriginalText(component) == null)
                    {
                        string src = GetSourceForTranslation(text, store);
                        if (!string.IsNullOrEmpty(src) && src != text)
                            TranslatorScanner.StoreOriginalText(component, src);
                    }
                    return text;

                case MissKind.Refreshed:
                    if (component != null)
                    {
                        TranslatorScanner.StoreOriginalText(component, miss.OriginalText);
                        TranslatorPatches.TrackTranslation(TypeHelper.GetInstanceID(component), miss.OriginalText, miss.NewText);
                    }
                    translatedCount++;
                    return miss.NewText;

                case MissKind.Gone:
                    // ⚠ The GAME's index: the stale snapshot is taken from the game's cache before a
                    // reload, and own-UI text returns before ever reaching this point.
                    _readback.MarkTarget(miss.TrimmedNormalized, ownUi: false);
                    return text;

                case MissKind.Retry:
                    NoteReverseMiss(text, miss.TrimmedNormalized);
                    return TranslateSingleTextWithTracking(text, component, isOwnUI, skipTypewriting, skipQueueing);

                case MissKind.Queue:
                    NoteReverseMiss(text, miss.TrimmedNormalized);
                    QueueForTranslation(text, component, isOwnUI);
                    return text;

                default:
                    // Closed, OwnUiUnknown, HeldHidden, HeldRevealing, NotQueued: the text as shown.
                    return text;
            }
        }

        /// <summary>
        /// DEBUG LOG: a text with Latin letters that reached the queue without the reverse index
        /// recognising it — after every skip check, so only texts actually queued are named.
        /// </summary>
        private static void NoteReverseMiss(string text, string trimmedNormalized)
        {
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
        }

        /// <summary>
        /// The three questions of the miss path only this host can answer — see
        /// <see cref="ITextGateHost"/>. One instance for every call: the component travels as the
        /// opaque object every caller already holds, so a miss allocates nothing here.
        /// </summary>
        private sealed class GateHost : ITextGateHost
        {
            public static readonly GateHost Instance = new GateHost();

            public bool IsHiddenWhileRevealing(object component)
            {
                if (!(component is Component visComp)) return false;
                try
                {
                    if (visComp.gameObject != null && !visComp.gameObject.activeInHierarchy)
                        return TranslatorPatches.IsInTypewritingState(TypeHelper.GetInstanceID(visComp));
                }
                catch { }
                return false;
            }

            public bool IsRevealInProgress(object component, string text)
            {
                int compId = (component is Component comp) ? TypeHelper.GetInstanceID(comp) : -1;
                return TranslatorPatches.IsTypewritingInProgress(compId, text, component);
            }

            public bool RefreshVariables() => VariableManager.RefreshOnMiss();
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
            _queue.ForgetAllRefused();

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

                    // 🔴 **What this translation IS, written into it.** A uuid has a source and a
                    // target; they settle at the first line and publishing freezes them. Until
                    // now only config.json held them — a preference standing in for a fact — so
                    // restoring a backup restored lines without their language, and a file copied
                    // anywhere arrived anonymous. See TranslationLanguages.
                    //
                    // ⚠ Only when SETTLED: "auto" is a mode, not a language, and writing it would
                    // make the file claim an answer nobody gave.
                    //
                    // ⚠ Underscore keys are excluded from the content hash on both sides
                    // (ContentHash.Of, Translation::hashFile), so this cannot make a single
                    // installed mod believe the server moved. A mod too old to know the key drops
                    // it on its next save — a loss of credit, never a breakage, exactly like
                    // _forked_from.
                    if (Languages.IsSettled(FileSourceLanguage))
                        output["_source_language"] = FileSourceLanguage;
                    if (Languages.IsSettled(FileTargetLanguage))
                        output["_target_language"] = FileTargetLanguage;

                    if (CurrentGame != null)
                    {
                        output["_game"] = new JObject
                        {
                            ["name"] = CurrentGame.name,
                            ["steam_id"] = CurrentGame.steam_id
                        };
                    }

                    // _source (hash, main_hash, site_id), _forked_from, _local_changes — the stamps
                    // as the store holds them, recounted just above.
                    Store.WriteStampsInto(output);

                    if (MetadataDirty)
                    {
                        output["_metadata_dirty"] = true;
                    }

                    // Settings sections, built by the same code that reads and
                    // replaces them (see the "Settings sections" region). An
                    // empty section is omitted: its absence means "nothing set".
                    foreach (var section in SettingsSections.All)
                    {
                        var token = BuildSettingsSection(section);
                        if (token != null)
                        {
                            output[SettingsSections.JsonKey(section)] = token;
                        }
                    }

                    // The lines, sorted, in the shape reading gives back — the two are written
                    // together in Engine/TranslationFileEntries so the round trip can be checked.
                    TranslationFileEntries.WriteInto(output, TranslationCache);

                    string json = output.ToString(Formatting.Indented);
                    File.WriteAllText(CachePath, json);
                    cacheModified = false;

                    if (DebugMode)
                        Adapter?.LogInfo($"Saved {TranslationCache.Count} cache entries with UUID: {FileUuid}");
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

            // The moment is the store's (TranslationStore.Fork): where the work came from is
            // written down before the reset wipes the sync state; the count is what was actually
            // received, measured now because the original goes on growing; then a new uuid, no
            // server, no Main, every line local, and both ancestor files deleted. The fingerprint
            // ignores the uuid, which is the whole point — it is taken here because this is the
            // last instant the cache holds exactly what was copied.
            try
            {
                int dropped = Store.Fork(Guid.NewGuid().ToString(), CountResolvedEntries(),
                                         ComputeContentFingerprint(), TranslationCache.Count);
                LogDebug($"[Fork] Dropped {dropped} ancestor file(s)");
            }
            catch (Exception e)
            {
                // A file that would not go stays to be reloaded at the next launch — said loudly,
                // because that is exactly the state the fork exists to prevent.
                Adapter?.LogWarning($"Failed to drop the ancestors after forking: {e.Message}");
            }
            AncestorSettings = null;

            // Reset server state - we're starting fresh
            ServerState = new ServerTranslationState();

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

    // ⚠ The mod's own `TranslationRole` enum lived here until 2026-09-07. It is the socle's
    // LineageRole now — same members, one meaning: this account's row, None when there is none.


    // TranslationEntry moved to TranslationEntry.cs — it has to be reachable from the checks
    // project, and this file references UnityEngine.

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
        /// The studio Unity records beside it — `Application.companyName`.
        ///
        /// 🔴 **It is what turns a weak product name into an identity.** A game calling itself
        /// "Game" identifies nothing on its own; with the studio beside it, two machines looking at
        /// the same title agree without anybody typing anything. The site keeps the pair and
        /// resolves lookups with it, so a translation published from here stays findable from
        /// another install whatever the folder is called.
        /// </summary>
        public string company_name { get; set; }

        /// <summary>
        /// How the steam_id was detected: "steam_appid.txt", "appmanifest", or null if not detected
        /// </summary>
        public string detection_method { get; set; }
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
