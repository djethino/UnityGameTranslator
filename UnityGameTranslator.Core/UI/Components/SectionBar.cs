using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// What the section bar's buttons carry beyond their words: a picture each, the bar folded to
    /// those pictures or open on the words, and — folded — the name of the section under the
    /// pointer, shown beside it (decided 2026-10-11: « la barre repliée en icônes, avec le nom au
    /// survol »). The buttons themselves are the bar's document (section-bar.json); this only
    /// dresses them, so the panel builds nothing by hand.
    ///
    /// ⚠ The name beside the picture is polled, as the help bar is (HelpZone.PollHover): pointer
    /// events never reach an injected handler on IL2CPP.
    /// </summary>
    public sealed class SectionBar
    {
        private const int IconSize = 18;
        private const int IconInset = 8;

        /// <summary>The close button of a title bar is this square (UniverseLib PanelBase): the fold button is its twin.</summary>
        private const int HeadButtonSize = 22;

        private readonly List<(ButtonHandle Button, Image Icon, RectTransform Label, GameObject Edge)> _items =
            new List<(ButtonHandle, Image, RectTransform, GameObject)>();
        private readonly List<string> _names = new List<string>();
        private readonly ButtonHandle _fold;
        private readonly GameObject _head;
        private GameObject _stack;
        private float _widestName;
        private readonly Image _headImage;
        private readonly LayoutElement _headLayout;
        private GameObject _tip;
        private Text _tipText;
        private bool _folded;
        private int _current = -1;

        private SectionBar(GameObject head, ButtonHandle fold)
        {
            _head = head;
            _fold = fold;
            if (head == null) return;
            _headImage = UIHelpers.GetComponentSafe<Image>(head) ?? head.AddComponent<Image>();
            if (_headImage != null)
            {
                _headImage.color = UIFactory.Colors.TitleBarBackground;
                UIFactory.SetShape(_headImage, UIFactory.Shapes.CardTop);
            }
            _headLayout = UIHelpers.GetComponentSafe<LayoutElement>(head) ?? head.AddComponent<LayoutElement>();
        }

        /// <summary>
        /// Dresses the head row (the window's title bar continued, with the fold button in it) and
        /// the section buttons, in order, with the marks named (Icons). A mark that cannot be built
        /// leaves its button with its words alone.
        /// </summary>
        public static SectionBar Dress(Host head, ButtonHandle fold, Host stack, IList<ButtonHandle> sections, IList<string> marks)
        {
            var bar = new SectionBar(head?.Object, fold);
            bar._stack = stack?.Object;
            DressLikeTitleBarButton(fold);
            for (int i = 0; i < sections.Count; i++)
            {
                var button = sections[i];
                var icon = AddIcon(button, marks[i]);
                var text = button.Ref?.ButtonText;
                var label = text != null ? text.rectTransform : null;
                if (text != null) text.alignment = TextAnchor.MiddleLeft;
                // The mod's chosen row: the tint, and an accent edge (UIStyles.CreateListItem).
                var host = button.Ref?.Component != null ? button.Ref.Component.gameObject : null;
                var edge = host != null ? UIFactory.AddBorder(host, UIStyles.ButtonHover, UIFactory.Shapes.BorderSmall) : null;
                if (edge != null) edge.SetActive(false);
                if (host != null) UIFactory.SetShape(UIHelpers.GetComponentSafe<Image>(host), UIFactory.Shapes.Small);
                bar._items.Add((button, icon, label, edge));
                bar._names.Add("");
                bar.Paint(i, false);
            }
            return bar;
        }

        /// <summary>The fold button as the title bar's close button is: the same square, the same fill.</summary>
        private static void DressLikeTitleBarButton(ButtonHandle fold)
        {
            var host = fold?.Ref?.Component != null ? fold.Ref.Component.gameObject : null;
            if (host == null) return;
            UIFactory.SetLayoutElement(host, minWidth: HeadButtonSize, minHeight: HeadButtonSize,
                preferredWidth: HeadButtonSize, preferredHeight: HeadButtonSize, flexibleWidth: 0, flexibleHeight: 0);
            UniverseLib.RuntimeHelper.SetColorBlock(fold.Ref.Component,
                UIFactory.Colors.ButtonNormal, UIFactory.Colors.ButtonHighlight, UIStyles.AccentPressed);
            if (fold.Ref.ButtonText != null) fold.Ref.ButtonText.fontSize = 14;
        }

        /// <summary>
        /// The head row follows the section's title bar, every frame the bar is up: as tall as it,
        /// and its colour — lighter while the window has the keyboard (the focus pass paints it).
        /// </summary>
        public void MatchHead(Host titleBar, Host window)
        {
            var title = titleBar?.Object;
            if (title == null) return;
            var titleRect = title.GetComponent<RectTransform>();
            var titleImage = UIHelpers.GetComponentSafe<Image>(title);
            float height = titleRect != null ? titleRect.rect.height : 0f;

            // 🔴 And the same edge around it (2026-10-14: « la barre de titre a un liseré autour avec
            // la couleur de fond, le bouton repli n'en a pas »). The window's title bar sits inside
            // a band of the window's ground; the bar's head gets the same band, measured from where
            // the title bar IS in its window, so the two tops line up whatever the theme's edge.
            var windowRect = window?.Object != null ? window.Object.GetComponent<RectTransform>() : null;
            var column = _head != null && _head.transform.parent != null
                ? UIHelpers.GetComponentSafe<VerticalLayoutGroup>(_head.transform.parent.gameObject) : null;
            if (titleRect != null && windowRect != null && column != null)
            {
                var corner = windowRect.InverseTransformPoint(
                    titleRect.TransformPoint(new Vector3(titleRect.rect.xMin, titleRect.rect.yMax, 0f)));
                int top = Mathf.Max(0, Mathf.RoundToInt(windowRect.rect.yMax - corner.y));
                int side = Mathf.Max(0, Mathf.RoundToInt(corner.x - windowRect.rect.xMin));
                var pad = column.padding;
                if (pad == null || pad.top != top || pad.left != side || pad.right != side)
                    column.padding = Compat.MakeRectOffset(side, side, top, pad != null ? pad.bottom : 0);
            }
            if (_headLayout != null && height > 0f && Mathf.Abs(_headLayout.minHeight - height) > 0.5f)
            {
                _headLayout.minHeight = height;
                _headLayout.preferredHeight = height;
                _headLayout.flexibleHeight = 0;
            }
            if (_headImage != null && titleImage != null && _headImage.color != titleImage.color) _headImage.color = titleImage.color;
        }

        /// <summary>The section up: lit as a chosen row; the others flat on the bar, lighter under the pointer.</summary>
        public void Current(int index)
        {
            if (index == _current) return;
            int was = _current;
            _current = index;
            if (was >= 0 && was < _items.Count) Paint(was, false);
            if (index >= 0 && index < _items.Count)
            {
                Paint(index, true);
                // The bar stays still; the button now lit makes the slight movement a tab's
                // content makes (user, 2026-10-15). Not on the first lighting: the bar arrives
                // with its window then.
                var host = _items[index].Button.Ref?.Component != null ? _items[index].Button.Ref.Component.gameObject : null;
                if (was >= 0 && host != null) Appearances.Block(host);
            }
            // Measured again here too: the interface font may have changed since (Mod Options).
            MeasureNames();
        }

        private void Paint(int index, bool current)
        {
            var (button, _, _, edge) = _items[index];
            var component = button.Ref?.Component;
            if (component == null) return;
            if (current)
                UniverseLib.RuntimeHelper.SetColorBlock(component,
                    UIStyles.ItemBackgroundSelected, UIStyles.ItemBackgroundSelected, UIStyles.ItemBackgroundSelected);
            else
                UniverseLib.RuntimeHelper.SetColorBlock(component,
                    new Color(0f, 0f, 0f, 0f), UIStyles.ItemBackgroundHover, UIStyles.ItemBackgroundSelected);
            if (edge != null) edge.SetActive(current);
            var text = button.Ref.ButtonText;
            if (text != null)
            {
                text.color = current ? UIStyles.TextPrimary : UIStyles.TextSecondary;
                text.fontStyle = current ? FontStyle.Bold : FontStyle.Normal;
            }
            var icon = _items[index].Icon;
            if (icon != null) icon.color = current ? UIStyles.TextPrimary : UIStyles.TextSecondary;
        }

        /// <summary>The words of section <paramref name="index"/> — its name, and the count after it when there is one.</summary>
        public void Name(int index, string words)
        {
            if (index < 0 || index >= _items.Count) return;
            if (_names[index] == words) return;
            _names[index] = words;
            if (!_folded) _items[index].Button.Label = words;
            MeasureNames();
        }

        /// <summary>
        /// The widest name, as it reads in bold (the section up is): what the open bar is sized to,
        /// so no name runs past its button (2026-10-14: « Translation Tools » did). Measured when a
        /// name changes or the section up does, never per frame — a count appearing, a font picked.
        /// </summary>
        private void MeasureNames()
        {
            var probe = _items.Count > 0 ? _items[0].Button.Ref?.ButtonText : null;
            if (probe == null) return;
            string kept = probe.text;
            var keptStyle = probe.fontStyle;
            probe.fontStyle = FontStyle.Bold;
            float widest = 0f;
            for (int i = 0; i < _names.Count; i++)
            {
                probe.text = _names[i];
                widest = Mathf.Max(widest, probe.preferredWidth);
            }
            probe.text = kept;
            probe.fontStyle = keptStyle;
            _widestName = widest;
        }

        /// <summary>
        /// How wide the bar is right now: its buttons' content — the picture alone folded, the
        /// picture and the widest name open — and the room around them, read from the layouts
        /// that hold them (the band copied from the window, the list's own padding). Never a
        /// width written down: a band or a name that grows takes the bar with it.
        /// </summary>
        public float Width
        {
            get
            {
                float content = _folded
                    ? IconInset + IconSize + IconInset
                    : IconInset + IconSize + 6 + _widestName + IconInset;
                float around = Sides(_head != null ? _head.transform.parent : null) + Sides(_stack != null ? _stack.transform : null);
                float head = HeadButtonSize + Sides(_head != null ? _head.transform : null) + Sides(_head != null ? _head.transform.parent : null);
                return Mathf.Ceil(Mathf.Max(content + around, head));
            }
        }

        private static float Sides(Transform holder)
        {
            if (holder == null) return 0f;
            var group = UIHelpers.GetComponentSafe<HorizontalOrVerticalLayoutGroup>(holder.gameObject);
            return group != null && group.padding != null ? group.padding.left + group.padding.right : 0f;
        }

        /// <summary>Folded to the pictures, or open on the words.</summary>
        public bool Folded
        {
            get => _folded;
            set
            {
                _folded = value;
                for (int i = 0; i < _items.Count; i++)
                {
                    var (button, icon, label, _) = _items[i];
                    button.Label = value ? "" : _names[i];
                    Place(icon, value);
                    if (label != null) label.offsetMin = new Vector2(value ? 0 : IconInset + IconSize + 6, label.offsetMin.y);
                }
                // The two marks every program folds a side bar with: open it (») or fold it («).
                _fold.Label = value ? "\u00BB" : "\u00AB";
                // Open: at the right end of the head, where the bar folds toward. Folded: centred.
                var row = _head != null ? UIHelpers.GetComponentSafe<HorizontalLayoutGroup>(_head) : null;
                if (row != null) row.childAlignment = value ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight;
                if (!value && _tip != null) _tip.SetActive(false);
            }
        }

        /// <summary>
        /// Folded: the name of the section under the pointer, beside its picture. Called every
        /// frame the bar is up.
        /// </summary>
        public void PollName()
        {
            if (!_folded) return;
            Vector3 mouse = UniverseLib.Input.InputManager.MousePosition;
            var at = new Vector2(mouse.x, mouse.y);
            for (int i = 0; i < _items.Count; i++)
            {
                // ⚠ GetComponent, never a cast of .transform: on IL2CPP the proxy is a Transform and
                // the cast throws — every frame, in the interface's update (2026-10-14).
                var rect = _items[i].Button.Ref?.Component != null ? _items[i].Button.Ref.Component.GetComponent<RectTransform>() : null;
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                var canvas = rect.GetComponentInParent<Canvas>();
                if (!UIHelpers.ContainsScreenPoint(rect, canvas, at)) continue;
                ShowTip(rect, _names[i]);
                return;
            }
            if (_tip != null && _tip.activeSelf) _tip.SetActive(false);
        }

        private void ShowTip(RectTransform beside, string words)
        {
            if (_tip == null)
            {
                _tip = UIFactory.CreateUIObject("SectionBarName", beside.root.gameObject);
                var background = _tip.AddComponent<Image>();
                background.color = UIStyles.PanelBackground;
                background.raycastTarget = false;
                var textObj = UIFactory.CreateUIObject("Words", _tip);
                _tipText = textObj.AddComponent<Text>();
                _tipText.font = UniversalUI.DefaultFont;
                _tipText.fontSize = UIStyles.FontSizeNormal;
                _tipText.color = UIStyles.TextPrimary;
                _tipText.alignment = TextAnchor.MiddleLeft;
                _tipText.raycastTarget = false;
                _tipText.horizontalOverflow = HorizontalWrapMode.Overflow;
                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(8, 0); textRect.offsetMax = new Vector2(-8, 0);
                TranslatorCore.RegisterExcluded(_tipText);
            }
            _tipText.text = words;
            var rect = _tip.GetComponent<RectTransform>();
            // TransformPoint, never GetWorldCorners (an array parameter, missing on IL2CPP).
            var local = beside.rect;
            var rightMiddle = beside.TransformPoint(new Vector3(local.xMax, local.center.y, 0f));
            rect.SetParent(beside.root, true);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.position = rightMiddle + beside.right * (6f * beside.lossyScale.x);
            rect.sizeDelta = new Vector2(_tipText.preferredWidth + 16, beside.rect.height);
            rect.SetAsLastSibling();
            _tip.SetActive(true);
        }

        private static Image AddIcon(ButtonHandle button, string mark)
        {
            var host = button?.Ref?.Component != null ? button.Ref.Component.gameObject : null;
            var sprite = Icons.Get(mark);
            if (host == null || sprite == null) return null;
            var holder = UIFactory.CreateUIObject("SectionMark", host);
            var layout = holder.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var image = holder.AddComponent<Image>();
            image.sprite = sprite;
            image.color = UIStyles.TextPrimary;
            image.preserveAspect = true;
            image.raycastTarget = false;
            Place(image, false);
            return image;
        }

        /// <summary>At the left of the button with its words beside it, or centred when the bar is folded.</summary>
        private static void Place(Image icon, bool centred)
        {
            if (icon == null) return;
            var rect = icon.rectTransform;
            rect.sizeDelta = new Vector2(IconSize, IconSize);
            if (centred)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
            }
            else
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(IconInset, 0f);
            }
        }
    }
}
