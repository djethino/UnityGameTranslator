# Universal Unity Game Translator (Beta)

**Website:** [unitygametranslator.asymptomatikgames.com](https://unitygametranslator.asymptomatikgames.com)

A universal translation mod for Unity games. Translate using AI (any OpenAI-compatible server — Ollama, LM Studio, Groq, Gemini, OpenAI, and more), Google Translate, DeepL, or download community translations. Works fully offline with a local AI server — no API key, no internet, no cost. Supports all writing systems and any language direction.

## Features

### Translation Engine
- **Runtime translation** — text is translated as you encounter it in-game
- **Multiple backends** — AI (OpenAI-compatible), Google Translate, DeepL, or community downloads only
- **Instant cache hits** — cached translations apply synchronously with zero latency
- **Number normalization** — "Kill 5 enemies" and "Kill 10 enemies" share the same translation
- **Auto language detection** — detects system language as default target
- **Cross-platform** — Windows, macOS, Linux

### Translation Backends

| Backend | Description |
|---------|-------------|
| **AI (LLM)** | Any OpenAI-compatible server — local or cloud (Ollama, LM Studio, Groq, Gemini, OpenAI, OpenRouter, and any other compatible provider) |
| **Google Translate** | Google Cloud Translation API |
| **DeepL** | DeepL API (Free and Pro tiers) |
| **None** | Only use cached/downloaded translations |

### In-Game Text Editor
- Click on any UI element to see all text in that area
- Edit translations directly in-game — saves immediately with Human (H) tag
- Retranslate with AI if the current translation isn't good enough
- Found in **Translation Parameters → Tools → Start Text Editor**

### Font System
- Automatic font detection (TextMeshPro, Unity UI.Text)
- Fallback fonts for any writing system — Latin, CJK, Arabic, Devanagari, Cyrillic, Thai, Hebrew, and more
- Per-font scaling and enable/disable
- **Font overrides by pattern** — override size for specific UI elements (tables, titles, tooltips)
  - Add overrides via inspector click, text search, or manual pattern
  - Supports recursive patterns (`path:**/TablePanel/**`)
  - Changes apply at runtime

### UI Exclusions
- Pattern-based exclusion system for text that shouldn't be translated (chat, player names, etc.)
- Visual inspector to click and exclude elements
- Find by text content search
- Wildcard patterns (`**/ChatPanel/**`, `*/PlayerName`)

### Image/Sprite Replacement
- Replace text embedded in sprites/images with translated versions
- Visual inspector to select and export original images
- Import translated images as replacement
- Sprite metadata (pivot, borders, PPU) preserved

### Dynamic Variables
- Extract player name, item stats, and other dynamic values from game objects
- Placeholder substitution for better cache reuse
- Variable scanner to discover candidates

### Online Community Features
- **Community translations** — download translations from the website
- **Automatic game detection** — via Steam ID, product name, or folder
- **Real-time sync** via SSE (Server-Sent Events) — get updates without restarting
- **3-way merge** — intelligently merge remote updates with your local changes
- **Device Flow login** — secure authentication without entering passwords in-game
- **Upload & share** — share your translations with the community
- **Mod update checker** — notifications when a new mod version is available

### Collaboration System (Main/Branch/Fork)

| Term | Description |
|------|-------------|
| **Main** | The original translation. First uploader becomes the owner. |
| **Branch** | A contributor's version, linked to the Main. One per user per UUID. |
| **Fork** | Copying a translation to create your own Branch. |

**Upload behavior:**

| Situation | Result |
|-----------|--------|
| UUID doesn't exist on server | Creates **new Main** |
| UUID exists, you're the owner | **Updates** your Main |
| UUID exists, owned by someone else | **Forks** → creates your **Branch** |

### Translation Quality System (H/V/A Tags)

| Tag | Name | Score | Description |
|-----|------|-------|-------------|
| **H** | Human | 3 pts | Written by a human |
| **V** | Validated | 2 pts | AI translation approved by human |
| **A** | AI | 1 pt | Translated by AI |
| **S** | Skip | — | Intentionally not translated |
| **M** | Mod | — | Mod UI translations (internal) |

**Quality Score** (0-3): `(H×3 + V×2 + A×1) / (H + V + A)`

**Capture Keys Only mode** — play without translating, capture all text, then translate manually on the website for 100% human translations.

### In-Game Overlay

- **First-run wizard** — guided setup on first launch
- **Settings hotkey** — F10 (configurable) opens the full settings panel
- **Translation info** — H/V/A distribution, quality score, sync status
- **Translation parameters** — tabs for Tools, Exclusions, Fonts (Global + Overrides), Images, Variables
- **Merge panel** — resolve conflicts with per-entry Keep Mine / Take Server choices
- **Status overlay** — corner notifications for updates, sync, and AI queue

> **Note:** Only text displayed during gameplay is translated. Play through the game to build the translation cache.

## Installation

### 1. Install a mod loader

| Mod Loader | Unity Type | Download |
|------------|------------|----------|
| BepInEx 5 | Mono | [GitHub](https://github.com/BepInEx/BepInEx/releases) |
| BepInEx 6 | Mono or IL2CPP | [Bleeding Edge](https://builds.bepinex.dev/projects/bepinex_be) |
| MelonLoader | Mono or IL2CPP | [GitHub](https://github.com/LavaGang/MelonLoader/releases) |

**How to identify your game type:**
- `GameAssembly.dll` in game folder → **IL2CPP**
- `<Game>_Data/Managed/Assembly-CSharp.dll` → **Mono**

> **Cross-platform:** The mod's DLLs are .NET assemblies that work on Windows, macOS, and Linux.

### 2. Install UnityGameTranslator

Download the release matching your mod loader from [GitHub Releases](https://github.com/djethino/UnityGameTranslator/releases) and extract to:

| Mod Loader | Extract DLLs to | User data (config, translations, fonts, images) |
|------------|-----------------|--------------------------------------------------|
| BepInEx | `<Game>/BepInEx/plugins/UnityGameTranslator/` | Same folder as the DLL |
| MelonLoader | `<Game>/Mods/` (DLLs directly, **no subfolder**) | `<Game>/UserData/UnityGameTranslator/` |

> **MelonLoader warning:** Do NOT place the DLLs inside `Mods/UnityGameTranslator/`. MelonLoader only scans the root `Mods/` folder and will not find mods inside subdirectories.

### 3. First Launch

The mod displays a setup wizard:
1. **Online mode** — enable community features or stay offline
2. **Settings hotkey** — pick a key to open settings (default: F10)
3. **Translation search** — search for existing community translations
4. **AI setup** — configure translation backend and model (optional)

### 4. Enable translation backend (optional)

By default, the mod only uses cached/downloaded translations. To enable live translation:

#### AI Translation (OpenAI-compatible API)

The mod works with **any server that exposes the OpenAI-compatible API** (`/v1/chat/completions`). This includes local servers, cloud providers, and an ever-growing list of AI platforms.

**Examples (non-exhaustive):**

| Server | URL to enter | API key |
|--------|-------------|---------|
| [Ollama](https://ollama.ai/) (local) | `http://localhost:11434` | None |
| [LM Studio](https://lmstudio.ai/) (local) | `http://localhost:1234` | None |
| [Groq](https://groq.com/) | `https://api.groq.com/openai` | Required (free tier) |
| [OpenRouter](https://openrouter.ai/) | `https://openrouter.ai/api` | Required (free tier) |
| [OpenAI](https://platform.openai.com/) | `https://api.openai.com` | Required |
| [Google Gemini](https://ai.google.dev/) | `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` | Required (free tier) |

> **URL resolution:** The mod auto-appends `/v1/chat/completions` if the URL doesn't already end with `/completions`. If your provider has a non-standard URL, enter the full path up to `/chat/completions`.

#### Commercial translation APIs

| Provider | Description |
|----------|-------------|
| [Google Translate](https://cloud.google.com/translate) | Cloud Translation API |
| [DeepL](https://www.deepl.com/pro-api) | Free and Pro tiers |

**Setup:** Open settings (F10) → Translation tab → select backend → enter URL/key → Test → Enable.

> **Recommended local model:** `qwen3:8b` — best balance of speed, quality, and multilingual support (~6-8 GB VRAM).

## Configuration

Config file location:
- BepInEx: `<Game>/BepInEx/plugins/UnityGameTranslator/config.json`
- MelonLoader: `<Game>/UserData/UnityGameTranslator/config.json`

Translation cache: `translations.json` in the same folder.

### Key Options

| Option | Description |
|--------|-------------|
| `translation_backend` | `"llm"`, `"google"`, `"deepl"`, or `"none"` |
| `target_language` | `"auto"` (system language) or specific (e.g., `"French"`) |
| `game_context` | Game description for better AI translations (e.g., `"Medieval fantasy RPG"`) |
| `settings_hotkey` | Key to open settings (default: `"F10"`) |
| `online_mode` | Enable community features (sync, upload) |
| `sync.merge_strategy` | `"ask"`, `"merge"`, or `"replace"` |

### External Resources (Fonts & Images)

UnityGameTranslator can use custom fonts and replacement images to improve translation quality — especially useful for languages with characters not supported by the game's default font, or when translating text baked into images (logos, buttons, title screens).

**Where to place external resources:**

| Mod Loader | Fonts folder | Images folder |
|------------|--------------|---------------|
| BepInEx | `<Game>/BepInEx/plugins/UnityGameTranslator/fonts/` | `<Game>/BepInEx/plugins/UnityGameTranslator/images/` |
| MelonLoader | `<Game>/UserData/UnityGameTranslator/fonts/` | `<Game>/UserData/UnityGameTranslator/images/` |

**Custom fonts** — drop `.ttf` or `.otf` files into the `fonts/` folder. The filename (without extension) becomes the font name shown in the Translation Parameters panel. Assign per UI element or globally.

**Replacement images** — drop `.png` files (with transparency) into the `images/` folder. Use the in-game image capture feature to export existing sprites, edit them externally, then save the modified versions back.

External resources are fully optional. The mod works perfectly without them.

### Self-Hosting

Deploy your own [website instance](https://github.com/djethino/UnityGameTranslator-website), then update `Directory.Build.props` before building:

```xml
<ApiBaseUrl>https://your-server.com/api/v1</ApiBaseUrl>
<WebsiteBaseUrl>https://your-server.com</WebsiteBaseUrl>
<SseBaseUrl>https://sse.your-server.com</SseBaseUrl>
```

Or override at runtime in `config.json`:
```json
{
  "api_base_url": "https://your-server.com/api/v1",
  "website_base_url": "https://your-server.com"
}
```

> **Security:** Your API token is sent to the configured server. Only use trusted instances.

## Building from Source

### Prerequisites

- .NET SDK 6.0+
- `extlibs/` folder with Unity, BepInEx, MelonLoader, and UniverseLib DLLs (see project structure)

### Build

```bash
./prepare-release.ps1
```

Creates release zips in `releases/` for all 5 mod loader variants.

### Project Structure

```
UnityGameTranslator/
├── UnityGameTranslator.Core/           # Shared translation engine
│   ├── TranslatorCore.cs               # Main logic, config, translation cache
│   ├── TranslatorPatches.cs            # Harmony patches for text interception
│   ├── TranslatorScanner.cs            # Scene scanning for UI components
│   ├── FontManager.cs                  # Font detection, replacement, scaling, overrides
│   ├── ImageReplacer.cs                # Sprite/image replacement for bitmap text
│   ├── VariableManager.cs              # Dynamic variable extraction
│   ├── ApiClient.cs                    # HTTP client for website API
│   ├── SseClient.cs                    # SSE streaming for real-time sync
│   ├── TranslationMerger.cs            # 3-way merge with tag awareness
│   ├── GameDetector.cs                 # Game identification (Steam ID, product name)
│   ├── GitHubUpdateChecker.cs          # Mod version update checker
│   ├── TokenProtection.cs              # AES-256 token encryption
│   └── UI/                             # UniverseLib uGUI overlay
│       ├── Panels/                     # 12 panels (wizard, settings, merge, inspector...)
│       └── Components/                 # Reusable UI components
├── UniverseLib/                        # Git submodule (yukieiji fork)
├── UnityGameTranslator-BepInEx5/       # BepInEx 5 adapter (Mono)
├── UnityGameTranslator-BepInEx6-Mono/  # BepInEx 6 adapter (Mono)
├── UnityGameTranslator-BepInEx6-IL2CPP/# BepInEx 6 adapter (IL2CPP)
├── UnityGameTranslator-MelonLoader-Mono/    # MelonLoader adapter (Mono)
├── UnityGameTranslator-MelonLoader-IL2CPP/  # MelonLoader adapter (IL2CPP)
└── Directory.Build.props               # Version + API URLs
```

## Acknowledgments

- **[UniverseLib](https://github.com/yukieiji/UniverseLib)** by sinai-dev & yukieiji — UI framework for Unity mods
- **[BepInEx](https://github.com/BepInEx/BepInEx)** — Unity plugin framework
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** by LavaGang — Universal Unity mod loader
- **[Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike — Runtime method patching
- **[Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)** by James Newton-King — JSON framework

See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for full license details.

## License

Dual-licensed:
- **Open Source:** [AGPL-3.0](LICENSE)
- **Commercial:** Contact us for proprietary use

See [LICENSING.md](LICENSING.md) for details.
