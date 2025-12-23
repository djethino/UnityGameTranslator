# Universal Unity Game Translator

A universal translation mod for Unity games using local AI (Ollama).

## Features

- **Runtime translation** - text is translated as you encounter it in-game
- **Local AI translation** via Ollama (no internet required, no API costs)
- **Auto language detection** - detects system language as target
- **Translation caching** - translated text is saved to avoid re-translating
- **Pattern matching** - handles dynamic text with numbers (e.g., "5 HP" → "5 PV")
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

> **Architecture:** UnityGameTranslator is compiled as AnyCPU and works with both x86 and x64 games. Install the mod loader version (x86/x64) matching your game, then use the same UnityGameTranslator plugin.

**How to know your game type:**
- `GameAssembly.dll` in game folder → **IL2CPP**
- `<Game>_Data/Managed/Assembly-CSharp.dll` → **Mono**

### 2. Install UnityGameTranslator

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

> **VRAM requirements:** qwen3:8b requires ~6-8 GB VRAM (Q4 quantization). Smaller models use less VRAM but may reduce translation quality.

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
  "preload_model": true
}
```

| Option | Description |
|--------|-------------|
| `target_language` | Target language (`"auto"` = system language, or `"French"`, `"German"`, etc.) |
| `source_language` | Source language (`"auto"` = let AI detect) |
| `game_context` | Game description for better translations (e.g., `"Medieval fantasy RPG"`) |
| `enable_ollama` | `true` to enable live AI translation |
| `model` | Ollama model to use |

## Sharing translations

Translation caches are stored in `translations.json`. Share this file to provide translations without requiring Ollama.

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
dotnet build UnityGameTranslator-BepInEx5/UnityGameTranslator.BepInEx5.csproj -c Release
dotnet build UnityGameTranslator-BepInEx6-Mono/UnityGameTranslator.BepInEx6Mono.csproj -c Release
dotnet build UnityGameTranslator-BepInEx6-IL2CPP/UnityGameTranslator.BepInEx6IL2CPP.csproj -c Release
dotnet build UnityGameTranslator-MelonLoader/UnityGameTranslator.MelonLoader.csproj -c Release
```

Output DLLs are in each project's `bin/` folder.

---

## License

Apache 2.0
