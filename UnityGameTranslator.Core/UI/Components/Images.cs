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

        internal ImageHandle(GameObject box, Image shown, LabelHandle why)
        {
            _box = box;
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
            if (_mine == null) return;
            // Qualified: `Object` alone is this handle's own property, not the engine's type.
            UnityEngine.Object.Destroy(_mine);
            _mine = null;
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

            return new ImageHandle(box, shown, why);
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
