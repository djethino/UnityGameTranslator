using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    // ═══════════════════════════════════════════════════════════════════════════════════════
    // 🔴 THE FRONTIER. Everything a panel may hold is one of the handles below, and nothing else.
    //
    // Written 2026-09-07, after an inventory showed the mod's fifteen panels calling UniverseLib
    // 735 times against 1 384 calls to the vocabulary — and eleven components out of thirteen
    // returning a GameObject, a Text or a ButtonRef, so that a panel could write `.color =` on
    // what a component gave it without ever naming UniverseLib. That is what kept every screen
    // welded to one engine: not the widgets, the TYPES that leaked out of them.
    //
    // A handle says what a thing IS on screen — a label, a button, a place to put children — and
    // what a panel may do with it: show or hide it, write its words, enable it, describe it. What
    // draws it is `internal`, reachable by the components and by nothing outside this folder,
    // which is what makes a screen describable in data one day: a description cannot contain a
    // GameObject, and a panel that holds only handles cannot want one.
    //
    // ⚠ No UnityEngine type in a PUBLIC member of this folder, ever — not GameObject, not Text,
    // not Color, not even TextAnchor (that enum lives in a module the IL2CPP rewriter cannot
    // resolve from a public signature; see UIStyles.CreateHint). The check in Core.Checks reads
    // these files as text and fails on the first one.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What colour a piece of text speaks in. The names are roles, never hues.</summary>
    public enum Tone
    {
        /// <summary>Ordinary text.</summary>
        Plain,
        /// <summary>Text that supports the ordinary text.</summary>
        Secondary,
        /// <summary>Text that stays out of the way: hints, captions.</summary>
        Muted,
        /// <summary>The product's accent.</summary>
        Accent,
        Success,
        Warning,
        Error,
        Info,
        /// <summary>Neither good nor bad: pending, not yet known.</summary>
        Neutral,
    }

    /// <summary>What a label is on the screen — its size, weight and default tone follow.</summary>
    public enum TextRole
    {
        /// <summary>The panel's own title, centred.</summary>
        Title,
        /// <summary>A section's title.</summary>
        SectionTitle,
        /// <summary>Ordinary text.</summary>
        Body,
        /// <summary>A sentence explaining a screen, centred, secondary.</summary>
        Description,
        /// <summary>A line of information beside a control, secondary.</summary>
        Info,
        /// <summary>A small caption, muted.</summary>
        Small,
        /// <summary>A hint in italics, muted.</summary>
        Hint,
        /// <summary>A status line the code writes, centred.</summary>
        Status,
    }

    /// <summary>
    /// How a label's words reach the translator — the policy every label carries.
    ///
    /// ⚠ Two writers on one label is the defect this exists to prevent: a label the pipeline
    /// translates AND the code rewrites ends up stuck on whichever wrote last.
    /// </summary>
    public enum TextPolicy
    {
        /// <summary>Written once at construction; the translation pipeline may translate it.</summary>
        UiText,
        /// <summary>Never translated: a name, a code, a language, the product's own terms.</summary>
        Excluded,
        /// <summary>Written by the code, translated at the moment it is written (<see cref="LabelHandle.Say"/>).</summary>
        Dynamic,
    }

    /// <summary>Where a control sits inside its row or stack. Nine positions, the same nine everywhere.</summary>
    public enum Placement
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
    }

    /// <summary>How much of the row a control takes.</summary>
    public enum Fill
    {
        /// <summary>As wide as what it holds.</summary>
        Content,
        /// <summary>The rest of the row.</summary>
        Stretch,
    }

    /// <summary>What a button is for, which decides its colour.</summary>
    public enum ButtonTone { Primary, Secondary, Success, Warning, Danger, Link }

    /// <summary>A button's height: the ordinary one, or the one that fits a dense row.</summary>
    public enum ButtonSize { Normal, Compact }

    /// <summary>Room between an edge and what sits inside it.</summary>
    public readonly struct Pad
    {
        public readonly int Left, Right, Top, Bottom;

        public Pad(int left, int right, int top, int bottom)
        {
            Left = left; Right = right; Top = top; Bottom = bottom;
        }

        public static Pad None => new Pad(0, 0, 0, 0);
        public static Pad All(int value) => new Pad(value, value, value, value);
        public static Pad Of(int horizontal, int vertical) => new Pad(horizontal, horizontal, vertical, vertical);

        public bool IsNone => Left == 0 && Right == 0 && Top == 0 && Bottom == 0;
    }

    /// <summary>
    /// Something on screen a panel may hold. Visible or not, describable, trackable — and that
    /// is all a panel knows about it.
    /// </summary>
    public abstract class Handle
    {
        /// <summary>What draws it. For the components, never for a panel.</summary>
        internal abstract GameObject Object { get; }

        /// <summary>Shown or hidden. Hidden takes no room.</summary>
        public bool Visible
        {
            get => Object != null && Object.activeSelf;
            set { if (Object != null) Object.SetActive(value); }
        }

        /// <summary>Its name in the hierarchy — the name it was created with.</summary>
        public string Name => Object != null ? Object.name : "";

        /// <summary>The same thing on screen, or not. Reference identity of what draws it.</summary>
        public bool IsSameAs(Handle other) => other != null && ReferenceEquals(Object, other.Object);
    }

    /// <summary>
    /// A place children go: a stack, a row, a card, a tab's content. What a panel builds INTO.
    /// </summary>
    public sealed class Host : Handle
    {
        private readonly GameObject _object;

        internal Host(GameObject obj) { _object = obj; }

        internal override GameObject Object => _object;

        /// <summary>Whether anything active sits inside.</summary>
        public bool HasActiveChildren => _object != null && UIHelpers.CountActiveChildren(_object.transform) > 0;

        /// <summary>
        /// Hide when nothing active sits inside, show otherwise. A row whose buttons were all
        /// hidden keeps its height and shows as an empty band — this is the answer.
        /// </summary>
        public void HideIfEmpty() { Visible = HasActiveChildren; }

        /// <summary>Remove everything inside, before rebuilding it.</summary>
        public void Clear() { UIHelpers.DestroyChildren(_object); }

        /// <summary>How many children it holds, active or not.</summary>
        public int ChildCount => _object != null ? _object.transform.childCount : 0;

        /// <summary>Put this host last among its siblings — drawn on top, laid out last.</summary>
        public void ToBack() { _object?.transform.SetAsLastSibling(); }

        /// <summary>Put this host first among its siblings.</summary>
        public void ToFront() { _object?.transform.SetAsFirstSibling(); }

        /// <summary>Place this host just before another sibling.</summary>
        public void PlaceBefore(Handle sibling)
        {
            if (_object == null || sibling?.Object == null) return;
            _object.transform.SetSiblingIndex(sibling.Object.transform.GetSiblingIndex());
        }
    }

    /// <summary>A piece of text, with the one way to write it that its policy allows.</summary>
    public sealed class LabelHandle : Handle
    {
        internal readonly Text Text;
        private readonly TextPolicy _policy;

        internal LabelHandle(Text text, TextPolicy policy)
        {
            Text = text;
            _policy = policy;
        }

        internal override GameObject Object => Text != null ? Text.gameObject : null;

        /// <summary>The policy this label was created with.</summary>
        public TextPolicy Policy => _policy;

        /// <summary>What it says right now.</summary>
        public string Value => Text != null ? Text.text : "";

        /// <summary>
        /// Write an English sentence, translated at this moment (when the interface is being
        /// translated at all). Numbers inside stay as placeholders in the cache, so "Apply (1)"
        /// and "Apply (7)" share one entry; any OTHER data — a name, a language — must be kept out
        /// and concatenated by the caller through <see cref="Show"/>.
        ///
        /// ⚠ For a <see cref="TextPolicy.Dynamic"/> label. Saying something into a UiText label
        /// puts two writers on it; this refuses loudly rather than leaving the label to fight.
        /// </summary>
        public void Say(string english)
        {
            if (Text == null) return;
            if (_policy == TextPolicy.UiText)
                throw new InvalidOperationException($"'{Name}' is a UiText label: the pipeline writes it. Create it Dynamic to write it from code.");

            Text.text = TranslatorCore.TranslateOwnUIDynamic(english ?? "", Text);
            Remeasure();
        }

        /// <summary>
        /// Write text that is already what it should be — composed by the caller from translated
        /// fragments and data, or a name, a code, a number.
        /// </summary>
        public void Show(string text)
        {
            if (Text == null) return;
            Text.text = text ?? "";
            Remeasure();
        }

        /// <summary>Recolour, by role.</summary>
        public Tone Tone
        {
            set { if (Text != null) Text.color = Tones.Colour(value); }
        }

        /// <summary>Italic on or off — a status that is provisional, a name that is absent.</summary>
        public bool Italic
        {
            set
            {
                if (Text == null) return;
                bool bold = Text.fontStyle == FontStyle.Bold || Text.fontStyle == FontStyle.BoldAndItalic;
                Text.fontStyle = value ? (bold ? FontStyle.BoldAndItalic : FontStyle.Italic)
                                       : (bold ? FontStyle.Bold : FontStyle.Normal);
            }
        }

        /// <summary>Bold on or off.</summary>
        public bool Bold
        {
            set
            {
                if (Text == null) return;
                bool italic = Text.fontStyle == FontStyle.Italic || Text.fontStyle == FontStyle.BoldAndItalic;
                Text.fontStyle = value ? (italic ? FontStyle.BoldAndItalic : FontStyle.Bold)
                                       : (italic ? FontStyle.Italic : FontStyle.Normal);
            }
        }

        /// <summary>
        /// The button holding this label, if any, is told its width may have changed: a count
        /// added at refresh time arrives long after the button was built.
        /// </summary>
        private void Remeasure()
        {
            var parent = Text.transform.parent;
            if (parent != null) ScopeMarks.Fit(parent.gameObject);
        }
    }

    /// <summary>
    /// A button: a verb, enabled or not, and what happens when it is pressed.
    ///
    /// ⚠ <see cref="Enabled"/> greys the button AND its marks and label in one act. Setting
    /// `interactable` alone is how a dead control kept the brightest mark on the card.
    /// </summary>
    public sealed class ButtonHandle : Handle
    {
        internal readonly ButtonRef Ref;
        private Action _clicked;
        private bool _wired;

        internal ButtonHandle(ButtonRef button) { Ref = button; }

        internal override GameObject Object => Ref?.Component != null ? Ref.Component.gameObject : null;

        /// <summary>The label inside, for a hint or a mark on it.</summary>
        public LabelHandle Text => Ref?.ButtonText != null ? new LabelHandle(Ref.ButtonText, TextPolicy.Dynamic) : null;

        /// <summary>Whether it can be pressed. Greys the label and the scope marks with it.</summary>
        public bool Enabled
        {
            get => Ref?.Component != null && Ref.Component.interactable;
            set
            {
                if (Ref?.Component == null) return;
                Ref.Component.interactable = value;
                ScopeMarks.Tint(Ref, value);
            }
        }

        /// <summary>The verb on it, translated as it is written.</summary>
        public string Label
        {
            set { Text?.Say(value); }
        }

        /// <summary>Recolour for another purpose — a confirm that turns dangerous.</summary>
        public ButtonTone Tone
        {
            set { if (Object != null) UIStyles.SetBackground(Object, Tones.ButtonFill(value)); }
        }

        /// <summary>Pressed. Every subscriber runs, in order.</summary>
        public event Action Clicked
        {
            add
            {
                _clicked += value;
                if (_wired || Ref == null) return;
                _wired = true;
                // ButtonRef's own listener, compiled inside UniverseLib for both runtimes — the
                // only place a UnityEvent may be subscribed from the Core (see CLAUDE.md).
                Ref.OnClick += () => _clicked?.Invoke();
            }
            remove { _clicked -= value; }
        }

        /// <summary>
        /// Busy: disabled under a label that says what is happening ("Fetching…"), until
        /// <see cref="Enabled"/> and <see cref="Label"/> are set again.
        /// </summary>
        public void Busy(string english)
        {
            Label = english;
            Enabled = false;
        }

        /// <summary>Which copy this button aims at, when it changes after construction.</summary>
        public void Retarget(EditSide side) { ScopeMarks.Retarget(Ref, side); }
    }

    /// <summary>A text field: its text, whether it may be typed into, and when it changed.</summary>
    public sealed class FieldHandle : Handle
    {
        internal readonly InputFieldRef Ref;

        internal FieldHandle(InputFieldRef field) { Ref = field; }

        internal override GameObject Object => Ref?.Component != null ? Ref.Component.gameObject : null;

        public string Text
        {
            get => Ref?.Text ?? "";
            set { if (Ref != null) Ref.Text = value ?? ""; }
        }

        public bool Enabled
        {
            get => Ref?.Component != null && Ref.Component.interactable;
            set { if (Ref?.Component != null) Ref.Component.interactable = value; }
        }

        /// <summary>Typed into. InputFieldRef's own C# event — never a UnityEvent from the Core.</summary>
        public event Action<string> Changed
        {
            add { if (Ref != null) Ref.OnValueChanged += value; }
            remove { if (Ref != null) Ref.OnValueChanged -= value; }
        }
    }

    /// <summary>A box that is on or off.</summary>
    public sealed class ToggleHandle : Handle
    {
        internal readonly Toggle Toggle;
        internal readonly GameObject Row;
        private readonly LabelHandle _label;

        internal ToggleHandle(GameObject row, Toggle toggle, LabelHandle label)
        {
            Row = row;
            Toggle = toggle;
            _label = label;
        }

        internal override GameObject Object => Row;

        /// <summary>The words beside the box.</summary>
        public LabelHandle Text => _label;

        public bool IsOn
        {
            get => Toggle != null && Toggle.isOn;
            set { if (Toggle != null) Toggle.isOn = value; }
        }

        public bool Enabled
        {
            get => Toggle != null && Toggle.interactable;
            set
            {
                if (Toggle == null) return;
                Toggle.interactable = value;
                if (_label != null) _label.Tone = value ? Tone.Plain : Tone.Muted;
            }
        }

        /// <summary>Flipped by the person. Through UIHelpers: the Il2Cpp delegate conversion lives there.</summary>
        public void OnChanged(Action<bool> handler) { UIHelpers.AddToggleListener(Toggle, handler); }
    }

    /// <summary>The colours behind the tones — one table, read by every component.</summary>
    internal static class Tones
    {
        public static Color Colour(Tone tone)
        {
            switch (tone)
            {
                case Tone.Secondary: return UIStyles.TextSecondary;
                case Tone.Muted: return UIStyles.TextMuted;
                case Tone.Accent: return UIStyles.TextAccent;
                case Tone.Success: return UIStyles.StatusSuccess;
                case Tone.Warning: return UIStyles.StatusWarning;
                case Tone.Error: return UIStyles.StatusError;
                case Tone.Info: return UIStyles.StatusInfo;
                case Tone.Neutral: return UIStyles.StatusNeutral;
                default: return UIStyles.TextPrimary;
            }
        }

        public static Color ButtonFill(ButtonTone tone)
        {
            switch (tone)
            {
                case ButtonTone.Primary: return UIStyles.ButtonPrimary;
                case ButtonTone.Success: return UIStyles.ButtonSuccess;
                case ButtonTone.Warning: return UIStyles.ButtonWarning;
                case ButtonTone.Danger: return UIStyles.ButtonDanger;
                case ButtonTone.Link: return UIStyles.ButtonLink;
                default: return UIStyles.ButtonSecondary;
            }
        }

        public static TextAnchor Anchor(Placement placement)
        {
            switch (placement)
            {
                case Placement.TopLeft: return TextAnchor.UpperLeft;
                case Placement.TopCenter: return TextAnchor.UpperCenter;
                case Placement.TopRight: return TextAnchor.UpperRight;
                case Placement.MiddleCenter: return TextAnchor.MiddleCenter;
                case Placement.MiddleRight: return TextAnchor.MiddleRight;
                case Placement.BottomLeft: return TextAnchor.LowerLeft;
                case Placement.BottomCenter: return TextAnchor.LowerCenter;
                case Placement.BottomRight: return TextAnchor.LowerRight;
                default: return TextAnchor.MiddleLeft;
            }
        }
    }
}
