using System;
using System.Collections.Generic;
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

        internal BuiltScreen(ScreenDocument doc) { _doc = doc; }

        internal void Add(string name, LabelHandle label) => _labels[name] = label;
        internal void Add(string name, ButtonHandle button) => _buttons[name] = button;
        internal void Add(string name, Host host) => _hosts[name] = host;
        internal void Add(string name, StatusLine status) => _statuses[name] = status;

        public LabelHandle Label(string name) => _labels.TryGetValue(name, out var l) ? l : throw new ScreenDocumentException($"{_doc.Name}: no label named '{name}'");
        public ButtonHandle Button(string name) => _buttons.TryGetValue(name, out var b) ? b : throw new ScreenDocumentException($"{_doc.Name}: no button named '{name}'");
        public Host Host(string name) => _hosts.TryGetValue(name, out var h) ? h : throw new ScreenDocumentException($"{_doc.Name}: no host named '{name}'");
        public StatusLine Status(string name) => _statuses.TryGetValue(name, out var s) ? s : throw new ScreenDocumentException($"{_doc.Name}: no status line named '{name}'");

        /// <summary>
        /// Write a slot. The document names it and says which piece holds it; the code says what
        /// goes there, in the interface's language — translated at this moment, as any Dynamic text.
        /// </summary>
        public void Say(string bind, string text)
        {
            if (!_doc.Binds.TryGetValue(bind, out var node))
                throw new ScreenDocumentException($"{_doc.Name}: no slot named '{bind}'");
            if (node.Kind == "button") Button(node.Name).Label = text;
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
        public static BuiltScreen Build(ScreenDocument doc, Host body, Host footer, Func<string, Action> actOf)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (actOf == null) throw new ArgumentNullException(nameof(actOf));

            var built = new BuiltScreen(doc);
            foreach (var node in doc.Body) Place(doc, node, body, built, actOf);
            foreach (var node in doc.Footer) Place(doc, node, footer, built, actOf);
            return built;
        }

        private static void Place(ScreenDocument doc, ScreenNode node, Host parent, BuiltScreen built, Func<string, Action> actOf)
        {
            switch (node.Kind)
            {
                case "card":
                {
                    var host = Stacks.Card(parent, node.Name, node.Int("width") ?? doc.CardWidth);
                    built.Add(node.Name, host);
                    foreach (var child in node.Children) Place(doc, child, host, built, actOf);
                    break;
                }
                case "stack":
                {
                    var host = Stacks.Vertical(parent, node.Name, Spacing(node) ?? 0);
                    built.Add(node.Name, host);
                    foreach (var child in node.Children) Place(doc, child, host, built, actOf);
                    break;
                }
                case "row":
                {
                    // A plain row unless the document says more: then the horizontal stack, with
                    // its padding, placement, surface, fill and floor spelt out.
                    bool plain = node.Word("pad") == null && node.Int("pad") == null && node.Word("placement") == null
                                 && node.Word("surface") == null && node.Word("fill") == null
                                 && node.Word("minHeight") == null && node.Int("minHeight") == null;
                    Host host;
                    if (plain)
                        host = Stacks.Row(parent, node.Name, Spacing(node) ?? 10);
                    else
                    {
                        int pad = Spacing(node, "pad") ?? 0;
                        host = Stacks.Horizontal(parent, node.Name, Spacing(node) ?? 0, Pad.All(pad),
                                                 Enum(node.Word("placement"), Placement.MiddleLeft),
                                                 Enum(node.Word("surface"), Surface.None),
                                                 Enum(node.Word("fill"), Fill.Stretch),
                                                 MinHeight(node));
                    }
                    host.Visible = node.StartsVisible;
                    built.Add(node.Name, host);
                    foreach (var child in node.Children) Place(doc, child, host, built, actOf);
                    break;
                }
                case "spacer":
                    built.Add(node.Name, Stacks.Spacer(parent, node.Int("height") ?? 0, node.Name));
                    break;
                case "status":
                    built.Add(node.Name, StatusLine.Create(parent, node.Name, node.Flag("centred") ?? true));
                    break;
                case "label":
                {
                    // A bound text is written at show time, so it is Dynamic whatever the document
                    // says: translated at the moment it is written, never registered as static UI text.
                    var policy = node.Bind != null ? TextPolicy.Dynamic : Enum(node.Word("policy"), TextPolicy.UiText);
                    var label = Labels.Create(parent, node.Name, node.Text ?? "",
                                              Enum(node.Word("role"), TextRole.Body),
                                              tone: node.Word("tone") != null ? Enum(node.Word("tone"), Tone.Plain) : (Tone?)null,
                                              centred: node.Flag("centred"),
                                              policy: policy,
                                              fill: Enum(node.Word("fill"), Fill.Content),
                                              minHeight: MinHeight(node),
                                              autoHeight: node.Flag("autoHeight") ?? false);
                    // Said only to override the role's own choice — a Hint is italic unless told otherwise.
                    if (node.Flag("italic") is bool italic) label.Italic = italic;
                    if (node.Flag("bold") is bool bold) label.Bold = bold;
                    label.Visible = node.StartsVisible;
                    built.Add(node.Name, label);
                    break;
                }
                case "button":
                {
                    var policy = node.Bind != null ? TextPolicy.Dynamic : Enum(node.Word("policy"), TextPolicy.UiText);
                    var size = node.Word("size") == "Compact" ? ButtonSize.Compact : ButtonSize.Normal;
                    // Where the verb writes, as two facts; the mark beside the label follows.
                    EditSide? scope = null;
                    if (node.Props["scope"] is Newtonsoft.Json.Linq.JObject scopeFacts)
                        scope = EditScope.SideAfter((bool)scopeFacts["onThisMachine"], (bool)scopeFacts["yourPublishedCopy"]);
                    var button = Buttons.Create(parent, node.Name, node.Text ?? "",
                                                Enum(node.Word("tone"), ButtonTone.Secondary), size,
                                                minWidth: node.Int("minWidth"), fill: Enum(node.Word("fill"), Fill.Content),
                                                scope: scope, policy: policy);
                    var handler = actOf(node.Act)
                                  ?? throw new ScreenDocumentException($"{doc.Name}: the act '{node.Act}' has no handler");
                    button.Clicked += handler;
                    button.Visible = node.StartsVisible;
                    built.Add(node.Name, button);
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
                case "RowHeightNormal": return UIStyles.RowHeightNormal;
                case "RowHeightLarge": return UIStyles.RowHeightLarge;
                case "RowHeightXLarge": return UIStyles.RowHeightXLarge;
                case "InputHeight": return UIStyles.InputHeight;
                case "MultiLineSmall": return UIStyles.MultiLineSmall;
                case "CodeDisplayHeight": return UIStyles.CodeDisplayHeight;
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
