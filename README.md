# Universal Unity Game Translator

https://unitygametranslator.asymptomatikgames.com/

A universal translation mod for Unity games using local AI (Ollama).

## Features

- **Runtime translation** - text is translated as you encounter it in-game
- **Local AI translation** via Ollama (no internet required, no API costs)
- **Instant cache hits** - cached translations apply synchronously
- **Number normalization** - "Kill 5 enemies" and "Kill 10 enemies" share the same translation
- **Translation queue overlay** - shows progress when Ollama is translating (top-right corner)
- **Auto language detection** - detects system language as target
- **Cross-platform** - works on Windows, macOS, Linux

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
- `Newtonsoft.Json.dll` - JSON library
- `config.json` - default configuration

### 3. Enable AI translation (optional)

By default, the plugin only uses cached translations. To enable live AI translation:

1. Install Ollama: https://ollama.ai
2. Download a model:
   ```
   ollama pull qwen3:8b
   ```
3. Edit `config.json` and set:
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
  "debug_ollama": false
}
```

| Option | Description |
|--------|-------------|
| `target_language` | Target language (`"auto"` = system language, or `"French"`, `"German"`, etc.) |
| `source_language` | Source language (`"auto"` = let AI detect) |
| `game_context` | Game description for better translations (e.g., `"Medieval fantasy RPG"`) |
| `enable_ollama` | `true` to enable live AI translation |
| `model` | Ollama model (`qwen3:8b` recommended, other models are experimental) |
| `normalize_numbers` | `true` to replace numbers with placeholders for better cache reuse |
| `debug_ollama` | `true` to log detailed Ollama requests/responses |

## Sharing translations

### Community website

Share and download translation files on the official community platform:

**[unitygametranslator.asymptomatikgames.com](https://unitygametranslator.asymptomatikgames.com)**

- Browse existing translations by game and language
- Upload your translation files to help others
- Fork and improve existing translations
- Automatic lineage tracking via file UUID

### Manual sharing

Translation caches are stored in `translations.json` in the plugin folder:
- BepInEx: `<Game>/BepInEx/plugins/UnityGameTranslator/translations.json`
- MelonLoader: `<Game>/UserData/UnityGameTranslator/translations.json`

To use a shared translation file, copy `translations.json` to your plugin folder. Make sure your game language matches the source language of the translation (e.g., for an English→French file, set your game to English).

Each `translations.json` file contains a unique `_uuid` that tracks its lineage. When you upload a file to the website, forks are automatically detected and linked to the original.

---

## Building from source

### Prerequisites

- .NET SDK 6.0+

### Setup extlibs

Create `extlibs/` folder with required DLLs:

```
extlibs/
├── BepInEx5/
│   ├── BepInEx.dll
│   └── 0Harmony.dll
├── BepInEx6-IL2CPP/
│   ├── BepInEx.Core.dll
│   ├── BepInEx.Unity.IL2CPP.dll
│   ├── Il2CppInterop.Runtime.dll
│   └── 0Harmony.dll
├── BepInEx6-Mono/
│   ├── BepInEx.Core.dll
│   ├── BepInEx.Unity.Mono.dll
│   └── 0Harmony.dll
├── MelonLoader/
│   ├── MelonLoader.dll
│   └── 0Harmony.dll
└── Unity/
    ├── UnityEngine.dll
    ├── UnityEngine.CoreModule.dll
    ├── UnityEngine.UI.dll
    ├── UnityEngine.IMGUIModule.dll
    └── Unity.TextMeshPro.dll
```

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
dotnet build UnityGameTranslator-MelonLoader/UnityGameTranslator.MelonLoader.csproj -c Release
```

Output DLLs are in each project's `bin/` folder.

### Versioning

The version is centralized in `Directory.Build.props`. Update the `<Version>` tag before release—all projects inherit it automatically.

---

## License

Apache 2.0
