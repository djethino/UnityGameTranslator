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
                    check(doc.Nodes.Count > 0 && doc.Acts.Count > 0,
                        $"{name} parses: {doc.Nodes.Count} pieces, {doc.Binds.Count} slot(s), {doc.Acts.Count} act(s)",
                        "a screen has pieces, and at least one verb somewhere on it");
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

            // ── The upload setup: a field, two dropdowns, a list, help on the controls ──
            var setup = ScreenDocument.FromFile(Path.Combine(folder, "upload-setup.json"));
            check(setup.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "cancel", "continue", "search", "sourceChanged", "targetChanged" }),
                "upload-setup.json asks for five acts, two of them from dropdowns", $"got {string.Join(",", setup.Acts.Keys)}");
            check(setup.Acts["sourceChanged"].Kind == "dropdown" && setup.Acts["sourceChanged"].Word("options") == "languages",
                "a dropdown asks for an act when its choice changes, and names where its choices come from", "the languages are the catalogue's, never a list in a document");
            check(setup.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "game", "gameSource", "legend", "searchStatus", "validation" }),
                "its five slots are the lines the code writes from the facts", $"got {string.Join(",", setup.Binds.Keys)}");
            check(setup.Nodes["ResultsScroll"].Kind == "list" && setup.Nodes["ResultsScroll"].Children.Count == 0
                  && setup.Nodes["GameSearchInput"].Kind == "field" && setup.Nodes["GameBox"].Kind == "section",
                "a list the code fills, a field the code reads, a section around them", "forms, not rules");
            check(ScreenDocument.HelpOf(setup.Nodes["GameSearchInput"]) != null && ScreenDocument.HelpOf(setup.Nodes["Source"]) != null
                  && ScreenDocument.HelpOf(setup.Nodes["ContinueBtn"]) != null && ScreenDocument.HelpOf(setup.Nodes["Title"]) == null,
                "the help sentences sit on the controls, not on the words", "what the bar says over each control is the document's; the bar itself is chrome.help");
            check(!setup.Nodes["TargetSettled"].StartsVisible && !setup.Nodes["SourceSettled"].StartsVisible,
                "the notes under the two languages start hidden", "shown once the code has read that the file states that language — the target with its first line, the source under strict detection");

            // ── The main screen: a row of tabs in the header, its contents in the body ─────
            var main = ScreenDocument.FromFile(Path.Combine(folder, "main.json"));
            check(main.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] {
                      "backups", "close", "compare", "contribute", "createIndependent", "ctaLogin", "download", "downloadLatest",
                      "editDetails", "fork", "loginLogout", "mergeWithMain", "modManager", "modUpdate", "options", "resourcesOpen",
                      "review", "search", "transParams", "updateFromMain", "upload" }),
                "main.json asks for the twenty-one acts its code handles", $"got {string.Join(",", main.Acts.Keys)}");
            check(main.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] {
                      "account", "aiStatus", "backups", "branchDesc", "communityGame", "downloadDesc", "entries", "guidance",
                      "loginLogout", "mergeDesc", "modManager", "modUpdate", "modUpdateVerb", "resourcesBy", "resourcesUrl",
                      "role", "roleActionsHint", "source", "syncStatus", "target", "upload", "uploadHint" }),
                "its slots are the lines the code writes on every redraw", $"got {string.Join(",", main.Binds.Keys)}");
            check(main.Body.Count == 0 && main.Header.Count == 5 && main.Header[4].Kind == "tabs" && main.Header[4].Children.Count == 2
                  && main.Header[4].Children.All(t => t.Kind == "tab" && t.Text != null && ScreenDocument.HelpOf(t) != null),
                "the row of tabs stays in the header and holds two named tabs; the body is theirs",
                "the buttons stay put while what they show scrolls — the builder puts a tab's contents in the body");
            check(main.Nodes["StatusCardHost"].Kind == "stack" && main.Nodes["StatusCardHost"].Children.Count == 0
                  && main.Nodes["TranslationListHost"].Kind == "stack" && main.Nodes["TranslationListHost"].Children.Count == 0,
                "the status card and the community list have hosts the document leaves empty", "two components the vocabulary does not describe, built by the code");
            check(main.Nodes["ModUpdateBanner"].Kind == "callout" && main.Nodes["ModUpdateBanner"].Word("tone") == "Success" && !main.Nodes["ModUpdateBanner"].StartsVisible
                  && main.Nodes["Glossary"].Kind == "collapsible" && main.Nodes["Glossary"].Flag("expanded") == false
                  && !main.Nodes["ResourcesLinkSection"].StartsVisible,
                "the update banner is a hidden callout, the glossary a folded collapsible, the resources block hidden", "shown by the code when their moment comes");
            check(main.Nodes["DownloadLatestBtn"].Bind == null && main.Nodes["DownloadLatestBtn"].Word("policy") == "Excluded"
                  && main.Nodes["ReviewBtn"].Bind == null && main.Nodes["ReviewBtn"].Word("policy") == "Dynamic",
                "a verb the code rewrites only on some paths keeps its words in the document", "a bound verb starts empty; these must read right before the first refresh that reaches them");
            check(main.Nodes["MergeWithMainBtn"].Act == "mergeWithMain" && main.Nodes["UpdateFromMainBtn"].Act == "updateFromMain"
                  && main.Nodes["ForkBtn"].Act == "fork" && main.Nodes["CreateIndependentBtn"].Act == "createIndependent",
                "the two doors to one act ask for it under two names", "an act is asked for by one button; the code answers both names with the same handler");

            // ── The upload screen: a scoped title, two boxes the code reads, a status line ──
            var upload = ScreenDocument.FromFile(Path.Combine(folder, "upload.json"));
            check(upload.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "back", "cancel", "upload" }),
                "upload.json asks for three acts", $"got {string.Join(",", upload.Acts.Keys)}");
            check(upload.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "entries", "game", "modeInfo", "statusInherited" }),
                "its four slots are the lines the code writes on every opening", $"got {string.Join(",", upload.Binds.Keys)}");
            check(upload.Nodes["TitleLabel"].Kind == "title" && upload.Nodes["TitleLabel"].Props["scope"] != null
                  && upload.Nodes["TitleLabel"].Word("policy") == "Dynamic" && upload.Nodes["TitleLabel"].Bind == null,
                "the title carries its scope switch and stays Dynamic with its words in the document",
                "the code retitles it per mode (Update, Contribute, Fork, Edit details) but not while the server is still being asked");
            check(upload.Nodes["StatusToggle"].Kind == "checkbox" && upload.Nodes["AcceptBranchesToggle"].Kind == "checkbox"
                  && upload.Nodes["StatusToggle"].Act == null && upload.Nodes["AcceptBranchesToggle"].Act == null,
                "the two declarations are boxes the code reads, asking for no act", "read at the moment of sending, never acted on");
            check(upload.Nodes["Status"].Kind == "status" && !upload.Nodes["BackBtn"].StartsVisible
                  && upload.Nodes["UploadBtn"].Props["scope"] != null && upload.Nodes["UploadBtn"].Word("policy") == "Dynamic",
                "a status line, a Back hidden until a setup comes back, and the verb that publishes carrying its mark", "the button that actually publishes says where it writes");

            // ── The corner notification: a pinned window with no title bar, one stack of hidden boxes ──
            var overlay = ScreenDocument.FromFile(Path.Combine(folder, "overlay.json"));
            check(overlay.Pinned && !overlay.TitleBar && !overlay.Backdrop && !overlay.Persist && overlay.Footer.Count == 0,
                "overlay.json is a corner, not a window: pinned, no title bar, no backdrop, nothing remembered, no footer",
                "the corner itself is a setting, applied by the code");
            check(overlay.Body.Count == 1 && overlay.Body[0].Kind == "stack" && overlay.Body[0].Int("pad") != null && overlay.Body[0].Int("spacing") != null,
                "one stack, whose spacing and padding the document states in pixels", "the code sizes the window from them — read there, never copied");
            check(overlay.Body[0].Children.All(b => !b.StartsVisible || b.Kind == "toast")
                  && overlay.Body[0].Children.Count(b => b.Kind == "callout") == 4 && overlay.Nodes["ToastBox"].Kind == "toast",
                "every box starts hidden: four callouts, a connection line, a toast", "the code shows each when its moment comes");
            check(overlay.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] {
                      "modDownload", "modIgnore", "modManager", "syncAction", "syncBranch", "syncCompare", "syncFork",
                      "syncIgnore", "syncSettings", "webNotifDismiss", "webNotifView" }),
                "overlay.json asks for the eleven acts its code handles", $"got {string.Join(",", overlay.Acts.Keys)}");
            check(overlay.Nodes["ConnectionDot"].Flag("wrap") == false && overlay.Nodes["ConnectionDot"].Int("minWidth") == 12,
                "the connection dot keeps its own glyph's width and never folds", "it is what keeps the words flush against it on the right");
            check(!upload.Pinned && upload.TitleBar && ScreenDocument.Parse(JObject.Parse(@"{""name"":""X"",""size"":{""width"":500,""height"":200},""body"":[],""footer"":[]}")).TitleBar,
                "a window keeps its title bar and is not pinned unless its document says so", "the defaults are the ordinary window's");

            // ── Every panel built from a document hands the builder what the document needs ──
            // 🔴 The builder refuses a document with a header or a help bar it was given nowhere
            // to put — at construction, inside CreatePanels, which then aborts: the panels after
            // it are never made and a half-built one stays on screen at its raw size. Reported as
            // "le panel backup prend toute la page quand j'ouvre un jeu". Read off the source,
            // since a panel cannot be constructed here.
            string panels = Find("UnityGameTranslator", "UnityGameTranslator.Core", "UI", "Panels");
            check(panels != null, "the panels are found", "without them this proves nothing");
            if (panels != null)
            {
                foreach (var file in Directory.GetFiles(panels, "*.cs"))
                {
                    string source = File.ReadAllText(file);
                    var embedded = System.Text.RegularExpressions.Regex.Match(source, @"FromEmbedded\(""([a-z-]+)""\)");
                    if (!embedded.Success) continue;

                    var doc = ScreenDocument.FromFile(Path.Combine(folder, embedded.Groups[1].Value + ".json"));
                    var build = System.Text.RegularExpressions.Regex.Match(source, @"ScreenBuilder\.Build\([^;]*\);");
                    string panel = Path.GetFileName(file);
                    check(build.Success, $"{panel} builds its document", "a document read and never built is a screen with nothing on it");
                    if (!build.Success) continue;

                    check(doc.Header.Count == 0 || build.Value.Contains("header:"),
                        $"{panel} gives its header a fixed place", "the builder refuses a header with nowhere to go, at construction");
                    check(doc.Help == null || build.Value.Contains("help:"),
                        $"{panel} hands the builder its help bar", "the builder refuses a help bar it was not given, at construction");
                    check(!doc.Nodes.Values.Any(n => n.Kind == "title") || build.Value.Contains("title:"),
                        $"{panel} hands the builder its way of making a scoped title", "the base keeps the strip for the window's resizes; the builder refuses a title it cannot make, at construction");
                }
            }

            // ── Refusals ──────────────────────────────────────────────────────
            Refuses(check, "a dropdown that does not say where its choices come from", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""dropdown"",""name"":""D"",""act"":""pick""}],""footer"":[]}", "choices come from");
            Refuses(check, "a dropdown without an act", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""dropdown"",""name"":""D"",""options"":""languages""}],""footer"":[]}", "asks for no act");
            Refuses(check, "rows written into a list", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""list"",""name"":""L"",""children"":[]}],""footer"":[]}", "holds nothing");
            Refuses(check, "a help sentence on a screen with no help bar", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""field"",""name"":""F"",""help"":""Type here""}],""footer"":[]}", "no help bar");
            Refuses(check, "a help bar with no sentence", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""chrome"":{""help"":""""},""body"":[],""footer"":[]}", "resting sentence");
            Refuses(check, "a name used in the header and again in the body", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""header"":[{""kind"":""spacer"",""name"":""A"",""height"":1}],""body"":[{""kind"":""spacer"",""name"":""A"",""height"":1}],""footer"":[]}", "used twice");
            Refuses(check, "an unknown kind", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""gauge"",""name"":""G""}],""footer"":[]}", "gauge");
            Refuses(check, "a button without an act", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[],""footer"":[{""kind"":""button"",""name"":""B"",""text"":""Go""}]}", "asks for no act");
            Refuses(check, "a name used twice", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""spacer"",""name"":""A"",""height"":1},{""kind"":""spacer"",""name"":""A"",""height"":1}],""footer"":[]}", "used twice");
            Refuses(check, "a label with neither text nor bind", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""L""}],""footer"":[]}", "has a text or a bind");
            Refuses(check, "a slot written into two pieces", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""A"",""text"":{""bind"":""t""}},{""kind"":""label"",""name"":""B"",""text"":{""bind"":""t""}}],""footer"":[]}", "two pieces");
            Refuses(check, "children under a label", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""label"",""name"":""L"",""text"":""x"",""children"":[]}],""footer"":[]}", "holds nothing");
            Refuses(check, "a screen without a size", @"{""name"":""X"",""body"":[],""footer"":[]}", "has a size");
            Refuses(check, "a tab outside a row of tabs", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""tab"",""name"":""T"",""text"":""One""}],""footer"":[]}", "not in a row of tabs");
            Refuses(check, "a label in a row of tabs", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""tabs"",""name"":""R"",""children"":[{""kind"":""label"",""name"":""L"",""text"":""x""}]}],""footer"":[]}", "holds only tabs");
            Refuses(check, "a row of tabs with no tab", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""tabs"",""name"":""R""}],""footer"":[]}", "holds no tab");
            Refuses(check, "a tab without its words", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""tabs"",""name"":""R"",""children"":[{""kind"":""tab"",""name"":""T""}]}],""footer"":[]}", "has a text");
            Refuses(check, "a callout without a tone", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""callout"",""name"":""C""}],""footer"":[]}", "has a tone");
            Refuses(check, "a collapsible without a title", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""collapsible"",""name"":""C""}],""footer"":[]}", "has a title");
            Refuses(check, "a title without its scope", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""title"",""name"":""T"",""text"":""Upload""}],""footer"":[]}", "which copy");
            Refuses(check, "a checkbox without its words", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""checkbox"",""name"":""C""}],""footer"":[]}", "has a text");
            Refuses(check, "two checkboxes asking for one act", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""checkbox"",""name"":""A"",""text"":""a"",""act"":""flip""},{""kind"":""checkbox"",""name"":""B"",""text"":""b"",""act"":""flip""}],""footer"":[]}", "two pieces");

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
