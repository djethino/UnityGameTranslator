using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The screens described in data (common/spec/screens): every document parses into the closed
    /// vocabulary, says what it asks of the code, and a document the vocabulary does not cover is
    /// refused whole rather than drawn half.
    /// </summary>
    internal static class ScreenDocumentChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string folder = Find("common", "spec", "screens");
            check(folder != null, "the screens are found", "this check reads them; without them, it proves nothing");
            if (folder == null) return;

            // ── Every shipped screen parses ───────────────────────────────────
            var screens = Directory.GetFiles(folder, "*.json").Where(f => Path.GetFileName(f) != "schema.json").ToList();
            check(screens.Count > 0, $"{screens.Count} screen(s) to read", "a rule with nothing to guard is decoration");
            foreach (var file in screens)
            {
                string name = Path.GetFileName(file);
                try
                {
                    var doc = ScreenDocument.FromFile(file);
                    check(doc.Nodes.Count > 0 && doc.Footer.Count > 0,
                        $"{name} parses: {doc.Nodes.Count} pieces, {doc.Binds.Count} slot(s), {doc.Acts.Count} act(s)",
                        "a screen has pieces, and a footer with at least one verb");
                }
                catch (ScreenDocumentException e)
                {
                    check(false, $"{name} parses", e.Message);
                }
            }

            // ── The vocabulary is one list ────────────────────────────────────
            var schema = JObject.Parse(File.ReadAllText(Path.Combine(folder, "schema.json")));
            var inSchema = schema["$defs"]["node"]["properties"]["kind"]["enum"].Select(t => (string)t).ToList();
            check(inSchema.SequenceEqual(ScreenDocument.Kinds),
                "the schema's kinds are ScreenDocument.Kinds", $"schema {string.Join(",", inSchema)} — mod {string.Join(",", ScreenDocument.Kinds)}");

            // ── The confirm dialog says exactly what its code writes and handles ──
            var confirm = ScreenDocument.FromFile(Path.Combine(folder, "confirm.json"));
            check(confirm.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "message", "title", "verb" }),
                "confirm.json asks for the three slots its code writes", $"got {string.Join(",", confirm.Binds.Keys)}");
            check(confirm.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "cancel", "confirm" }),
                "confirm.json asks for the two acts its code handles", $"got {string.Join(",", confirm.Acts.Keys)}");
            check(confirm.Binds["verb"].Kind == "button" && confirm.Binds["title"].Kind == "label",
                "a slot knows which kind of piece holds it", "Say() writes a button's label or a label's text accordingly");
            check(confirm.Name == "Confirm" && confirm.Width == 400 && confirm.MinHeight == 150 && !confirm.Persist && confirm.CardWidth == 360,
                "identity, size and chrome come from the document", "what TranslatorPanelBase used to get from overrides");

            // ── The settings choice: a frame with a host the code fills ────────
            var choice = ScreenDocument.FromFile(Path.Combine(folder, "settings-choice.json"));
            check(choice.Binds.Keys.SequenceEqual(new[] { "intro" }) && choice.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "apply", "cancel", "compare" }),
                "settings-choice.json asks for one slot and three acts", $"got {string.Join(",", choice.Binds.Keys)} / {string.Join(",", choice.Acts.Keys)}");
            check(choice.Nodes.TryGetValue("Sections", out var sections) && sections.Kind == "stack" && sections.Children.Count == 0,
                "the rows' host is a stack the document leaves empty", "one row per section both sides changed, built at show time");
            check(choice.Acts["compare"].Props["scope"] != null,
                "Compare says where it writes", "two buttons that read identically write to opposite sides; the mark is what tells them apart");

            // ── The login: pieces that start hidden, a status line, four acts ──
            var login = ScreenDocument.FromFile(Path.Combine(folder, "login.json"));
            check(login.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "cancel", "copy", "openWebsite", "start" })
                  && login.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "code", "instructions" }),
                "login.json asks for four acts and two slots", $"got {string.Join(",", login.Acts.Keys)} / {string.Join(",", login.Binds.Keys)}");
            check(!login.Nodes["CodeRow"].StartsVisible && !login.Nodes["OpenWebsiteBtn"].StartsVisible && login.Nodes["StartLoginBtn"].StartsVisible,
                "the code row and Open Website start hidden, Start Login shown", "which is shown when is the flow's business, in code; the document only says how it starts");
            check(login.Nodes["Status"].Kind == "status" && login.Nodes["CodeLabel"].Word("role") == "Code",
                "a status line and a code label are named as such", "the code writes the one with a tone and the other with a device code");

            // ── The backups: a fixed header, a help bar, a host the code fills, one verb ──
            var backups = ScreenDocument.FromFile(Path.Combine(folder, "backups.json"));
            check(backups.Header.Count == 1 && backups.Header[0].Kind == "card" && backups.Header[0].Children.Count == 4,
                "backups.json has a header: one card, four pieces", "what somebody reads before choosing a row stays put while the rows scroll");
            check(backups.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "now", "privacy", "title" })
                  && backups.Binds.Values.All(n => backups.Header[0].Children.Contains(n)),
                "its three slots are all in the header", "the socle's words, written by the code rather than copied into the document");
            check(backups.Binds["now"].Word("policy") == "Excluded",
                "the line holding a count is Excluded", "a figure is written as it is, never through the mod's own translation");
            check(backups.Acts.Keys.SequenceEqual(new[] { "close" }),
                "backups.json asks for one act", $"got {string.Join(",", backups.Acts.Keys)}");
            check(backups.Nodes.TryGetValue("List", out var list) && list.Kind == "stack" && list.Children.Count == 0,
                "the blocks' host is a stack the document leaves empty", "two blocks built from the backups folder at show time, their heights a rule");
            check(backups.Help == "Hover an element to see what it does" && confirm.Help == null,
                "a help bar is declared by its resting sentence, and only where there is one", "what each control says there is the code's");
            check(backups.MinWidth == 560 && backups.MinHeight == backups.Height,
                "the document says no height floor: that floor is measured", "what two lists cost at two rows each is known once they are laid out, not before");

            // ── Refusals ──────────────────────────────────────────────────────
            Refuses(check, "a help bar with no sentence", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""chrome"":{""help"":""""},""body"":[],""footer"":[]}", "resting sentence");
            Refuses(check, "a name used in the header and again in the body", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""header"":[{""kind"":""spacer"",""name"":""A"",""height"":1}],""body"":[{""kind"":""spacer"",""name"":""A"",""height"":1}],""footer"":[]}", "used twice");
            Refuses(check, "an unknown kind", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""gauge"",""name"":""G""}],""footer"":[]}", "gauge");
            Refuses(check, "a button without an act", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[],""footer"":[{""kind"":""button"",""name"":""B"",""text"":""Go""}]}", "asks for no act");
            Refuses(check, "a name used twice", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""spacer"",""name"":""A"",""height"":1},{""kind"":""spacer"",""name"":""A"",""height"":1}],""footer"":[]}", "used twice");
            Refuses(check, "a label with neither text nor bind", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""L""}],""footer"":[]}", "has a text or a bind");
            Refuses(check, "a slot written into two pieces", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""A"",""text"":{""bind"":""t""}},{""kind"":""label"",""name"":""B"",""text"":{""bind"":""t""}}],""footer"":[]}", "two pieces");
            Refuses(check, "children under a label", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""L"",""text"":""x"",""children"":[]}],""footer"":[]}", "holds nothing");
            Refuses(check, "a screen without a size", @"{""name"":""X"",""body"":[],""footer"":[]}", "has a size");

            var defaults = ScreenDocument.Parse(JObject.Parse(@"{""name"":""X"",""size"":{""width"":500,""height"":200},""body"":[],""footer"":[]}"));
            check(defaults.MinWidth == 500 && defaults.MinHeight == 200 && defaults.Backdrop && defaults.Persist && defaults.CardWidth == 460,
                "what a document leaves unsaid takes the base's defaults", "minimums equal the size, backdrop and persistence on, cards the width minus the margins");
        }

        private static void Refuses(Action<bool, string, string> check, string what, string json, string expectedWord)
        {
            try
            {
                ScreenDocument.Parse(JObject.Parse(json));
                check(false, $"{what} is refused", "it parsed");
            }
            catch (ScreenDocumentException e)
            {
                check(e.Message.Contains(expectedWord, StringComparison.Ordinal), $"{what} is refused", e.Message);
            }
        }

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
