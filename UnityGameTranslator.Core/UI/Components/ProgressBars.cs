using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// How far a long piece of work has got: a trough the width of its row, the done share filled.
    ///
    /// 🔴 Why (user, 2026-09-28): "c'est toujours mieux quand c'est un peu graphique". A count in a
    /// line of text says the same, but the eye has to read and divide; a bar is read at a glance —
    /// and the mod had none (QualityBar is deliberately NOT one: it shows shares of what was
    /// captured, not work advancing).
    ///
    /// ⚠ Drawn the way QualityBar is, for the same reason: two segments sized by their share
    /// (flexibleWidth), no anchors or fill-amount — layout only, which is what renders the same on
    /// Mono and IL2CPP.
    /// </summary>
    public sealed class ProgressHandle : Handle
    {
        private readonly GameObject _root;
        private readonly LayoutElement _done;
        private readonly LayoutElement _left;
        private readonly Image _doneImage;
        private readonly int _height;
        private float _value = -1f;

        internal ProgressHandle(GameObject root, LayoutElement done, LayoutElement left, Image doneImage, int height)
        {
            _root = root;
            _done = done;
            _left = left;
            _doneImage = doneImage;
            _height = height;
        }

        internal override GameObject Object => _root;

        /// <summary>The done share, 0 to 1. Written only when it moves.</summary>
        public float Value
        {
            get => Mathf.Max(0f, _value);
            set
            {
                float v = Mathf.Clamp01(value);
                if (Mathf.Abs(v - _value) < 0.001f) return;
                _value = v;
                if (_done != null) _done.flexibleWidth = v;
                if (_left != null) _left.flexibleWidth = 1f - v;
                // A share of nothing takes no room and shows nothing: the trough alone says "not started".
                if (_doneImage != null) _doneImage.enabled = v > 0f;
            }
        }

        /// <summary>From a count: <paramref name="done"/> of <paramref name="total"/>; empty when there is no total.</summary>
        public void Show(int done, int total) => Value = total > 0 ? (float)done / total : 0f;
    }

    public static class ProgressBars
    {
        /// <summary>Height of the bar: thin, like the website's progress bars.</summary>
        public const int DefaultHeight = 6;

        public static ProgressHandle Create(Host parent, string name, Tone tone)
        {
            int height = DefaultHeight;
            int radius = Mathf.Max(1, height / 2);

            // The trough: the whole row, rounded at both ends, the colour every trough here has.
            var root = UIFactory.CreateHorizontalGroup(parent.Object, name, false, false, true, true,
                0, default, UIStyles.TroughBackground);
            UIFactory.SetLayoutElement(root, minHeight: height, preferredHeight: height,
                flexibleWidth: 9999, flexibleHeight: 0);
            var trough = root.GetComponent<Image>();
            if (trough != null) UIFactory.SetShape(trough, UIShapes.Rounded(radius, Corners.All));

            // What is done, then what is left — both sized only by their share.
            var doneObj = UIFactory.CreateUIObject("Done", root);
            var doneImage = doneObj.AddComponent<Image>();
            doneImage.color = Tones.Colour(tone);
            doneImage.raycastTarget = false;
            UIFactory.SetShape(doneImage, UIShapes.Rounded(radius, Corners.All));
            var done = UIFactory.SetLayoutElement(doneObj, minHeight: height, flexibleWidth: 0);

            var leftObj = UIFactory.CreateUIObject("Left", root);
            var left = UIFactory.SetLayoutElement(leftObj, minHeight: height, flexibleWidth: 1);

            var bar = new ProgressHandle(root, done, left, doneImage, height);
            bar.Value = 0f;
            return bar;
        }
    }
}
