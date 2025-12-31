# Universal Unity Game Translator (Alpha)

**Website:** [unitygametranslator.asymptomatikgames.com](https://unitygametranslator.asymptomatikgames.com)

A universal translation mod for Unity games with local AI (Ollama) and online community translations.

## Features

### Translation Engine
- **Runtime translation** - text is translated as you encounter it in-game
- **Local AI translation** via Ollama (no internet required, no API costs)
- **Instant cache hits** - cached translations apply synchronously
- **Number normalization** - "Kill 5 enemies" and "Kill 10 enemies" share the same translation
- **Auto language detection** - detects system language as target
- **Cross-platform** - works on Windows, macOS, Linux

### Online Sync
- **Community translations** - download translations from the community website
- **Automatic game detection** - detects game via Steam ID or product name
- **Update notifications** - get notified when translations are updated
- **Mod update checker** - get notified when a new mod version is available on GitHub
- **Upload your work** - share translations with the community
- **3-way merge** - intelligently merge updates while keeping your local changes
- **Device Flow login** - secure authentication without entering passwords in-game

### In-Game Overlay
- **Settings hotkey** - press F10 (configurable) to open settings
- **First-run wizard** - guided setup on first launch
- **Ollama configuration** - test connection, select model, set game context
- **Sync options** - configure update checking and merge behavior
- **Translation info** - view current translation source and local changes
- **Login/Upload** - authenticate and upload translations without leaving the game

> **Note:** Only text displayed during gameplay is translated. Play through the game to build the translation cache.

## Installation

### 1. Install a mod loader

| Mod Loader | Unity Type | Download |
|------------|------------|----------|
| BepInEx 5 | Mono | [GitHub](https://github.com/BepInEx/BepInEx/releases) |
| BepInEx 6 | Mono or IL2CPP | [Bleeding Edge](https://builds.bepinex.dev/projects/bepinex_be) |
| MelonLoader | Mono or IL2CPP | [GitHub](https://github.com/LavaGang/MelonLoader/releases) |

> **BepInEx 6** is in beta but supports both Mono and IL2CPP games. Use the version matching your game type.

> **Cross-platform:** UnityGameTranslator DLLs are .NET assemblies that work on Windows, macOS, and Linux. The same release package works on all platforms. Install the mod loader version matching your OS and architecture, then use the same UnityGameTranslator plugin.

**How to know your game type:**
- `GameAssembly.dll` in game folder → **IL2CPP**
- `<Game>_Data/Managed/Assembly-CSharp.dll` → **Mono**

### 2. Install UnityGameTranslator

**First run:** Launch the game once with the mod loader installed, then quit. This creates the required folder structure for plugins.

- [BepInEx installation guide](https://docs.bepinex.dev/articles/user_guide/installation/index.html)
- [MelonLoader installation guide](https://melonwiki.xyz/#/?id=requirements)

Download the release matching your mod loader and extract to:

| Mod Loader | Extract to |
|------------|------------|
| BepInEx | `<Game>/BepInEx/plugins/UnityGameTranslator/` |
| MelonLoader | `<Game>/Mods/` |

The zip contains:
- `UnityGameTranslator.dll` - main plugin
- `UnityGameTranslator.Core.dll` - translation engine
- `UniverseLib.*.dll` - UI framework (variant depends on mod loader)
- `Newtonsoft.Json.dll` - JSON library
- (BepInEx 5 only) `System.Buffers.dll`, `System.Memory.dll`, etc. - .NET Standard polyfills

### 3. First Launch

On first launch, the mod displays a setup wizard:
1. **Welcome screen** - introduction to the mod
2. **Online mode** - choose to enable community features or stay offline
3. **Settings hotkey** - pick a key to open settings (default: F10)
4. **Translation search** - if online, search for existing translations
5. **Ollama setup** - configure local AI translation (optional)

After setup, press the hotkey anytime to open settings.

### 4. Enable AI translation (optional)

By default, the plugin only uses cached/downloaded translations. To enable live AI translation:

1. Install Ollama: https://ollama.ai
2. Download a model:
   ```
   ollama pull qwen3:8b
   ```
3. Open settings (F10) and enable Ollama, or edit `config.json`:
   ```json
   "enable_ollama": true
   ```

> **Recommended model:** `qwen3:8b` is the tested and optimized model (requires ~6-8 GB VRAM). It provides the best balance of speed, quality, and multilingual support. Other models may work but are experimental.

## Configuration

Config file location:
- BepInEx: `<Game>/BepInEx/plugins/UnityGameTranslator/config.json`
- MelonLoader: `<Game>/UserData/UnityGameTranslator/config.json`

```json
{
  "ollama_url": "http://localhost:11434",
  "model": "qwen3:8b",
  "target_language": "auto",
  "source_language": "auto",
  "game_context": "",
  "enable_ollama": false,
  "normalize_numbers": true,
  "preload_model": true,
  "debug_ollama": false,

  "settings_hotkey": "F10",
  "first_run_completed": true,
  "online_mode": true,

  "api_token": null,
  "api_user": null,

  "sync": {
    "check_update_on_start": true,
    "auto_download": false,
    "notify_updates": true,
    "check_mod_updates": true,
    "merge_strategy": "ask",
    "ignored_uuids": []
  }
}
```

### Translation Options

| Option | Description |
|--------|-------------|
| `target_language` | Target language (`"auto"` = system language, or `"French"`, `"German"`, etc.) |
| `source_language` | Source language (`"auto"` = let AI detect) |
| `game_context` | Game description for better translations (e.g., `"Medieval fantasy RPG"`) |
| `enable_ollama` | `true` to enable live AI translation |
| `model` | Ollama model (`qwen3:8b` recommended, other models are experimental) |
| `normalize_numbers` | `true` to replace numbers with placeholders for better cache reuse |
| `debug_ollama` | `true` to log detailed Ollama requests/responses |

### UI Options

| Option | Description |
|--------|-------------|
| `settings_hotkey` | Key to open settings overlay (default: `F10`) |
| `first_run_completed` | `true` after completing the setup wizard |
| `online_mode` | `true` to enable community features (sync, upload) |

### Sync Options

| Option | Description |
|--------|-------------|
| `check_update_on_start` | Check for translation updates when game starts |
| `auto_download` | Automatically download updates (if no conflicts) |
| `notify_updates` | Show notification when updates are available |
| `check_mod_updates` | Check for new mod versions on GitHub at startup |
| `merge_strategy` | How to handle updates: `"ask"`, `"merge"`, or `"replace"` |
| `ignored_uuids` | List of translation UUIDs to ignore updates for |

### Authentication

| Option | Description |
|--------|-------------|
| `api_token` | API token for authenticated actions (set via login flow) |
| `api_user` | Username of logged-in user |

## Community Features

### Downloading Translations

1. Press F10 to open settings
2. Click "Search translations" or enable "Check updates on start"
3. The mod searches for translations matching your game and language
4. Select a translation to download

### Uploading Translations

1. Press F10 to open settings
2. Click "Login" to authenticate via the website
3. Enter the displayed code at the website
4. Once logged in, click "Upload" to share your translation

### Collaboration System (Main/Branch/Fork)

UnityGameTranslator uses a **Main/Branch** model for collaborative translation:

#### Terminology

| Term | Description |
|------|-------------|
| **Main** | The original translation. First uploader becomes the Main owner. |
| **Branch** | A contributor's version, linked to the Main. Each contributor has one Branch per UUID. |
| **Fork** | The action of copying a translation to create your own Branch. |

#### Roles

| Role | Description |
|------|-------------|
| **Main owner** | You created this translation. You can update it and merge contributions from Branches. |
| **Branch contributor** | You downloaded a translation, improved it, and uploaded your changes as a Branch. |
| **None** | You haven't uploaded yet. Your first upload will create a Main or Branch. |

#### How it works

1. **First upload** → Your translation becomes the **Main** for that UUID
2. **Download + modify + upload** → You **fork** the translation, creating your **Branch**
3. **Main owner** can view all Branches on the website and merge contributions
4. **Languages are locked** after first upload - source/target cannot be changed

#### Upload behavior

| Situation | Result |
|-----------|--------|
| UUID doesn't exist on server | Creates **new Main** translation |
| UUID exists, you're the Main owner | **Updates** your Main translation |
| UUID exists, owned by someone else | **Forks** it → creates your **Branch** |

#### In the mod

- Your role is shown in the main panel: "Main translation" or "Branch of @username"
- Branch count is displayed if you're the Main owner
- Languages become read-only in Options after your first upload

### Merging Updates

When a translation you downloaded has updates:
- **Merge** - keeps your local changes + adds new entries from remote
- **Replace** - overwrites everything with the new version
- **Ignore** - keep your version, don't ask again for this translation

The mod uses 3-way merge logic to intelligently combine changes.

### Translation Quality System (H/V/A Tags)

Each translation entry has a **quality tag** indicating how it was created:

| Tag | Name | Description | Score Weight |
|-----|------|-------------|--------------|
| **H** | Human | Manually written by a human | 3 points |
| **V** | Validated | AI translation reviewed and approved by human | 2 points |
| **A** | AI | Automatically translated by Ollama | 1 point |

> **Note:** Entries with tag `H` but empty value are displayed as "C" (Capture) in stats and count as 0 points until translated.

**Additional tags (excluded from quality score):**

| Tag | Name | Description |
|-----|------|-------------|
| **S** | Skip | Text intentionally not translated (e.g., alien language, Klingon, foreign text meant to stay foreign). AI decides when to skip. *Experimental.* |
| **M** | Mod | Mod UI translations (settings panel, buttons). *Optional, internal use.* |

#### Quality Score (0-3 scale)

The quality score is calculated from the H/V/A distribution:

```
Score = (H×3 + V×2 + A×1) / (H + V + A)
```

| Score | Label | Meaning |
|-------|-------|---------|
| 2.5 - 3.0 | **Excellent** | Mostly human translations |
| 2.0 - 2.5 | **Good** | Mix of human and validated |
| 1.5 - 2.0 | **Fair** | Balanced mix |
| 1.0 - 1.5 | **Basic** | Mostly AI with some review |
| 0.0 - 1.0 | **Raw AI** | Unreviewed AI translations |

#### How tags are assigned

- **Capture mode**: Text saved with empty value, tag `H` (shows as `C` in stats until translated)
- **AI translation**: Ollama translates → tag `A`
- **Manual edit**: User edits a translation in-game or on website → tag `H`
- **Validation**: User approves AI translation on website → tag `V`

#### Capture Keys Only Mode (100% Human Translation)

For translators who want full control without AI:

1. Open Settings (F10) → enable **"Capture keys only"**
2. Play through the game - all text is captured but NOT translated
3. Upload the captured file to the website
4. Edit translations manually on the website → all entries become `H` (Human)
5. Result: 100% human translation with **Excellent** quality score

This workflow is ideal for:
- Professional translators who don't want AI interference
- Languages not well supported by AI
- Games requiring specific terminology or style

#### Editing on the website

Translation owners can edit their translations directly on the website:
- **Edit** any entry → changes tag to `H` (Human)
- **Validate** AI entries → changes tag to `V` (Validated)
- Changes sync back to the mod on next download/update check

#### In the mod UI

- Translation list shows: `H:150 V:50 A:300 (Fair)`
- Status card shows visual H/V/A bar with colors
- Quality score displayed as `1.8/3 (Fair)`

### Manual Sharing

Translation caches are stored in `translations.json` in the plugin folder:
- BepInEx: `<Game>/BepInEx/plugins/UnityGameTranslator/translations.json`
- MelonLoader: `<Game>/UserData/UnityGameTranslator/translations.json`

Each file contains metadata for tracking:
```json
{
  "_uuid": "unique-file-id",
  "_game": { "name": "Game Name", "steam_id": "12345" },
  "_source": { "site_id": 123, "uploader": "user", "hash": "sha256:..." },
  "_local_changes": 42,

  "Hello": "Bonjour",
  "Play": "Jouer"
}
```

---

## Building from source

### Prerequisites

- .NET SDK 6.0+

### Setup extlibs

Create `extlibs/` folder with required DLLs:

```
extlibs/
├── Unity/
│   ├── UnityEngine.dll
│   ├── UnityEngine.CoreModule.dll
│   ├── UnityEngine.UI.dll
│   ├── UnityEngine.IMGUIModule.dll
│   ├── UnityEngine.TextRenderingModule.dll
│   └── Unity.TextMeshPro.dll
├── UniverseLib/
│   └── UniverseLib.Mono.dll            # Compile-time reference for Core
├── BepInEx5/
│   ├── BepInEx.dll
│   ├── 0Harmony.dll
│   └── UniverseLib.Mono.dll
├── BepInEx6-Mono/
│   ├── BepInEx.Core.dll
│   ├── BepInEx.Unity.Mono.dll
│   ├── 0Harmony.dll
│   └── UniverseLib.Mono.dll
├── BepInEx6-IL2CPP/
│   ├── BepInEx.Core.dll
│   ├── BepInEx.Unity.IL2CPP.dll
│   ├── Il2CppInterop.Runtime.dll
│   ├── 0Harmony.dll
│   └── UniverseLib.BIE.IL2CPP.Interop.dll
├── MelonLoader-Mono/
│   ├── MelonLoader.dll
│   ├── 0Harmony.dll
│   └── UniverseLib.Mono.dll
└── MelonLoader-IL2CPP/
    ├── MelonLoader.dll
    ├── 0Harmony.dll
    ├── Il2CppInterop.Runtime.dll
    └── UniverseLib.ML.IL2CPP.Interop.dll
```

> **UniverseLib:** Build from the `UniverseLib/` submodule or download pre-built DLLs from the [yukieiji fork releases](https://github.com/yukieiji/UniverseLib/releases).

**Sources:**
- BepInEx 5: [Releases](https://github.com/BepInEx/BepInEx/releases) → `BepInEx/core/`
- BepInEx 6: [Bleeding Edge](https://builds.bepinex.dev/projects/bepinex_be) → `BepInEx/core/`
- MelonLoader: [Releases](https://github.com/LavaGang/MelonLoader/releases) → `MelonLoader/net6/`
- Unity: Any Unity game → `<Game>_Data/Managed/`

### Build

```bash
./prepare-release.ps1
```

This script builds all projects and creates release zips in `releases/`.

Or build individually:
```bash
dotnet build UnityGameTranslator-BepInEx5/UnityGameTranslator.BepInEx5.csproj -c Release
dotnet build UnityGameTranslator-BepInEx6-Mono/UnityGameTranslator.BepInEx6Mono.csproj -c Release
dotnet build UnityGameTranslator-BepInEx6-IL2CPP/UnityGameTranslator.BepInEx6IL2CPP.csproj -c Release
dotnet build UnityGameTranslator-MelonLoader-Mono/UnityGameTranslator.MelonLoaderMono.csproj -c Release
dotnet build UnityGameTranslator-MelonLoader-IL2CPP/UnityGameTranslator.MelonLoaderIL2CPP.csproj -c Release
```

Output DLLs are in each project's `bin/` folder.

### Versioning

The version is centralized in `Directory.Build.props`. Update the `<Version>` tag before release—all projects inherit it automatically.

### Project Structure

```
UnityGameTranslator/
├── UnityGameTranslator.Core/           # Shared translation engine
│   ├── TranslatorCore.cs               # Main logic, config, Ollama API
│   ├── TranslatorPatches.cs            # Harmony patches for text
│   ├── TranslatorScanner.cs            # Scene scanning for UI
│   ├── UI/                             # UniverseLib uGUI overlay system
│   │   ├── TranslatorUIManager.cs      # Panel manager, hotkey handling
│   │   ├── UIStyles.cs                 # Centralized styles and colors
│   │   └── Panels/                     # UI panels
│   │       ├── WizardPanel.cs          # First-run setup wizard
│   │       ├── MainPanel.cs            # Main settings panel
│   │       ├── OptionsPanel.cs         # Configuration options
│   │       ├── LoginPanel.cs           # Device flow authentication
│   │       ├── UploadPanel.cs          # Upload translations
│   │       ├── UploadSetupPanel.cs     # Upload configuration (game, languages)
│   │       ├── MergePanel.cs           # 3-way merge conflict resolution
│   │       ├── LanguagePanel.cs        # Language selection
│   │       ├── ConfirmationPanel.cs    # User confirmations
│   │       └── StatusOverlay.cs        # Corner notifications (updates, sync)
│   ├── ApiClient.cs                    # HTTP client for website API
│   ├── GameDetector.cs                 # Game identification
│   ├── GitHubUpdateChecker.cs          # GitHub releases update checker
│   ├── TranslationMerger.cs            # 3-way merge logic
│   └── TokenProtection.cs              # AES-256 token encryption
├── UniverseLib/                        # Git submodule (yukieiji fork)
├── UnityGameTranslator-BepInEx5/       # BepInEx 5 adapter
├── UnityGameTranslator-BepInEx6-Mono/  # BepInEx 6 Mono adapter
├── UnityGameTranslator-BepInEx6-IL2CPP/# BepInEx 6 IL2CPP adapter
├── UnityGameTranslator-MelonLoader-Mono/   # MelonLoader Mono adapter
├── UnityGameTranslator-MelonLoader-IL2CPP/ # MelonLoader IL2CPP adapter
├── extlibs/                            # External DLLs (Unity, BepInEx, UniverseLib)
├── releases/                           # Build output
└── Directory.Build.props               # Shared version
```

> **UI System:** The mod uses [UniverseLib](https://github.com/yukieiji/UniverseLib) (yukieiji fork) for its overlay UI. This provides a unified uGUI-based interface that works on both Mono and IL2CPP Unity games, avoiding IMGUI crashes on IL2CPP.

---

## Acknowledgments

UnityGameTranslator is built on the shoulders of amazing open-source projects:

- **[UniverseLib](https://github.com/yukieiji/UniverseLib)** by sinai-dev & yukieiji - UI framework for Unity mods
- **[BepInEx](https://github.com/BepInEx/BepInEx)** - Unity plugin framework
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** by LavaGang - Universal Unity mod loader
- **[Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike - Runtime method patching
- **[Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)** by James Newton-King - JSON framework
- **[Ollama](https://ollama.ai/)** - Local AI inference engine

Special thanks to the Unity modding community for documentation, tutorials, and support.

See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for full license details.

---

## License

This project is dual-licensed:

- **Open Source:** [AGPL-3.0](LICENSE) - Free for open source use
- **Commercial:** Contact us for proprietary/commercial use

See [LICENSING.md](LICENSING.md) for details.
