# Assets

The only pictures this mod ships. Embedded in the Core assembly (see the `.csproj`) and loaded
through `UI/Branding.cs`.

| File | Origin | Notes |
|---|---|---|
| `icon-128.png` | `website/public/icon-128.png`, also in the Manager | The product's face, shared by the three products so the ecosystem is recognisably one family. |
| `asymptomatik-full.png` | `asymptomatikgames/logo-full-640.webp`, taken from the Manager | The publisher's signature, on the About tab's band. **Derived**: black line art whose opacity comes from each pixel's darkness, so the white behind it is the band's own and the strokes keep their anti-aliasing. It is drawn black on white rather than inverted — inverted hand-drawn strokes read as a negative, not as a drawing. |
| `gear.png` | `asymptomatikgames/gear-icon.webp`, taken from the Manager | The ASymptOmatik mark, turning while something is being waited for — the same mark, for the same reason, as the Manager's `SpinningGear`. |

⚠ **The gear is not the publisher's signature.** It stands for ASymptOmatik; the product is
published by ASymptOmatik **Games**, and only `asymptomatik-full.png` says so. The gear turns as an
indicator; the full logo signs.

⚠ **Copied rather than shared.** The mod and the Manager are separate repositories and neither can
reference the other's files. Regenerate both from the same source if it changes, and keep this
table in step with `manager/src/UnityGameTranslator.Manager.Gui/Assets/README.md`.

🔴 **A picture is loaded through `TextureUtils`, never `TextureHelper`.** The second names one
overload; when IL2CPP has stripped it the process dies with no exception and no log line. See
CLAUDE.md, and `analyse/pieges-projet.md` §2 for what that cost.
