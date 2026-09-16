using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>One piece of a screen: its kind, its name, what the document says about it, and what it holds.</summary>
    public sealed class ScreenNode
    {
        public string Kind;
        public string Name;
        public JObject Props;
        public readonly List<ScreenNode> Children = new List<ScreenNode>();

        /// <summary>The words, when the document gives them; null when the text is a bind.</summary>
        public string Text => Props["text"] is JValue v ? (string)v : null;

        /// <summary>The slot the code writes at show time, when the text is one; null otherwise.</summary>
        public string Bind => Props["text"] is JObject o ? (string)o["bind"] : null;

        /// <summary>The verb a button asks for; null on anything else.</summary>
        public string Act => (string)Props["act"];

        /// <summary>Hidden at first when the document says so; the code shows it when its moment comes.</summary>
        public bool StartsVisible => Flag("visible") ?? true;

        public int? Int(string prop) => Props[prop] is JValue v && v.Type == JTokenType.Integer ? (int?)(int)v : null;
        public float? Number(string prop) => Props[prop] is JValue v && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (float?)(float)v : null;
        public string Word(string prop) => Props[prop] is JValue v && v.Type == JTokenType.String ? (string)v : null;
        public bool? Flag(string prop) => Props[prop] is JValue v && v.Type == JTokenType.Boolean ? (bool?)(bool)v : null;
    }

    /// <summary>
    /// A screen of the mod as a document (common/spec/screens/*.json): its identity and size,
    /// the fixed header when it has one, the tree of its body, the row of its footer, and what it
    /// asks of the code — the `bind` slots it expects written and the `act` verbs it expects handled.
    ///
    /// 🔴 The document says the SHAPE and never a rule. What a label shows, whether a verb is
    /// offered, which tone it takes — those are decided in code from the facts, and written into
    /// the screen through the binds. That is what lets a Core on another engine draw the same
    /// screen from the same file: its interpreter reads kinds and names, its code reads the same
    /// facts, and the rules stay where the corpus can hold them.
    ///
    /// ⚠ Pure by contract: Newtonsoft only. Parsing REFUSES a document the vocabulary does not
    /// cover (an unknown kind, a nameless piece, a button without an act, a name used twice): a
    /// screen that half-parses is a screen that draws half, and the schema beside the documents
    /// says the same thing to check-spec.py. Loaded once, from the assembly's own copy of the
    /// spec (the mod ships as one file) or from the spec folder (the checks).
    /// </summary>
    public sealed class ScreenDocument
    {
        /// <summary>The closed vocabulary. The same list as the schema's enum — a check says so.</summary>
        public static readonly string[] Kinds = { "card", "stack", "row", "spacer", "label", "button", "status", "section", "field", "dropdown", "list", "tabs", "tab", "callout", "collapsible", "title", "checkbox", "toast", "slider" };

        /// <summary>What the help bar says over this piece, or null.</summary>
        public static string HelpOf(ScreenNode node) => node.Word("help");

        public string Name { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int MinWidth { get; private set; }
        public int MinHeight { get; private set; }
        public bool Backdrop { get; private set; } = true;
        public bool Persist { get; private set; } = true;
        /// <summary>The bar with the screen's name and its close button; off for a corner notification.</summary>
        public bool TitleBar { get; private set; } = true;
        /// <summary>Hangs from a screen corner chosen at run time instead of being centred: never dragged or resized by hand.</summary>
        public bool Pinned { get; private set; }
        /// <summary>The width the body's cards are laid out for; the window's width minus its margins when the document says nothing.</summary>
        public int CardWidth { get; private set; }
        /// <summary>The resting sentence of the help bar above the footer; null for a screen with no such bar.</summary>
        public string Help { get; private set; }

        /// <summary>The fixed part between the title bar and the body — what stays put while the body scrolls. Empty for most screens.</summary>
        public readonly List<ScreenNode> Header = new List<ScreenNode>();
        public readonly List<ScreenNode> Body = new List<ScreenNode>();
        public readonly List<ScreenNode> Footer = new List<ScreenNode>();

        /// <summary>The slots the code writes, by name, each with the piece that holds it.</summary>
        public readonly Dictionary<string, ScreenNode> Binds = new Dictionary<string, ScreenNode>(StringComparer.Ordinal);

        /// <summary>The verbs the code handles, by name, each with the button that asks for it.</summary>
        public readonly Dictionary<string, ScreenNode> Acts = new Dictionary<string, ScreenNode>(StringComparer.Ordinal);

        /// <summary>Every piece by name, in document order.</summary>
        public readonly Dictionary<string, ScreenNode> Nodes = new Dictionary<string, ScreenNode>(StringComparer.Ordinal);

        public static ScreenDocument Parse(JObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var doc = new ScreenDocument();

            doc.Name = (string)root["name"];
            if (string.IsNullOrEmpty(doc.Name)) throw new ScreenDocumentException("a screen has a name");

            var size = root["size"] as JObject ?? throw new ScreenDocumentException($"{doc.Name}: a screen has a size");
            doc.Width = Required(size, "width", doc.Name);
            doc.Height = Required(size, "height", doc.Name);
            doc.MinWidth = (int?)size["minWidth"] ?? doc.Width;
            doc.MinHeight = (int?)size["minHeight"] ?? doc.Height;

            if (root["chrome"] is JObject chrome)
            {
                doc.Backdrop = (bool?)chrome["backdrop"] ?? true;
                doc.Persist = (bool?)chrome["persist"] ?? true;
                doc.TitleBar = (bool?)chrome["titleBar"] ?? true;
                doc.Pinned = (bool?)chrome["pinned"] ?? false;
                doc.CardWidth = (int?)chrome["cardWidth"] ?? 0;
                doc.Help = (string)chrome["help"];
                if (doc.Help != null && doc.Help.Length == 0)
                    throw new ScreenDocumentException($"{doc.Name}: a help bar has a resting sentence");
            }
            if (doc.CardWidth <= 0) doc.CardWidth = doc.Width - 40;

            if (root["header"] is JArray header) doc.ReadInto(doc.Header, header);
            doc.ReadInto(doc.Body, root["body"] as JArray ?? throw new ScreenDocumentException($"{doc.Name}: a screen has a body"));
            doc.ReadInto(doc.Footer, root["footer"] as JArray ?? throw new ScreenDocumentException($"{doc.Name}: a screen has a footer"));
            return doc;
        }

        public static ScreenDocument FromFile(string path)
            => Parse(JObject.Parse(File.ReadAllText(path)));

        /// <summary>
        /// The document as shipped inside the assembly: the spec's screens are embedded at build
        /// time under <c>screens/&lt;name&gt;.json</c>, so the one DLL the mod is carries its own screens.
        /// </summary>
        public static ScreenDocument FromEmbedded(string screen)
        {
            var assembly = typeof(ScreenDocument).Assembly;
            string resource = $"screens/{screen}.json";
            using (var stream = assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                    throw new ScreenDocumentException($"the screen '{screen}' is not embedded in {assembly.GetName().Name} (resource '{resource}')");
                using (var reader = new StreamReader(stream))
                    return Parse(JObject.Parse(reader.ReadToEnd()));
            }
        }

        private void ReadInto(List<ScreenNode> into, JArray nodes, string parentKind = null)
        {
            foreach (var item in nodes)
            {
                var obj = item as JObject ?? throw new ScreenDocumentException($"{Name}: a piece is an object");
                var node = new ScreenNode
                {
                    Kind = (string)obj["kind"],
                    Name = (string)obj["name"],
                    Props = obj,
                };

                if (string.IsNullOrEmpty(node.Kind) || Array.IndexOf(Kinds, node.Kind) < 0)
                    throw new ScreenDocumentException($"{Name}: '{node.Name ?? "?"}' is of kind '{node.Kind}', which the vocabulary does not have");
                if (string.IsNullOrEmpty(node.Name))
                    throw new ScreenDocumentException($"{Name}: a {node.Kind} has a name");
                if (Nodes.ContainsKey(node.Name))
                    throw new ScreenDocumentException($"{Name}: the name '{node.Name}' is used twice");
                Nodes[node.Name] = node;

                // A tab is nothing else's child, and a row of tabs holds nothing else.
                if (node.Kind == "tab" && parentKind != "tabs")
                    throw new ScreenDocumentException($"{Name}: the tab '{node.Name}' is not in a row of tabs");
                if (parentKind == "tabs" && node.Kind != "tab")
                    throw new ScreenDocumentException($"{Name}: '{node.Name}' is a {node.Kind} in a row of tabs, which holds only tabs");

                switch (node.Kind)
                {
                    case "tab":
                        if (node.Text == null)
                            throw new ScreenDocumentException($"{Name}: the tab '{node.Name}' has a text");
                        break;
                    case "callout":
                        if (node.Word("tone") == null)
                            throw new ScreenDocumentException($"{Name}: the callout '{node.Name}' has a tone");
                        break;
                    case "collapsible":
                        if (node.Word("title") == null)
                            throw new ScreenDocumentException($"{Name}: the collapsible '{node.Name}' has a title");
                        break;
                    case "title":
                        // The scope switch is the point of a title: which copy the screen writes
                        // to, on every screen that shows translation lines, without exception.
                        if (!(node.Props["scope"] is JObject))
                            throw new ScreenDocumentException($"{Name}: the title '{node.Name}' says which copy the screen writes to (scope)");
                        goto case "label";
                    case "slider":
                        if (node.Word("caption") == null || node.Props["min"] == null || node.Props["max"] == null)
                            throw new ScreenDocumentException($"{Name}: the slider '{node.Name}' has a caption and a range");
                        goto case "field";
                    case "checkbox":
                    case "field":
                        // A box without words is bare: its words are elsewhere on its row. All three
                        // may ask for an act as they change, or be read by the code when it needs them.
                        if (node.Act != null)
                        {
                            if (Acts.ContainsKey(node.Act))
                                throw new ScreenDocumentException($"{Name}: the act '{node.Act}' is asked for by two pieces");
                            Acts[node.Act] = node;
                        }
                        break;
                    case "label":
                    case "button":
                        if (node.Text == null && node.Bind == null)
                            throw new ScreenDocumentException($"{Name}: '{node.Name}' has a text or a bind");
                        if (node.Bind != null)
                        {
                            if (Binds.ContainsKey(node.Bind))
                                throw new ScreenDocumentException($"{Name}: the bind '{node.Bind}' is written into two pieces");
                            Binds[node.Bind] = node;
                        }
                        if (node.Kind == "button")
                        {
                            if (string.IsNullOrEmpty(node.Act))
                                throw new ScreenDocumentException($"{Name}: the button '{node.Name}' asks for no act");
                            if (Acts.ContainsKey(node.Act))
                                throw new ScreenDocumentException($"{Name}: the act '{node.Act}' is asked for by two buttons");
                            Acts[node.Act] = node;
                        }
                        break;
                    case "spacer":
                        if (node.Int("height") == null)
                            throw new ScreenDocumentException($"{Name}: the spacer '{node.Name}' has a height");
                        break;
                    case "dropdown":
                        // The choices are never written in a document: they come from a source
                        // every product shares, named here.
                        if (node.Word("options") == null)
                            throw new ScreenDocumentException($"{Name}: the dropdown '{node.Name}' says where its choices come from");
                        if (string.IsNullOrEmpty(node.Act))
                            throw new ScreenDocumentException($"{Name}: the dropdown '{node.Name}' asks for no act");
                        if (Acts.ContainsKey(node.Act))
                            throw new ScreenDocumentException($"{Name}: the act '{node.Act}' is asked for by two pieces");
                        Acts[node.Act] = node;
                        break;
                }

                if (HelpOf(node) != null && Help == null)
                    throw new ScreenDocumentException($"{Name}: '{node.Name}' has a help sentence and the screen declares no help bar");

                if (obj["children"] is JArray children)
                {
                    if (node.Kind == "label" || node.Kind == "button" || node.Kind == "spacer" || node.Kind == "status"
                        || node.Kind == "field" || node.Kind == "dropdown" || node.Kind == "list"
                        || node.Kind == "title" || node.Kind == "checkbox" || node.Kind == "toast" || node.Kind == "slider")
                        throw new ScreenDocumentException($"{Name}: a {node.Kind} holds nothing");
                    ReadInto(node.Children, children, node.Kind);
                }
                else if (node.Kind == "tabs")
                {
                    throw new ScreenDocumentException($"{Name}: the row of tabs '{node.Name}' holds no tab");
                }

                into.Add(node);
            }
        }

        private static int Required(JObject size, string prop, string name)
        {
            return (int?)size[prop] ?? throw new ScreenDocumentException($"{name}: size.{prop} is required");
        }
    }

    public sealed class ScreenDocumentException : Exception
    {
        public ScreenDocumentException(string message) : base(message) { }
    }
}
