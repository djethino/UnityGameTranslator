using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The three pictures this mod ships — the product's face, the publisher's signature, the
    /// turning mark — read once out of the assembly and handed over as sprites.
    ///
    /// 🔴 **Through TextureUtils, never TextureHelper.** UniverseLib's helper NAMES one overload of
    /// Sprite.Create; ours finds the one the running game actually carries. On a game where IL2CPP
    /// has stripped the named one, the process dies with no exception and no log line — nothing to
    /// catch and nothing to read. It killed two games outright before the rule was written
    /// (CLAUDE.md, analyse/pieges-projet.md §2), and this is the mod's first picture since.
    ///
    /// ⚠ Each picture is loaded at most once and kept: a texture per panel opening would be a leak
    /// on a screen somebody can open twenty times. A failure is remembered too — a game that cannot
    /// decode a PNG will not decode it better on the next opening, and the screen says so in words
    /// rather than showing an empty box.
    /// </summary>
    public static class Branding
    {
        /// <summary>The product's face, shared with the website and the Manager.</summary>
        public const string ProductIcon = "icon-128";

        /// <summary>The publisher's signature: black line art, meant to sit on its own white band.</summary>
        public const string PublisherLogo = "asymptomatik-full";

        /// <summary>The ASymptOmatik mark, which turns while something is being waited for.</summary>
        public const string Gear = "gear";

        private static readonly Dictionary<string, object> _sprites =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private static readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The sprite for one of the names above, or null when this game could not make it.
        ///
        /// ⚠ Null is an answer the caller has to render — see the About tab, which says the picture
        /// could not be shown rather than leaving a hole where a logo belongs.
        /// </summary>
        public static object Sprite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            object sprite;
            if (_sprites.TryGetValue(name, out sprite)) return sprite;
            if (_failed.Contains(name)) return null;

            try
            {
                byte[] png = Read(name);
                if (png == null)
                {
                    _failed.Add(name);
                    TranslatorCore.LogWarning($"[Branding] '{name}' is not embedded in this build");
                    return null;
                }

                // Size and format are replaced by LoadImage; 2x2 is what every other picture in
                // this mod starts from (see ImageReplacer).
                var texture = Compat.MakeTexture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture == null || !TextureUtils.LoadImageToTexture(texture, png))
                {
                    _failed.Add(name);
                    TranslatorCore.LogWarning($"[Branding] '{name}' could not be decoded by this game");
                    return null;
                }

                // ⚠ Kept, never destroyed with the panel: the sprite below points at it, and a
                // texture collected under a live sprite draws as a magenta square.
                texture.name = "UGT.Branding." + name;

                sprite = TextureUtils.CreateSpriteSafe(texture, new Vector2(0.5f, 0.5f), 100f, Vector4.zero);
                if (sprite == null)
                {
                    _failed.Add(name);
                    TranslatorCore.LogWarning($"[Branding] Sprite.Create refused '{name}' on this runtime");
                    return null;
                }

                _sprites[name] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                _failed.Add(name);
                TranslatorCore.LogWarning($"[Branding] '{name}' failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>The bytes of one embedded picture, or null when this build carries none.</summary>
        private static byte[] Read(string name)
        {
            var assembly = typeof(Branding).Assembly;

            using (var stream = assembly.GetManifestResourceStream("assets/" + name + ".png"))
            {
                if (stream == null) return null;

                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }
    }
}
