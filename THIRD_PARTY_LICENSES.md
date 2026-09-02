# Third-Party Licenses

UnityGameTranslator includes the following third-party libraries. We thank all the developers and contributors for their work.

---

## UniverseLib

A library for making plugins which target IL2CPP and Mono Unity games.

- **Source:** https://github.com/yukieiji/UniverseLib (fork of sinai-dev/UniverseLib)
- **License:** LGPL-2.1
- **Copyright:** sinai-dev, yukieiji

```
This library is free software; you can redistribute it and/or modify it under
the terms of the GNU Lesser General Public License as published by the Free
Software Foundation; either version 2.1 of the License, or (at your option)
any later version.
```

---

## Harmony

A library for patching, replacing and decorating .NET methods during runtime.

- **Source:** https://github.com/pardeike/Harmony
- **License:** MIT
- **Copyright:** Andreas Pardeike

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software.
```

---

## BepInEx

Unity / XNA game patcher and plugin framework.

- **Source:** https://github.com/BepInEx/BepInEx
- **License:** LGPL-2.1
- **Copyright:** BepInEx Team

```
This library is free software; you can redistribute it and/or modify it under
the terms of the GNU Lesser General Public License as published by the Free
Software Foundation; either version 2.1 of the License, or (at your option)
any later version.
```

---

## MelonLoader

The World's First Universal Mod Loader for Unity Games.

- **Source:** https://github.com/LavaGang/MelonLoader
- **License:** Apache-2.0
- **Copyright:** LavaGang

```
Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
```

---

## Newtonsoft.Json

Popular high-performance JSON framework for .NET.

- **Source:** https://github.com/JamesNK/Newtonsoft.Json
- **License:** MIT
- **Copyright:** James Newton-King

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction.
```

---

## .NET Libraries (Microsoft)

System.Buffers, System.Memory, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe

- **Source:** https://github.com/dotnet/runtime
- **License:** MIT
- **Copyright:** .NET Foundation and Contributors

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction.
```

---

## Unity Engine

Unity runtime libraries are used for compilation and compatibility.

- **Source:** https://unity.com/
- **License:** Unity Terms of Service
- **Note:** Unity DLLs are not redistributed; users must have a valid Unity game installation.

---

*This file is included in all release packages to comply with LGPL-2.1 and Apache-2.0 license requirements.*

## ICU word-break dictionaries (Thai, Lao, Khmer, Myanmar)

Word lists used to find word boundaries in scripts written without spaces
(`UnityGameTranslator.Core/TextShaping/Resources/Dictionaries/`, embedded gzip-compressed).

- **Source:** https://github.com/unicode-org/icu/tree/main/icu4c/source/data/brkitr/dictionaries
- **License:** Unicode License v3 (https://www.unicode.org/copyright.html)
- **Copyright:** 2016 and later: Unicode, Inc. and others; 2006-2015 International Business Machines Corporation, Apple Inc., and others.
- **Modifications:** comments and blank lines removed, one word per line, gzip-compressed.

## Unicode Character Database (Indic categories)

`UnityGameTranslator.Core/TextShaping/IndicTables.g.cs` is generated from
`IndicPositionalCategory.txt` and `IndicSyllabicCategory.txt` by `tools/generate-indic-tables.py`.

- **Source:** https://www.unicode.org/Public/UCD/latest/ucd/
- **License:** Unicode License v3 (https://www.unicode.org/copyright.html)
- **Copyright:** Unicode, Inc.
