using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A picture the code puts on a screen: what the game draws right now on the thing somebody
    /// picked.
    ///
    /// 🔴 **A box of a settled height, and the picture fitted inside it.** Not the picture's own
    /// size: a texture is 2048 across and a card is 420. And not a height that follows the
    /// proportions either — that would move every button below it from one selection to the next,
    /// so the eye would have to find them again at each click. The box keeps its room, the picture
    /// keeps its proportions, and whatever is left is the surface behind.
    ///
    /// 🔴 **A box that cannot show something SAYS WHY** (<see cref="Explain"/>). It is the one rule
    /// this piece exists under: a picture missing with nothing said reads as a broken screen, and an
    /// image that could not be read is exactly the case this window met the day it was written.
    /// </summary>
    public sealed class ImageHandle : Handle
    {
        private readonly GameObject _box;
        private readonly Image _shown;
        private readonly LabelHandle _why;

        // What was made HERE to show the last picture, and is nobody else's: a sprite built around
        // a raw texture. A sprite the game handed over is the game's and is never destroyed —
        // doing so would take the picture off the object the person is looking at.
        private Sprite _mine;

        // The texture decoded HERE for the last picture (ShowEncoded), destroyed with its sprite.
        private Texture2D _mineTexture;

        // Behind the picture: a game's wide picture blurred over the whole box (ShowGameCover).
        // Hidden otherwise. Its few pixels and its sprite are this box's, like the picture's.
        private readonly Image _backdrop;
        private Texture2D _backdropTexture;
        private Sprite _backdropSprite;

        internal ImageHandle(GameObject box, Image backdrop, Image shown, LabelHandle why)
        {
            _box = box;
            _backdrop = backdrop;
            _shown = shown;
            _why = why;
        }

        internal override GameObject Object => _box;

        /// <summary>
        /// Paints this box white instead of the usual trough — for a picture that brings its own
        /// paper.
        ///
        /// 🔴 **Because the publisher's signature is black line art.** Inverted to white it reads
        /// as a negative rather than as a drawing, so it keeps its ink and the box brings the
        /// white; the logo's anti-aliasing was cut against that same white, so there is no seam.
        /// Painting the stack BEHIND the box does nothing: this box paints its own background, and
        /// that is what showed as a grey band under a logo that was supposed to sit on white.
        ///
        /// ⚠ Repainting a piece is what a panel may do; building one is not (see ScreenBuilder).
        /// And it is for that one case: dark text or a dark drawing is all that reads on white.
        /// </summary>
        public void OnPaper()
        {
            if (_box == null) return;
            UIStyles.SetBackground(_box, Color.white, UIFactory.Shapes.Small);
        }

        /// <summary>
        /// Show what the game draws on this thing. The argument is what the picker found and the
        /// panel holds — a sprite or a texture, whichever the game uses — and this piece asks
        /// <see cref="TextureUtils.SpriteForDisplay"/> what can be made of it.
        ///
        /// ⚠ Nothing showable is not nothing said: the reason takes the picture's place.
        /// </summary>
        public void Show(object spriteObj)
        {
            Drop();

            string why;
            bool mine;
            var sprite = TextureUtils.SpriteForDisplay(spriteObj, out why, out mine) as Sprite;
            if (sprite == null)
            {
                Explain(why ?? "Nothing to show.");
                return;
            }

            if (mine) _mine = sprite;
            if (_shown != null)
            {
                _shown.sprite = sprite;
                _shown.gameObject.SetActive(true);
            }
            if (_why != null) _why.Visible = false;
        }

        /// <summary>
        /// Show a picture from its encoded bytes (PNG, JPG) — a game's cover fetched for a list
        /// (2026-10-05). Decoded the way every picture of this mod is (TextureUtils: the overload
        /// the running game actually has); the texture and its sprite are this box's, and go with
        /// the next picture or <see cref="Clear"/>. Nothing decodable leaves the box empty: a cover
        /// helps recognise a game, the words beside it still say which one it is.
        /// </summary>
        /// <returns>Whether a picture is shown.</returns>
        public bool ShowEncoded(byte[] bytes)
        {
            Drop();
            if (bytes == null || bytes.Length == 0) return false;

            var texture = Compat.MakeTexture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture == null) return false;

            if (!TextureUtils.LoadImageToTexture(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return false;
            }

            // Held only AFTER Show: Show begins by dropping what this box made for the last picture,
            // and a texture held before it would be destroyed under its own sprite (drawn white).
            Show(texture);
            if (_mine == null)
            {
                UnityEngine.Object.Destroy(texture);
                return false;
            }
            _mineTexture = texture;

            // Out of the engine's sweep of unused assets, as ImageReplacer's pictures are: a texture
            // made at run time is "unused" to it, and the sweep took them (IL2CPP, 2026-10-04).
            // Drop destroys both itself, so nothing outlives this box.
            texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _mine.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return _shown != null && _shown.sprite != null;
        }

        /// <summary>
        /// Show a GAME's picture from its encoded bytes, the way the site and the Manager show it
        /// (common GameCandidates.FillsFrame, user 2026-10-06): a cover — taller than wide — fills
        /// the box, cropped at its edges; a wider picture (a store header, a screenshot) is shown
        /// whole over a blurred copy of itself.
        ///
        /// 🔴 **Nothing here the mod does not already do in a game**: the picture is decoded as
        /// ShowEncoded decodes it, the crop is a sprite of one region (TextureUtils.CreateSpriteSafe
        /// with a Rect, as the shape atlas), and the blur is a few pixels written like a language
        /// flag (Icons.FlagSprite) that the engine's bilinear sampling stretches. The arithmetic is
        /// Engine/CoverFit, held by CoverFitChecks.
        ///
        /// ⚠ A box not laid out yet (no size) shows the picture whole, as ShowEncoded does; a
        /// picture whose pixels come in an order CoverFit does not read gets no blur — said once.
        /// </summary>
        /// <returns>Whether a picture is shown.</returns>
        public bool ShowGameCover(byte[] bytes)
        {
            if (!ShowEncoded(bytes)) return false;

            var texture = _mineTexture;
            int width = texture.width, height = texture.height;
            float frameAspect = FrameAspect();

            if (Common.GameCandidates.FillsFrame(width, height))
            {
                if (frameAspect > 0f) Crop(texture, frameAspect);
                return true;
            }

            Blur(texture, frameAspect > 0f ? frameAspect : (float)width / height);
            return true;
        }

        /// <summary>The proportions of the room the picture is drawn in, or 0 before it is laid out.</summary>
        private float FrameAspect()
        {
            var rect = _shown != null ? _shown.rectTransform.rect : default(Rect);
            return rect.width > 0f && rect.height > 0f ? rect.width / rect.height : 0f;
        }

        /// <summary>The picture's centred region with the frame's proportions, in place of the whole.</summary>
        private void Crop(Texture2D texture, float frameAspect)
        {
            CoverFit.CentredRegion(texture.width, texture.height, frameAspect, out int x, out int y, out int w, out int h);
            if (w == texture.width && h == texture.height) return;

            var region = TextureUtils.CreateSpriteSafe(texture, Compat.MakeRect(x, y, w, h),
                                                       new Vector2(0.5f, 0.5f), 100f, Vector4.zero) as Sprite;
            if (region == null) return; // the whole picture stays, fitted

            UnityEngine.Object.Destroy(_mine);
            _mine = region;
            _mine.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _shown.sprite = region;
        }

        /// <summary>A blurred copy of the picture over the whole box, behind the picture shown whole.</summary>
        private void Blur(Texture2D texture, float frameAspect)
        {
            if (_backdrop == null) return;

            CoverFit.Layout layout;
            switch (texture.format)
            {
                case TextureFormat.RGB24: layout = CoverFit.Layout.Rgb24; break;
                case TextureFormat.RGBA32: layout = CoverFit.Layout.Rgba32; break;
                case TextureFormat.ARGB32: layout = CoverFit.Layout.Argb32; break;
                case TextureFormat.BGRA32: layout = CoverFit.Layout.Bgra32; break;
                default:
                    if (DiagnosticOnce.First("cover-blur-format", texture.format.ToString()))
                        TranslatorCore.LogInfo($"[Images] A game picture decoded as {texture.format}: shown whole, without its blurred backdrop.");
                    return;
            }

            CoverFit.CentredRegion(texture.width, texture.height, frameAspect, out int x, out int y, out int w, out int h);

            // A few pixels: the blur IS the shrink. Eight rows, as many columns as the frame's shape.
            const int rows = 8;
            int columns = Math.Max(1, (int)Math.Round(rows * frameAspect));
            var shrunk = CoverFit.Shrink(TextureUtils.GetRawTextureDataSafe(texture), texture.width, texture.height,
                                         layout, x, y, w, h, columns, rows);
            if (shrunk == null) return;

            var colours = new Color32[columns * rows];
            for (int i = 0; i < colours.Length; i++)
                colours[i] = new Color32(shrunk[i * 4], shrunk[i * 4 + 1], shrunk[i * 4 + 2], shrunk[i * 4 + 3]);

            var small = Compat.MakeTexture2D(columns, rows, TextureFormat.RGBA32, false);
            if (small == null) return;
            small.filterMode = FilterMode.Bilinear;
            small.wrapMode = TextureWrapMode.Clamp;
            if (!TextureUtils.SetPixels32Safe(small, colours))
            {
                UnityEngine.Object.Destroy(small);
                return;
            }
            small.Apply(false, false);

            var sprite = TextureUtils.CreateSpriteSafe(small, new Vector2(0.5f, 0.5f), 100f, Vector4.zero) as Sprite;
            if (sprite == null)
            {
                UnityEngine.Object.Destroy(small);
                return;
            }

            // Out of the engine's sweep of unused assets, like the picture (ShowEncoded).
            small.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _backdropTexture = small;
            _backdropSprite = sprite;

            _backdrop.sprite = sprite;
            _backdrop.gameObject.SetActive(true);
        }

        /// <summary>
        /// Say why there is no picture, in the words the code composed — an ordinary sentence of
        /// this interface, so it goes through the label's own Dynamic policy like any other.
        /// </summary>
        public void Explain(string sentence)
        {
            Drop();
            Blank();
            if (_why == null) return;
            _why.Say(sentence ?? "");
            _why.Visible = true;
        }

        /// <summary>Back to holding nothing — the box empties with the selection it belonged to.</summary>
        public void Clear()
        {
            Drop();
            Blank();
            if (_why == null) return;
            _why.Show("");
            _why.Visible = false;
        }

        private void Blank()
        {
            if (_shown == null) return;
            _shown.sprite = null;
            _shown.gameObject.SetActive(false);
        }

        /// <summary>Let go of the sprite built for the last picture, if it was built here.</summary>
        private void Drop()
        {
            if (_mine != null)
            {
                // Qualified: `Object` alone is this handle's own property, not the engine's type.
                UnityEngine.Object.Destroy(_mine);
                _mine = null;
            }

            // After the sprite built on it: a texture destroyed under a live sprite draws magenta.
            if (_mineTexture != null)
            {
                UnityEngine.Object.Destroy(_mineTexture);
                _mineTexture = null;
            }

            // The blurred copy goes with the picture it was made from — same order.
            if (_backdrop != null)
            {
                _backdrop.sprite = null;
                _backdrop.gameObject.SetActive(false);
            }
            if (_backdropSprite != null)
            {
                UnityEngine.Object.Destroy(_backdropSprite);
                _backdropSprite = null;
            }
            if (_backdropTexture != null)
            {
                UnityEngine.Object.Destroy(_backdropTexture);
                _backdropTexture = null;
            }
        }
    }

    public static class Images
    {
        /// <summary>
        /// Build the box. It keeps <paramref name="height"/> whatever it holds and takes the width
        /// of its row; the picture inside is fitted to it with its proportions kept.
        /// </summary>
        public static ImageHandle Create(Host parent, string name, int height)
        {
            GameObject box = UIFactory.CreateUIObject(name, parent.Object);

            // The surface behind, so a picture with transparent parts reads as a picture and not as
            // a hole, and so an empty box is visibly a box. Painted like every other trough here.
            var back = box.AddComponent<Image>();
            back.raycastTarget = false;
            UIStyles.SetBackground(box, UIStyles.TroughBackground, UIFactory.Shapes.Small);

            UIFactory.SetLayoutElement(box, minHeight: height, preferredHeight: height,
                                       flexibleHeight: 0, flexibleWidth: 9999);

            // Behind the picture, a blurred copy of it — only for a game's wide picture
            // (ImageHandle.ShowGameCover). Created first, so it is drawn under the picture; stretched
            // over the same room, dimmed so the picture in front stays the subject.
            GameObject backdropObj = UIFactory.CreateUIObject("Backdrop", box);
            var backdrop = backdropObj.AddComponent<Image>();
            backdrop.raycastTarget = false;
            backdrop.preserveAspect = false;
            backdrop.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            Stretch(backdropObj, UIStyles.SmallSpacing);
            backdropObj.SetActive(false);

            // The picture, inset a little so the surface reads as a frame around it.
            GameObject shownObj = UIFactory.CreateUIObject("Shown", box);
            var shown = shownObj.AddComponent<Image>();
            shown.raycastTarget = false;
            // What keeps the proportions: the picture fills what it can of the box and no more.
            shown.preserveAspect = true;
            Stretch(shownObj, UIStyles.SmallSpacing);
            shownObj.SetActive(false);

            // Why there is nothing, in the picture's place — never instead of the box. An ordinary
            // label of the vocabulary, so its role, its tone and its translation policy are the
            // same ones every other sentence of this interface is written under.
            var why = Labels.Create(new Host(box), "Why", "", TextRole.Small, tone: Tone.Muted,
                                    centred: true, policy: TextPolicy.Dynamic, fill: Fill.Stretch);
            Stretch(why.Object, UIStyles.ElementSpacing);
            why.Visible = false;

            return new ImageHandle(box, backdrop, shown, why);
        }

        /// <summary>Fill the parent, keeping <paramref name="inset"/> pixels on every side.</summary>
        private static void Stretch(GameObject obj, int inset)
        {
            var rect = obj.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
