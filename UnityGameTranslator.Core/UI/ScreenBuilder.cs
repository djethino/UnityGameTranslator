using System;
using System.Collections.Generic;
using System.Linq;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// A screen once built: its pieces by name, and the slots the code writes.
    /// </summary>
    internal sealed class BuiltScreen
    {
        private readonly ScreenDocument _doc;
        private readonly Dictionary<string, LabelHandle> _labels = new Dictionary<string, LabelHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, ButtonHandle> _buttons = new Dictionary<string, ButtonHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, Host> _hosts = new Dictionary<string, Host>(StringComparer.Ordinal);
        private readonly Dictionary<string, StatusLine> _statuses = new Dictionary<string, StatusLine>(StringComparer.Ordinal);
        private readonly Dictionary<string, FieldHandle> _fields = new Dictionary<string, FieldHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, SearchableDropdown> _dropdowns = new Dictionary<string, SearchableDropdown>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScrollList> _lists = new Dictionary<string, ScrollList>(StringComparer.Ordinal);
        private readonly Dictionary<string, TabBar> _tabBars = new Dictionary<string, TabBar>(StringComparer.Ordinal);
        private readonly Dictionary<string, Collapsible> _collapsibles = new Dictionary<string, Collapsible>(StringComparer.Ordinal);

        internal BuiltScreen(ScreenDocument doc) { _doc = doc; }

        internal void Add(string name, TabBar tabs) => _tabBars[name] = tabs;
        internal void Add(string name, Collapsible collapsible) => _collapsibles[name] = collapsible;

        public TabBar Tabs(string name) => _tabBars.TryGetValue(name, out var t) ? t : throw new ScreenDocumentException($"{_doc.Name}: no row of tabs named '{name}'");
        public Collapsible Collapsible(string name) => _collapsibles.TryGetValue(name, out var c) ? c : throw new ScreenDocumentException($"{_doc.Name}: no collapsible named '{name}'");

        internal void Add(string name, LabelHandle label) => _labels[name] = label;
        internal void Add(string name, ButtonHandle button) => _buttons[name] = button;
        internal void Add(string name, Host host) => _hosts[name] = host;
        internal void Add(string name, StatusLine status) => _statuses[name] = status;
        internal void Add(string name, FieldHandle field) => _fields[name] = field;
        internal void Add(string name, SearchableDropdown dropdown) => _dropdowns[name] = dropdown;
        internal void Add(string name, ScrollList list) => _lists[name] = list;
        internal void Add(string name, ToggleHandle toggle) => _toggles[name] = toggle;
        private readonly Dictionary<string, ToggleHandle> _toggles = new Dictionary<string, ToggleHandle>(StringComparer.Ordinal);
        public ToggleHandle Toggle(string name) => _toggles.TryGetValue(name, out var t) ? t : throw new ScreenDocumentException($"{_doc.Name}: no checkbox named '{name}'");
        internal void Add(string name, Toasts toast) => _toasts[name] = toast;
        private readonly Dictionary<string, Toasts> _toasts = new Dictionary<string, Toasts>(StringComparer.Ordinal);
        internal void Add(string name, SliderHandle slider) => _sliders[name] = slider;
        private readonly Dictionary<string, SliderHandle> _sliders = new Dictionary<string, SliderHandle>(StringComparer.Ordinal);
        public SliderHandle Slider(string name) => _sliders.TryGetValue(name, out var s) ? s : throw new ScreenDocumentException($"{_doc.Name}: no slider named '{name}'");
        public Toasts Toast(string name) => _toasts.TryGetValue(name, out var t) ? t : throw new ScreenDocumentException($"{_doc.Name}: no toast named '{name}'");

        public LabelHandle Label(string name) => _labels.TryGetValue(name, out var l) ? l : throw new ScreenDocumentException($"{_doc.Name}: no label named '{name}'");
        public ButtonHandle Button(string name) => _buttons.TryGetValue(name, out var b) ? b : throw new ScreenDocumentException($"{_doc.Name}: no button named '{name}'");
        public Host Host(string name) => _hosts.TryGetValue(name, out var h) ? h : throw new ScreenDocumentException($"{_doc.Name}: no host named '{name}'");
        public StatusLine Status(string name) => _statuses.TryGetValue(name, out var s) ? s : throw new ScreenDocumentException($"{_doc.Name}: no status line named '{name}'");
        public FieldHandle Field(string name) => _fields.TryGetValue(name, out var f) ? f : throw new ScreenDocumentException($"{_doc.Name}: no field named '{name}'");
        public SearchableDropdown Dropdown(string name) => _dropdowns.TryGetValue(name, out var d) ? d : throw new ScreenDocumentException($"{_doc.Name}: no dropdown named '{name}'");
        public ScrollList List(string name) => _lists.TryGetValue(name, out var s) ? s : throw new ScreenDocumentException($"{_doc.Name}: no list named '{name}'");

        /// <summary>
        /// Write a slot. The document names it and says which piece holds it; the code says what
        /// goes there, in the interface's language — translated at this moment, as any Dynamic text.
        /// </summary>
        public void Say(string bind, string text)
        {
            if (!_doc.Binds.TryGetValue(bind, out var node))
                throw new ScreenDocumentException($"{_doc.Name}: no slot named '{bind}'");
            if (node.Kind == "button") Button(node.Name).Label = text;
            // A slot the document marks Excluded holds a figure or somebody's own words — written
            // as they are, never through the mod's own translation.
            else if (node.Word("policy") == "Excluded") Label(node.Name).Show(text);
            else Label(node.Name).Say(text);
        }
    }

    /// <summary>
    /// The interpreter for this engine: reads a ScreenDocument and builds it with the vocabulary
    /// (UI/Components), piece by piece, name by name. A Core on another engine has its own.
    ///
    /// ⚠ One switch, closed on ScreenDocument.Kinds: a kind the document may carry and this does
    /// not draw is refused here, loudly, rather than drawn as nothing.
    /// </summary>
    internal static class ScreenBuilder
    {
        /// <param name="actOf">The handler for each act the document asks for; asked once per button, at build time, so an act nobody handles fails the build and not the click.</param>
        /// <param name="header">Where the document's fixed header goes — required when it has one, since a header drawn into the scrolling body would scroll.</param>
        /// <param name="help">The help bar the document's `help` sentences go to — required when the document has any, and the panel's to create from the document's own resting sentence.</param>
        /// <param name="layoutChanged">Told when a piece changes the screen's height on its own — a collapsible opening — so the panel can size itself again.</param>
        /// <param name="title">The panel's own way of making a title with its scope switch (<c>TranslatorPanelBase.ScopedTitle</c>) — required when the document has a `title`, because the base keeps hold of the strip for the window's resizes.</param>
        public static BuiltScreen Build(ScreenDocument doc, Host body, Host footer, Func<string, Action> actOf,
                                        Host header = null, HelpZone help = null, Action layoutChanged = null,
                                        Func<Host, string, string, EditSide, TextPolicy, LabelHandle> title = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (actOf == null) throw new ArgumentNullException(nameof(actOf));
            if (doc.Header.Count > 0 && header == null)
                throw new ScreenDocumentException($"{doc.Name}: the document has a header and the panel gave it nowhere fixed to go");
            if (doc.Help != null && help == null)
                throw new ScreenDocumentException($"{doc.Name}: the document declares a help bar and the panel built none");
            if (title == null && doc.Nodes.Values.Any(n => n.Kind == "title"))
                throw new ScreenDocumentException($"{doc.Name}: the document has a title with a scope switch and the panel gave no way to make one");

            var built = new BuiltScreen(doc);
            var site = new Site { Doc = doc, Built = built, ActOf = actOf, Help = help, Body = body, LayoutChanged = layoutChanged, Title = title };
            foreach (var node in doc.Header) Place(site, node, header);
            foreach (var node in doc.Body) Place(site, node, body);
            foreach (var node in doc.Footer) Place(site, node, footer);
            return built;
        }

        /// <summary>What every piece is placed with — carried down the tree rather than passed five times.</summary>
        private sealed class Site
        {
            public ScreenDocument Doc;
            public BuiltScreen Built;
            public Func<string, Action> ActOf;
            public HelpZone Help;
            /// <summary>The scrolling body — where a row of tabs placed in the header puts its contents.</summary>
            public Host Body;
            public Action LayoutChanged;
            public Func<Host, string, string, EditSide, TextPolicy, LabelHandle> Title;
        }

        /// <summary>Where a verb writes, as the document's two facts; null when the piece says nothing.</summary>
        private static EditSide? ScopeOf(ScreenNode node)
            => node.Props["scope"] is Newtonsoft.Json.Linq.JObject facts
               ? EditScope.SideAfter((bool)facts["onThisMachine"], (bool)facts["yourPublishedCopy"])
               : (EditSide?)null;

        /// <summary>A piece's padding: one figure for all sides, or one across and one down.</summary>
        private static Pad PadOf(ScreenNode node)
        {
            int all = Spacing(node, "pad") ?? 0;
            int x = Spacing(node, "padX") ?? all;
            int y = Spacing(node, "padY") ?? all;
            return Pad.Of(x, y);
        }

        private static bool HasPad(ScreenNode node)
            => node.Props["pad"] != null || node.Props["padX"] != null || node.Props["padY"] != null;

        /// <summary>The help sentence the document gives a piece, attached to what was built for it.</summary>
        private static void Describe(Site site, ScreenNode node, Handle handle)
        {
            var text = ScreenDocument.HelpOf(node);
            if (text != null) site.Help.Describe(handle, text);
        }

        private static Action Act(Site site, ScreenNode node)
            => site.ActOf(node.Act)
               ?? throw new ScreenDocumentException($"{site.Doc.Name}: the act '{node.Act}' has no handler");

        private static void Place(Site site, ScreenNode node, Host parent)
        {
            var doc = site.Doc;
            var built = site.Built;
            switch (node.Kind)
            {
                case "card":
                {
                    var host = Stacks.Card(parent, node.Name, node.Int("width") ?? doc.CardWidth,
                                           stretchVertically: node.Flag("stretch") ?? false,
                                           surface: Enum(node.Word("surface"), Surface.Card));
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, host);
                    Describe(site, node, host);
                    foreach (var child in node.Children) Place(site, child, host);
                    break;
                }
                case "tabs":
                {
                    // The buttons where the piece is placed, the contents in the body — or in the
                    // piece the document names, a tab of another row for sub-tabs: a row of tabs
                    // in the header stays put while what it shows scrolls.
                    var bar = new TabBar();
                    var contents = node.Word("contentsIn") != null ? built.Host(node.Word("contentsIn")) : site.Body;
                    bar.CreateUI(parent, contents, node.Int("rowHeight") ?? 32);
                    built.Add(node.Name, bar);
                    foreach (var tab in node.Children)
                    {
                        var content = bar.Tab(tab.Text);
                        built.Add(tab.Name, content);
                        Describe(site, tab, bar.Button(tab.Text));
                        foreach (var child in tab.Children) Place(site, child, content);
                    }
                    break;
                }
                case "callout":
                {
                    var tone = Enum(node.Word("tone"), CalloutTone.Info);
                    Pad? pad = HasPad(node) ? PadOf(node) : (Pad?)null;
                    Host host = node.Word("direction") == "Stack"
                        ? Callout.Box(parent, node.Name, tone, Spacing(node) ?? 5, pad, MinHeight(node) ?? 0)
                        : Callout.HorizontalBox(parent, node.Name, tone, Spacing(node) ?? 8, pad,
                                                Enum(node.Word("placement"), Placement.MiddleLeft), MinHeight(node) ?? 0);
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, host);
                    Describe(site, node, host);
                    foreach (var child in node.Children) Place(site, child, host);
                    break;
                }
                case "collapsible":
                {
                    // Opening it changes the screen's height: the panel is told, so it sizes itself again.
                    var changed = site.LayoutChanged;
                    var block = Collapsible.Create(parent, node.Name, node.Word("title"),
                                                   expanded: node.Flag("expanded") ?? false,
                                                   onToggled: changed != null ? (Action<bool>)(_ => changed()) : null);
                    built.Add(node.Name, block);
                    built.Add(node.Name, block.Body);
                    Describe(site, node, block.Handle);
                    foreach (var child in node.Children) Place(site, child, block.Body);
                    break;
                }
                case "section":
                {
                    var host = Stacks.Section(parent, node.Name, MinHeight(node) ?? 0);
                    built.Add(node.Name, host);
                    Describe(site, node, host);
                    foreach (var child in node.Children) Place(site, child, host);
                    break;
                }
                case "stack":
                {
                    var host = Stacks.Vertical(parent, node.Name, Spacing(node) ?? 0, PadOf(node),
                                               surface: Enum(node.Word("surface"), Surface.None),
                                               fill: Enum(node.Word("fill"), Fill.Stretch),
                                               minHeight: MinHeight(node),
                                               fillHeight: node.Flag("fillHeight") ?? false);
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, host);
                    Describe(site, node, host);
                    foreach (var child in node.Children) Place(site, child, host);
                    break;
                }
                case "row":
                {
                    // The panel's ordinary row — its own padding, a floor, a placement — unless the
                    // document says more: then the horizontal stack, with padding, surface and fill
                    // spelt out.
                    bool bare = !HasPad(node) && node.Word("surface") == null && node.Word("fill") == null;
                    Host host;
                    if (bare)
                        host = Stacks.Row(parent, node.Name, Spacing(node) ?? 10, MinHeight(node),
                                          Enum(node.Word("placement"), Placement.MiddleLeft));
                    else
                    {
                        host = Stacks.Horizontal(parent, node.Name, Spacing(node) ?? 0, PadOf(node),
                                                 Enum(node.Word("placement"), Placement.MiddleLeft),
                                                 Enum(node.Word("surface"), Surface.None),
                                                 Enum(node.Word("fill"), Fill.Stretch),
                                                 MinHeight(node));
                    }
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, host);
                    Describe(site, node, host);
                    foreach (var child in node.Children) Place(site, child, host);
                    break;
                }
                case "spacer":
                    built.Add(node.Name, Stacks.Spacer(parent, node.Int("height") ?? 0, node.Name));
                    break;
                case "status":
                    built.Add(node.Name, StatusLine.Create(parent, node.Name, node.Flag("centred") ?? true));
                    break;
                case "toast":
                    built.Add(node.Name, Toasts.Create(parent, node.Name));
                    break;
                case "slider":
                {
                    Action changed = node.Act != null ? Act(site, node) : null;
                    float min = node.Number("min") ?? 0f, max = node.Number("max") ?? 1f;
                    Func<float, string> format = node.Word("format") == "Percent"
                        ? (Func<float, string>)(v => $"{v * 100f:0}%")
                        : v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    // Its value at show time is the code's; built at the floor of its range.
                    var slider = Sliders.Labelled(parent, node.Name, node.Word("caption"), min, max, min, format,
                                                  onChanged: changed != null ? (Action<float>)(_ => changed()) : null,
                                                  captionWidth: node.Int("captionWidth") ?? 120,
                                                  wholeNumbers: node.Flag("wholeNumbers") ?? false);
                    built.Add(node.Name, slider);
                    Describe(site, node, slider);
                    break;
                }
                case "title":
                {
                    // Made by the panel, not here: the base keeps the strip for the window's resizes.
                    var policy = node.Bind != null
                        ? (node.Word("policy") == "Excluded" ? TextPolicy.Excluded : TextPolicy.Dynamic)
                        : Enum(node.Word("policy"), TextPolicy.UiText);
                    var label = site.Title(parent, node.Name, node.Text ?? "", ScopeOf(node).Value, policy);
                    built.Add(node.Name, label);
                    Describe(site, node, label);
                    break;
                }
                case "checkbox":
                {
                    Action changed = node.Act != null ? Act(site, node) : null;
                    var onChanged = changed != null ? (Action<bool>)(_ => changed()) : null;
                    // Without words the box is bare: its words are elsewhere on its row.
                    var toggle = node.Text == null
                        ? CheckBoxes.Bare(parent, node.Name, initial: node.Flag("initial") ?? false, onChanged: onChanged)
                        : CheckBoxes.Create(parent, node.Name, node.Text,
                                            initial: node.Flag("initial") ?? false,
                                            onChanged: onChanged,
                                            policy: Enum(node.Word("policy"), TextPolicy.UiText),
                                            tone: Enum(node.Word("tone"), Tone.Plain),
                                            fill: Enum(node.Word("fill"), Fill.Content));
                    toggle.Visible = node.StartsVisible;
                    built.Add(node.Name, toggle);
                    Describe(site, node, toggle);
                    break;
                }
                case "field":
                {
                    // With a caption, the field and its words share a row of their own.
                    var field = node.Word("caption") != null
                        ? Fields.Captioned(parent, node.Name, node.Word("caption"), node.Word("placeholder") ?? "",
                                           Enum(node.Word("input"), FieldKind.Text),
                                           captionWidth: node.Int("captionWidth") ?? 120,
                                           fieldMinWidth: node.Int("minWidth"),
                                           fieldFill: Enum(node.Word("fill"), Fill.Stretch))
                        : Fields.Create(parent, node.Name, node.Word("placeholder") ?? "",
                                        Enum(node.Word("input"), FieldKind.Text),
                                        minHeight: MinHeight(node),
                                        fill: Enum(node.Word("fill"), Fill.Stretch),
                                        minWidth: node.Int("minWidth"));
                    if (node.Act != null)
                    {
                        var changed = Act(site, node);
                        field.Changed += _ => changed();
                    }
                    field.Visible = node.StartsVisible;
                    built.Add(node.Name, field);
                    Describe(site, node, field);
                    break;
                }
                case "dropdown":
                {
                    // The choices come from where the document says, never from the document: the
                    // languages are the catalogue's, the one list every product offers.
                    SearchableDropdown dropdown;
                    switch (node.Word("options"))
                    {
                        case "languages":
                        {
                            // The catalogue's list, with one answer before it when the document
                            // says so ("auto (Detect)") — a choice that is not a language.
                            var names = LanguageHelper.GetLanguageNames();
                            string first = node.Word("first");
                            if (first != null)
                            {
                                var withFirst = new string[names.Length + 1];
                                withFirst[0] = first;
                                Array.Copy(names, 0, withFirst, 1, names.Length);
                                names = withFirst;
                            }
                            dropdown = SearchableDropdown.ForLanguages(node.Name, names, first ?? "");
                            break;
                        }
                        case "code":
                            // The code sets the choices and the initial one at show time (SetOptions, SelectedValue).
                            dropdown = new SearchableDropdown(node.Name, new string[0], "", node.Int("popupHeight") ?? 200);
                            break;
                        default:
                            throw new ScreenDocumentException($"{doc.Name}: '{node.Name}' takes its choices from '{node.Word("options")}', which this engine does not offer");
                    }
                    var changed = Act(site, node);
                    var host = dropdown.CreateUI(parent, _ => changed(), node.Int("width") ?? 200,
                                                 stretch: node.Flag("stretch") ?? false,
                                                 minHeight: MinHeight(node));
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, dropdown);
                    Describe(site, node, host);
                    break;
                }
                case "list":
                {
                    var list = ScrollList.Create(parent, node.Name,
                                                 minHeight: node.Int("minHeight") ?? 0,
                                                 preferredHeight: node.Int("preferredHeight"),
                                                 fillHeight: node.Flag("fill") ?? true,
                                                 emptyText: node.Word("empty"),
                                                 spacing: node.Int("spacing") ?? 5,
                                                 padding: node.Int("padding") ?? 5);
                    list.Visible = node.StartsVisible;
                    built.Add(node.Name, list);
                    Describe(site, node, list.Handle);
                    break;
                }
                case "label":
                {
                    // A bound text is written at show time, so it is Dynamic — translated at the
                    // moment it is written, never registered as static UI text — unless the document
                    // marks it Excluded: a count, a date, a name somebody wrote, shown as is.
                    var policy = node.Bind != null
                        ? (node.Word("policy") == "Excluded" ? TextPolicy.Excluded : TextPolicy.Dynamic)
                        : Enum(node.Word("policy"), TextPolicy.UiText);
                    var label = Labels.Create(parent, node.Name, node.Text ?? "",
                                              Enum(node.Word("role"), TextRole.Body),
                                              tone: node.Word("tone") != null ? Enum(node.Word("tone"), Tone.Plain) : (Tone?)null,
                                              centred: node.Flag("centred"),
                                              policy: policy,
                                              wrap: node.Flag("wrap") ?? true,
                                              fill: Enum(node.Word("fill"), Fill.Content),
                                              minHeight: MinHeight(node),
                                              autoHeight: node.Flag("autoHeight") ?? false,
                                              minWidth: node.Int("minWidth"),
                                              align: node.Word("align") != null ? Enum(node.Word("align"), Placement.MiddleLeft) : (Placement?)null);
                    // Said only to override the role's own choice — a Hint is italic unless told otherwise.
                    if (node.Flag("italic") is bool italic) label.Italic = italic;
                    if (node.Flag("bold") is bool bold) label.Bold = bold;
                    label.Visible = node.StartsVisible;
                    built.Add(node.Name, label);
                    Describe(site, node, label);
                    break;
                }
                case "button":
                {
                    // A bound verb is Dynamic like a bound label, and Excluded on the same terms:
                    // a version number written on a button is shown as it is.
                    var policy = node.Bind != null
                        ? (node.Word("policy") == "Excluded" ? TextPolicy.Excluded : TextPolicy.Dynamic)
                        : Enum(node.Word("policy"), TextPolicy.UiText);
                    var size = Enum(node.Word("size"), ButtonSize.Normal);
                    // Where the verb writes, as two facts; the mark beside the label follows.
                    var button = Buttons.Create(parent, node.Name, node.Text ?? "",
                                                Enum(node.Word("tone"), ButtonTone.Secondary), size,
                                                minWidth: node.Int("minWidth"), fill: Enum(node.Word("fill"), Fill.Content),
                                                scope: ScopeOf(node), policy: policy);
                    button.Clicked += Act(site, node);
                    button.Visible = node.StartsVisible;
                    built.Add(node.Name, button);
                    Describe(site, node, button);
                    break;
                }
                default:
                    throw new ScreenDocumentException($"{doc.Name}: this engine's builder does not draw '{node.Kind}'");
            }
        }

        private static int? MinHeight(ScreenNode node)
        {
            if (node.Int("minHeight") is int pixels) return pixels;
            switch (node.Word("minHeight"))
            {
                case null: return null;
                case "RowHeightSmall": return UIStyles.RowHeightSmall;
                case "RowHeightMedium": return UIStyles.RowHeightMedium;
                case "RowHeightNormal": return UIStyles.RowHeightNormal;
                case "MultiLineMedium": return UIStyles.MultiLineMedium;
                case "MultiLineLarge": return UIStyles.MultiLineLarge;
                case "RowHeightLarge": return UIStyles.RowHeightLarge;
                case "RowHeightXLarge": return UIStyles.RowHeightXLarge;
                case "InputHeight": return UIStyles.InputHeight;
                case "MultiLineSmall": return UIStyles.MultiLineSmall;
                case "CodeDisplayHeight": return UIStyles.CodeDisplayHeight;
                case "ButtonHeight": return UIStyles.ButtonHeight;
                // A row of a screen's own buttons: the button, and the room a row of them keeps around itself.
                case "ButtonRowHeight": return UIStyles.ButtonHeight + 16;
                default: throw new ScreenDocumentException($"'{node.Name}': '{node.Word("minHeight")}' is not a height the theme names");
            }
        }

        private static int? Spacing(ScreenNode node, string prop = "spacing")
        {
            if (node.Int(prop) is int pixels) return pixels;
            switch (node.Word(prop))
            {
                case null: return null;
                case "SmallSpacing": return UIStyles.SmallSpacing;
                case "ElementSpacing": return UIStyles.ElementSpacing;
                default: throw new ScreenDocumentException($"'{node.Name}': '{node.Word(prop)}' is not a spacing the theme names");
            }
        }

        private static T Enum<T>(string word, T fallback) where T : struct
        {
            if (word == null) return fallback;
            return System.Enum.TryParse(word, out T value)
                ? value
                : throw new ScreenDocumentException($"'{word}' is not a {typeof(T).Name}");
        }
    }
}
