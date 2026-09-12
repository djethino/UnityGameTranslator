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

        public int? Int(string prop) => Props[prop] is JValue v && v.Type == JTokenType.Integer ? (int?)(int)v : null;
        public string Word(string prop) => Props[prop] is JValue v && v.Type == JTokenType.String ? (string)v : null;
        public bool? Flag(string prop) => Props[prop] is JValue v && v.Type == JTokenType.Boolean ? (bool?)(bool)v : null;
    }

    /// <summary>
    /// A screen of the mod as a document (common/spec/screens/*.json): its identity and size,
    /// the tree of its body, the row of its footer, and what it asks of the code — the `bind`
    /// slots it expects written and the `act` verbs it expects handled.
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
        public static readonly string[] Kinds = { "card", "stack", "row", "spacer", "label", "button" };

        public string Name { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int MinWidth { get; private set; }
        public int MinHeight { get; private set; }
        public bool Backdrop { get; private set; } = true;
        public bool Persist { get; private set; } = true;
        /// <summary>The width the body's cards are laid out for; the window's width minus its margins when the document says nothing.</summary>
        public int CardWidth { get; private set; }

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
                doc.CardWidth = (int?)chrome["cardWidth"] ?? 0;
            }
            if (doc.CardWidth <= 0) doc.CardWidth = doc.Width - 40;

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

        private void ReadInto(List<ScreenNode> into, JArray nodes)
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

                switch (node.Kind)
                {
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
                }

                if (obj["children"] is JArray children)
                {
                    if (node.Kind == "label" || node.Kind == "button" || node.Kind == "spacer")
                        throw new ScreenDocumentException($"{Name}: a {node.Kind} holds nothing");
                    ReadInto(node.Children, children);
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
