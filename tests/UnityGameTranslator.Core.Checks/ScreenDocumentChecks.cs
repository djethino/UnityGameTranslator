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
            // The parts — templates alone, shared by screens — live under their folder and parse
            // with the same reader: a part has no pieces of its own, only templates.
            string partsFolder = Path.Combine(folder, "parts");
            var parts = Directory.Exists(partsFolder) ? Directory.GetFiles(partsFolder, "*.json").ToList() : new List<string>();
            check(screens.Count > 0 && parts.Count > 0, $"{screens.Count} screen(s) and {parts.Count} part(s) to read", "a rule with nothing to guard is decoration");
            foreach (var file in screens.Concat(parts))
            {
                string name = Path.GetFileName(file);
                try
                {
                    var doc = ScreenDocument.FromFile(file);
                    if (doc.IsPart)
                        check(doc.Nodes.Count == 0 && doc.Templates.Count > 0,
                            $"parts/{name} parses: {doc.Templates.Count} template(s) and no screen of its own",
                            "a part is what a component builds into a host a screen leaves for it");
                    else
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
                      "editDetails", "fix", "fork", "loginLogout", "mergeWithMain", "modManager", "modUpdate", "options", "resourcesOpen",
                      "review", "search", "transParams", "updateFromMain", "upload" }),
                "main.json asks for the twenty-two acts its code handles", $"got {string.Join(",", main.Acts.Keys)}");
            check(main.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] {
                      "account", "aiStatus", "backups", "branchDesc", "communityGame", "downloadDesc", "entries", "failures", "failuresFix", "guidance",
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
                  && overlay.Body[0].Children.Count(b => b.Kind == "callout") == 5 && overlay.Nodes["ToastBox"].Kind == "toast",
                "every box starts hidden: five callouts, a connection line, a toast", "the code shows each when its moment comes");
            check(overlay.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] {
                      "failuresFix", "failuresIgnore", "modDownload", "modIgnore", "modManager", "syncAction", "syncBranch", "syncCompare", "syncFork",
                      "syncIgnore", "syncSettings", "webNotifDismiss", "webNotifView" }),
                "overlay.json asks for the thirteen acts its code handles", $"got {string.Join(",", overlay.Acts.Keys)}");
            check(overlay.Nodes["ConnectionDot"].Flag("wrap") == false && overlay.Nodes["ConnectionDot"].Int("minWidth") == 12,
                "the connection dot keeps its own glyph's width and never folds", "it is what keeps the words flush against it on the right");
            check(!upload.Pinned && upload.TitleBar && ScreenDocument.Parse(JObject.Parse(@"{""name"":""X"",""size"":{""width"":500,""height"":200},""body"":[],""footer"":[]}")).TitleBar,
                "a window keeps its title bar and is not pinned unless its document says so", "the defaults are the ordinary window's");

            // ── Merge, inspector, wizard ──────────────────────────────────────────
            var merge = ScreenDocument.FromFile(Path.Combine(folder, "merge.json"));
            check(merge.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "apply", "cancel", "replace", "review" })
                  && merge.Nodes["ConflictScroll"].Kind == "list" && merge.Nodes["ConflictScroll"].Int("preferredHeight") != null
                  && merge.Nodes["BulkChoiceHost"].Kind == "row" && merge.Nodes["BulkChoiceHost"].Children.Count == 0,
                "merge.json: four acts, a list stating its preferred height, an empty host for the bulk choice",
                "the rows are built from the conflicts; the choice is rebuilt because it cannot be told back to nothing chosen");
            check(merge.Nodes["Title"].Kind == "title" && (bool)merge.Nodes["Title"].Props["scope"]["onThisMachine"] && !(bool)merge.Nodes["Title"].Props["scope"]["yourPublishedCopy"]
                  && (bool)merge.Nodes["ApplyBtn"].Props["scope"]["onThisMachine"] && !(bool)merge.Nodes["ApplyBtn"].Props["scope"]["yourPublishedCopy"],
                "the merge's title and Apply both say Local", "the whole merge settles this machine's file and publishes nothing");

            var inspector = ScreenDocument.FromFile(Path.Combine(folder, "inspector.json"));
            check(inspector.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "cameraChanged", "clearSelection", "excludePattern", "excludeThis", "exportOriginal", "markReplace", "stop" })
                  && inspector.Nodes["CameraTarget"].Word("options") == "code"
                  && inspector.Nodes["ImageActionRow"].StartsVisible == false && inspector.Nodes["TextEditRow"].StartsVisible == false && inspector.Nodes["ExclusionActionRow"].StartsVisible,
                "inspector.json: seven acts, the camera list the code's, the exclusion row up and the two other modes' rows down",
                "which row shows is the mode's, decided by the code");
            check(inspector.Nodes["TextEditScroll"].Int("minHeight") == 260,
                "the text-edit list's floor is stated once, in the document", "the code reads it there and revises the box once the list is filled");

            var wizard = ScreenDocument.FromFile(Path.Combine(folder, "wizard.json"));
            check(wizard.Body.Count == 7 && wizard.Body.All(s => s.Kind == "stack" && !s.StartsVisible) && wizard.Footer.Count == 0 && !wizard.Persist,
                "wizard.json: seven steps, all hidden, no shared footer, nothing remembered", "one step at a time, each with its own buttons; the window is sized to the step");
            check(wizard.Nodes["OnlineToggle"].Text == null && wizard.Nodes["OfflineToggle"].Text == null && wizard.Nodes["DeepLFreeToggle"].Text != null,
                "the two mode boxes are bare, the DeepL one carries its words", "a bare box's words are the title beside it, and the whole box is highlighted");
            check(wizard.Acts.Count == 31 && wizard.Acts.Values.Count(n => n.Kind == "field") == 5 && wizard.Acts.Values.Count(n => n.Kind == "dropdown") == 4
                  && wizard.Acts.Values.Count(n => n.Kind == "checkbox") == 4
                  && wizard.Nodes["AIUrl"].Word("placeholder") == UnityGameTranslator.Common.Endpoints.OllamaDefault,
                "wizard.json asks for 31 acts — five as fields are typed in, four as choices change, four as boxes flip — and offers the socle's default AI address",
                $"got {wizard.Acts.Count} acts; the address is {wizard.Nodes["AIUrl"].Word("placeholder")}");
            check(wizard.Nodes["HotkeyHost"].Children.Count == 0 && wizard.Nodes["TranslationListHost"].Children.Count == 0,
                "the hotkey capture and the community list have hosts the document leaves empty", "two components the vocabulary does not describe");

            // ── Tools and options: the two big tabbed screens ─────────────────────
            var tools = ScreenDocument.FromFile(Path.Combine(folder, "tools.json"));
            check(tools.Header.Count == 2 && tools.Header[0].Kind == "tabs" && tools.Header[0].Children.Count == 6
                  && tools.Header[1].Kind == "stack" && tools.Header[1].Children.Count == 1 && tools.Header[1].Children[0].Kind == "tabs"
                  && tools.Header[1].Children[0].Word("contentsIn") == "FontsTab" && tools.Header[1].Children[0].Int("rowHeight") == 26
                  && tools.Nodes["FontsTab"].Children.Count == 0,
                "tools.json: six tabs, and a row of sub-tabs in a header host whose contents go into the Fonts tab",
                "the sub-tab buttons are chrome, shown only while Fonts is open; the Fonts tab holds nothing but what they show");
            check(tools.Nodes.Values.Where(n => n.Kind == "list").All(n => n.Int("preferredHeight") != null)
                  && tools.Nodes.Values.Count(n => n.Kind == "list") == 11
                  && tools.Nodes.Values.Where(n => n.Kind == "list" && !n.StartsVisible).All(n => n.Flag("fill") == false),
                "every one of the eleven lists states its preferred height, and the hidden find lists take no spare height",
                "ScrollingListHeightRule: a list weighed at its minimum leaves the panel no slack");
            check(tools.Acts.Count == 27 && tools.Nodes["FontSharpness"].Word("options") == "code"
                  && (bool)tools.Nodes["TextEditorBtn"].Props["scope"]["onThisMachine"] && !(bool)tools.Nodes["TextEditorBtn"].Props["scope"]["yourPublishedCopy"],
                "tools.json asks for 27 acts (eight of them settle a failed line, three turning its pages); the sharpness choices are the GPU's; the editors write locally", $"got {tools.Acts.Count} acts");

            var options = ScreenDocument.FromFile(Path.Combine(folder, "options.json"));
            check(options.Header.Count == 1 && options.Header[0].Kind == "tabs" && options.Header[0].Children.Count == 5 && options.Body.Count == 0,
                "options.json: five tabs in the header, the body theirs", "the tab buttons stay put while the settings scroll");
            check(options.Nodes.Values.Count(n => n.Kind == "slider") == 2 && options.Nodes["OpacityFocused"].Number("min") == 0.4f
                  && options.Nodes["OpacityFocused"].Word("format") == "Percent",
                "the two opacity sliders floor at 40% and read as percentages", "lower is not translucent but unreadable — uGUI applies the alpha to the text too");
            check(options.Nodes["SourceLang"].Word("first") == "auto (Detect)" && options.Nodes["TargetLang"].Word("first") == "auto (System)",
                "the language pickers offer an answer before the catalogue's list", "auto is a choice that is not a language");
            check(options.Nodes.Values.Count(n => n.Kind == "field" && n.Word("caption") != null) == 7
                  && options.Nodes["MaxAttempts"].Word("input") == null,
                "the seven advanced numbers are captioned Text fields", "Decimal is locale-aware and refuses the dot these values are written with; validation happens at Apply");
            check(options.Nodes.Values.Count(n => n.Kind == "stack" && n.Name.EndsWith("Host")) == 11
                  && options.Nodes.Values.Where(n => n.Kind == "stack" && n.Name.EndsWith("Host")).All(n => n.Children.Count == 0),
                "eleven empty hosts, one per hotkey capture", "the capture control is the code's");
            check(options.Nodes["CaptureKeyboardWhy"].Bind != null && !options.Nodes["CaptureKeyboardWhy"].StartsVisible
                  && !options.Nodes["PauseWhy"].StartsVisible && !options.Nodes["PauseBlocked"].StartsVisible,
                "each capture box has a hidden line for the runtime's own reason; freezing has its three", "whether an intention can be honoured is the game's to say");
            check(options.Acts.Count == 38 && options.Nodes["AiAdvanced"].Kind == "collapsible" && options.Nodes["AiAdvanced"].Flag("expanded") == false,
                "options.json asks for 38 acts and folds the AI's advanced settings", $"got {options.Acts.Count} acts");

            // ── Templates: the rows of every list, described once, instantiated per element ──
            var templated = new Dictionary<string, int> {
                { "merge", 2 }, { "settings-choice", 1 }, { "upload-setup", 1 }, { "inspector", 1 }, { "tools", 10 }, { "backups", 5 } };
            foreach (var pair in templated)
            {
                var doc = ScreenDocument.FromFile(Path.Combine(folder, pair.Key + ".json"));
                check(doc.Templates.Count == pair.Value,
                    $"{pair.Key}.json describes {pair.Value} template(s)", $"got {doc.Templates.Count}: {string.Join(", ", doc.Templates.Keys)}");
                check(doc.Templates.Values.All(t => t.Pieces.Nodes.ContainsKey(t.Name) && !doc.Nodes.ContainsKey(t.Name)),
                    $"each template of {pair.Key}.json is named by its root, and by no piece of the screen", "the code reaches an instance by the template's own names");
            }
            check(backups.Templates["Entry"].Pieces.Nodes.ContainsKey("TextHost") && backups.Templates["EntryRenaming"].Pieces.Nodes.ContainsKey("TextHost"),
                "a name may repeat from one template to the next", "each instance is reached by its own names; two templates for two states of one row share them on purpose");
            check(backups.Templates["Entry"].Pieces.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "delete", "keep", "rename", "restore" })
                  && backups.Templates["EntryRenaming"].Pieces.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "cancel", "ok" }),
                "a template's acts are its own, answered per instance", "the code hands each instance the acts of the element it stands for");

            // ── Parts: the status card and the community list, described once for two screens ──
            var statusCard = ScreenDocument.FromFile(Path.Combine(partsFolder, "status-card.json"));
            check(statusCard.IsPart && statusCard.Body.Count == 0 && statusCard.Nodes.Count == 0 && statusCard.Templates.Keys.SequenceEqual(new[] { "Card" }),
                "status-card.json is a part: one template, no screen of its own", "the card is built by its component into the host main.json leaves for it");
            var card = statusCard.Templates["Card"];
            check(card.Pieces.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "dismiss", "manage" })
                  && card.Pieces.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "details", "identity", "legend", "notice", "noticeVerb", "secondary", "stage", "voteHint" }),
                "the card asks for two acts and eight slots", $"got {string.Join(",", card.Pieces.Acts.Keys)} / {string.Join(",", card.Pieces.Binds.Keys)}");
            check(card.Root.Kind == "stack" && card.Root.Word("pad") == "SectionPadding" && card.Root.Word("surface") == null,
                "the card is a section among sections: their padding, no surface of its own", "dressed as a card it read as a box of the wrong width stacked among the others");
            check(new[] { "IdentityMarks", "BadgeHost", "QualityRow", "Contributions", "VoteHost" }.All(h => card.Pieces.Nodes[h].Children.Count == 0)
                  && new[] { "QualityRow", "StageRow", "LegendRow", "EmptyRow", "ModeRow", "Contributions", "VoteRow", "DismissBtn" }.All(h => !card.Pieces.Nodes[h].StartsVisible),
                "five hosts the code fills (flags, chips, bar, kinds, votes) and every row after the details starts hidden", "which rows show is the standing's, decided in code");
            check(card.Pieces.Binds["secondary"].Word("policy") == null && card.Pieces.Binds["voteHint"].Word("policy") == null
                  && card.Pieces.Binds["identity"].Word("policy") == "Excluded" && card.Pieces.Binds["legend"].Word("policy") == "Excluded",
                "the two sentences the code says stay Dynamic; the lines it composes are Excluded", "a sentence is translated as it is written, a composed line is written as it is");

            var community = ScreenDocument.FromFile(Path.Combine(partsFolder, "community-list.json"));
            check(community.IsPart && community.Templates.Keys.OrderBy(k => k).SequenceEqual(new[] { "More", "Rank", "Row", "Rows", "Status" }),
                "community-list.json is a part of five templates", $"got {string.Join(",", community.Templates.Keys)}");
            check(community.Templates["Rows"].Root.Kind == "list" && community.Templates["Status"].Root.Kind == "label"
                  && community.Templates["Rows"].Root.Int("minHeight") == 200 && community.Templates["Rows"].Root.Flag("fill") != false,
                "the list and its status line are leaves placed once; the list states its floor and takes the spare height", "the two screens holding it used to pass the same figure");
            var row = community.Templates["Row"];
            check(row.Pieces.Acts.Keys.OrderBy(k => k).SequenceEqual(new[] { "pick", "select" }) && row.Pieces.Acts["select"].Kind == "checkbox"
                  && row.Pieces.Acts["select"].Text == null && row.Pieces.Acts["pick"].Kind == "row" && row.Pieces.Acts["pick"] == row.Root,
                "a row is chosen by pressing it anywhere, or its bare tick box", "two doors to one choice; written by the code, the box is not one");
            check(row.Root.Word("surface") == "Item" && row.Pieces.Nodes["Accent"].Int("minWidth") == 3 && row.Pieces.Nodes["Accent"].Flag("fillHeight") == true
                  && row.Pieces.Nodes["Accent"].Word("surface") == null,
                "a row sits on the item surface with a stripe three wide down its full height, painted by the code", "the stripe says 'the player's own' in the accent; which row that is, is a rule");
            check(row.Pieces.Binds.Keys.OrderBy(k => k).SequenceEqual(new[] { "author", "details", "facts", "note", "title" })
                  && new[] { "Title", "Facts", "Note", "Composition", "Arrow" }.All(n => !row.Pieces.Nodes[n].StartsVisible)
                  && row.Pieces.Nodes["From"].Children.Count == 0 && row.Pieces.Nodes["Into"].Children.Count == 0 && row.Pieces.Nodes["Votes"].Children.Count == 0
                  && row.Pieces.Nodes["Marks"].Children.Count == 0 && row.Pieces.Nodes["Pair"].Children.Any(n => n.Name == "Marks")
                  && row.Pieces.Nodes["Badges"].Children.Count == 0,
                "a row's optional lines start hidden; its flags, its marks, its chips, its bar and its votes have hosts", "what the server sent, and the reader's own library, decide which of them show — and a fork's origin is a chip, not a line");
            Refuses(check, "a row asking for an act a box already asks for", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""row"",""name"":""R"",""act"":""go"",""children"":[{""kind"":""checkbox"",""name"":""C"",""act"":""go""}]}],""footer"":[]}", "two pieces");

            // ── The components that own a part build it, and answer its acts ──
            string components = Find("UnityGameTranslator", "UnityGameTranslator.Core", "UI", "Components");
            check(components != null, "the components are found", "without them this proves nothing");
            if (components != null)
            {
                int owners = 0;
                foreach (var file in Directory.GetFiles(components, "*.cs"))
                {
                    string source = File.ReadAllText(file);
                    var embedded = System.Text.RegularExpressions.Regex.Match(source, @"FromEmbedded\(""parts/([a-z-]+)""\)");
                    if (!embedded.Success) continue;
                    owners++;

                    string component = Path.GetFileName(file);
                    var part = ScreenDocument.FromFile(Path.Combine(partsFolder, embedded.Groups[1].Value + ".json"));
                    check(part.IsPart && source.Contains("ScreenBuilder.Part("),
                        $"{component} owns a part and builds it through ScreenBuilder.Part", "a screen's templates are built from the screen; a part's from its component");
                    var acts = part.Templates.Values.SelectMany(t => t.Pieces.Acts.Keys).Distinct().ToList();
                    var unanswered = acts.Where(a => !source.Contains($"\"{a}\"")).ToList();
                    check(unanswered.Count == 0, $"{component} answers every act {embedded.Groups[1].Value}.json asks for",
                        unanswered.Count == 0 ? "the builder would refuse at construction otherwise" : $"nothing names: {string.Join(", ", unanswered)}");

                    Wiring(check, component, source, part);
                }
                check(owners == 2, "two components own a part: the status card and the community list", $"found {owners}");
            }

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

                    // 🔴 Every act the document asks for has a case in the panel's ActOf, and no
                    // case answers for an act the document does not ask — read off the source,
                    // because the builder's own refusal fires at construction, inside a game.
                    var actOf = System.Text.RegularExpressions.Regex.Match(source, @"private Action ActOf\(string act\)\s*\{(.*?)\n        \}", System.Text.RegularExpressions.RegexOptions.Singleline);
                    check(actOf.Success, $"{panel} answers the document's acts in ActOf", "a screen built from a document routes its verbs through one table");
                    if (!actOf.Success) continue;
                    var cases = System.Text.RegularExpressions.Regex.Matches(actOf.Groups[1].Value, @"case ""([A-Za-z0-9]+)"":")
                        .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
                    var missing = doc.Acts.Keys.Where(a => !cases.Contains(a)).ToList();
                    var dead = cases.Where(c => !doc.Acts.ContainsKey(c)).ToList();
                    check(missing.Count == 0, $"{panel} answers every act {Path.GetFileName(embedded.Groups[1].Value)}.json asks for",
                        missing.Count == 0 ? "the builder would refuse at construction otherwise" : $"no case for: {string.Join(", ", missing)}");
                    check(dead.Count == 0, $"{panel} answers no act its document does not ask for",
                        dead.Count == 0 ? "a case nothing asks for is dead code" : $"unasked: {string.Join(", ", dead)}");

                    Wiring(check, panel, source, doc);
                }
            }

            // ── The frontier: a panel builds nothing by hand any more ──────────────
            // 🔴 What this chantier was for: a second engine rewrites the interpreter and the
            // components, and touches no panel. So no panel may reach for a factory — every piece
            // comes from its document, every row from a template. What a panel may still do to a
            // built piece is repaint it (Retint, Highlight) or put a language mark in a host.
            // The base is the chrome (scroll layout, header host, help bar) and is the one
            // exception, named.
            if (panels != null)
            {
                var construction = new[] {
                    @"Stacks\.(Vertical|Horizontal|Row|Card|Section|ListItem|Spacer|FlexSpacer)\(", @"Labels\.\w+\(", @"Buttons\.\w+\(",
                    @"Fields\.\w+\(", @"CheckBoxes\.\w+\(", @"Sliders\.\w+\(", @"Choices\.\w+\(", @"TagChips\.\w+\(",
                    @"ScrollList\.Create\(", @"StatusLine\.Create\(", @"Toasts\.Create\(", @"Callout\.Create\(", @"Collapsible\.Create\(",
                    @"new SearchableDropdown\(", @"new TabBar\(", @"UIFactory\.", @"UIStyles\.Create" };
                int scanned = 0;
                foreach (var file in Directory.GetFiles(panels, "*.cs"))
                {
                    string panel = Path.GetFileName(file);
                    if (panel == "TranslatorPanelBase.cs") continue;
                    scanned++;
                    string source = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                    source = System.Text.RegularExpressions.Regex.Replace(source, @"//[^\r\n]*", "");
                    var found = construction.Select(c => System.Text.RegularExpressions.Regex.Match(source, c)).Where(m => m.Success).Select(m => m.Value).ToList();
                    check(found.Count == 0, $"{panel} builds nothing by hand",
                        found.Count == 0 ? "its pieces are its document's, its rows its templates'" : "found: " + string.Join(", ", found));
                }
                check(scanned >= 12, $"{scanned} panels scanned", "a rule with nothing to guard is decoration");
                check(System.Text.RegularExpressions.Regex.IsMatch("            var row = Stacks.Row(body, \"X\");", construction[0]),
                    "the frontier would catch a row built by hand", "a pattern that matches nothing guards nothing");
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
            var bare = ScreenDocument.Parse(JObject.Parse(@"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""checkbox"",""name"":""C""},{""kind"":""field"",""name"":""F"",""act"":""typed""}],""footer"":[{""kind"":""button"",""name"":""B"",""text"":""Go"",""act"":""go""}]}"));
            check(bare.Nodes["C"].Text == null && bare.Acts.ContainsKey("typed") && bare.Acts["typed"].Kind == "field",
                "a checkbox without words is bare, and a field may ask for an act as it is typed in", "the words of a bare box are elsewhere on its row; a field's act is how a wizard keeps its state as the person types");
            Refuses(check, "a slider without its range", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""slider"",""name"":""S"",""caption"":""Size:""}],""footer"":[]}", "caption and a range");
            Refuses(check, "two checkboxes asking for one act", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""checkbox"",""name"":""A"",""text"":""a"",""act"":""flip""},{""kind"":""checkbox"",""name"":""B"",""text"":""b"",""act"":""flip""}],""footer"":[]}", "two pieces");
            Refuses(check, "a template named like a piece of the screen", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""spacer"",""name"":""A"",""height"":1}],""footer"":[],""templates"":[{""kind"":""spacer"",""name"":""A"",""height"":1}]}", "shares its name");
            Refuses(check, "a template declared twice", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[],""footer"":[],""templates"":[{""kind"":""spacer"",""name"":""A"",""height"":1},{""kind"":""spacer"",""name"":""A"",""height"":1}]}", "declared twice");
            Refuses(check, "a template that is a title", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[],""footer"":[],""templates"":[{""kind"":""title"",""name"":""T"",""text"":""x"",""scope"":{""onThisMachine"":true,""yourPublishedCopy"":false}}]}", "only a screen carries");
            Refuses(check, "a choice with one word", @"{""name"":""X"",""size"":{""width"":400,""height"":200},""body"":[{""kind"":""choice"",""name"":""C"",""options"":[""Keep""]}],""footer"":[]}", "at least two words");
            Refuses(check, "a part with a body", @"{""name"":""X"",""part"":true,""body"":[],""templates"":[{""kind"":""spacer"",""name"":""A"",""height"":1}]}", "a part has no body");
            Refuses(check, "a part with a size", @"{""name"":""X"",""part"":true,""size"":{""width"":400,""height"":200},""templates"":[{""kind"":""spacer"",""name"":""A"",""height"":1}]}", "a part has no size");
            Refuses(check, "a part without a template", @"{""name"":""X"",""part"":true,""templates"":[]}", "at least one template");
            Refuses(check, "a part with a help sentence", @"{""name"":""X"",""part"":true,""templates"":[{""kind"":""spacer"",""name"":""A"",""height"":1,""help"":""x""}]}", "no help bar");
            var aPart = ScreenDocument.Parse(JObject.Parse(@"{""name"":""P"",""part"":true,""templates"":[{""kind"":""row"",""name"":""R"",""children"":[{""kind"":""button"",""name"":""B"",""text"":""Go"",""act"":""go""}]}]}"));
            check(aPart.IsPart && aPart.Width == 0 && aPart.Templates["R"].Pieces.Acts.ContainsKey("go") && aPart.Acts.Count == 0,
                "a part is templates alone: no size, no pieces of its own, its acts per template", "what a component builds into a host, never a window");

            var defaults = ScreenDocument.Parse(JObject.Parse(@"{""name"":""X"",""size"":{""width"":500,""height"":200},""body"":[],""footer"":[]}"));
            check(defaults.MinWidth == 500 && defaults.MinHeight == 200 && defaults.Backdrop && defaults.Persist && defaults.CardWidth == 460,
                "what a document leaves unsaid takes the base's defaults", "minimums equal the size, backdrop and persistence on, cards the width minus the margins");
        }

        /// <summary>Which kinds each accessor of a built screen hands back.</summary>
        private static readonly Dictionary<string, string[]> Accessors = new Dictionary<string, string[]>
        {
            { "Label", new[] { "label", "title" } }, { "Button", new[] { "button" } },
            { "Host", new[] { "card", "callout", "collapsible", "section", "stack", "row", "spacer", "tab" } },
            { "Status", new[] { "status" } }, { "Field", new[] { "field" } }, { "Dropdown", new[] { "dropdown" } },
            { "List", new[] { "list" } }, { "Toggle", new[] { "checkbox" } }, { "Slider", new[] { "slider" } },
            { "Choice", new[] { "choice" } }, { "Chip", new[] { "chip" } }, { "Toast", new[] { "toast" } },
            { "Tabs", new[] { "tabs" } }, { "Collapsible", new[] { "collapsible" } },
        };

        /// <summary>
        /// 🔴 Everything the code asks a built screen for exists in its document, of the kind the
        /// accessor hands back — and everything the document leaves to the code is reached from it.
        ///
        /// The builder refuses an unknown name at construction, inside a game, where one bad name
        /// takes every panel after it down with it. The other direction fails in silence: a slot
        /// nothing writes stays blank, a hidden row nothing shows stays hidden, a host nothing
        /// fills stays empty — and each reads as a screen that simply lacks the thing. Read off the
        /// source: the names are string literals, so a rename on either side goes red here.
        /// </summary>
        private static void Wiring(Action<bool, string, string> check, string who, string source, ScreenDocument doc)
        {
            var nodes = new Dictionary<string, ScreenNode>(doc.Nodes);
            var binds = new Dictionary<string, ScreenNode>(doc.Binds);
            foreach (var t in doc.Templates.Values)
            {
                foreach (var n in t.Pieces.Nodes) nodes[n.Key] = n.Value;
                foreach (var b in t.Pieces.Binds) binds[b.Key] = b.Value;
            }

            var wrong = new List<string>();
            var reached = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                         @"\.(Label|Button|Host|Status|Field|Dropdown|List|Toggle|Slider|Choice|Chip|Toast|Tabs|Collapsible)\(""([A-Za-z0-9]+)""\)"))
            {
                string accessor = m.Groups[1].Value, name = m.Groups[2].Value;
                reached.Add(name);
                if (!nodes.TryGetValue(name, out var node)) wrong.Add($"{accessor}(\"{name}\") names nothing");
                else if (Array.IndexOf(Accessors[accessor], node.Kind) < 0) wrong.Add($"{accessor}(\"{name}\") is a {node.Kind}");
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"\.Say\(""([a-zA-Z0-9]+)"""))
            {
                reached.Add(m.Groups[1].Value);
                if (!binds.ContainsKey(m.Groups[1].Value)) wrong.Add($"Say(\"{m.Groups[1].Value}\") writes no slot");
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"\.Instantiate\(""([A-Za-z0-9]+)"""))
            {
                reached.Add(m.Groups[1].Value);
                if (!doc.Templates.ContainsKey(m.Groups[1].Value)) wrong.Add($"Instantiate(\"{m.Groups[1].Value}\") names no template");
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"ScreenBuilder\.Part\(Part, ""([A-Za-z0-9]+)"""))
            {
                reached.Add(m.Groups[1].Value);
                if (!doc.Templates.ContainsKey(m.Groups[1].Value)) wrong.Add($"Part(\"{m.Groups[1].Value}\") names no template");
            }
            check(wrong.Count == 0, $"{who} asks its document only for what it describes",
                wrong.Count == 0 ? "an unknown name is refused at construction, inside a game" : string.Join("; ", wrong));

            // The other way round: what the document leaves to the code, the code reaches. A name
            // handed to a helper (`PlaceHotkey(capture, "HkForceScanHost", …)`) counts as reached:
            // any literal that is exactly a piece's name is a reach, whichever door it goes through.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"""([A-Za-z][A-Za-z0-9]*)"""))
                if (nodes.ContainsKey(m.Groups[1].Value)) reached.Add(m.Groups[1].Value);
            var unwritten = binds.Where(b => !reached.Contains(b.Key) && !reached.Contains(b.Value.Name)).Select(b => b.Key).ToList();
            check(unwritten.Count == 0, $"{who} writes every slot its document declares",
                unwritten.Count == 0 ? "a slot nothing writes is a blank on the screen" : $"never written: {string.Join(", ", unwritten)}");

            var neverShown = nodes.Values.Where(n => !n.StartsVisible && !reached.Contains(n.Name)).Select(n => n.Name).ToList();
            check(neverShown.Count == 0, $"{who} reaches every piece that starts hidden",
                neverShown.Count == 0 ? "a hidden piece nothing shows is a piece that does not exist" : $"never reached: {string.Join(", ", neverShown)}");

            var unfilled = nodes.Values.Where(n => (n.Kind == "stack" || n.Kind == "row") && n.Children.Count == 0 && !reached.Contains(n.Name)).Select(n => n.Name).ToList();
            check(unfilled.Count == 0, $"{who} fills every host its document leaves empty",
                unfilled.Count == 0 ? "an empty host nothing fills is a gap on the screen" : $"never reached: {string.Join(", ", unfilled)}");

            var uninstantiated = doc.Templates.Keys.Where(t => !reached.Contains(t)).ToList();
            check(uninstantiated.Count == 0, $"{who} instantiates every template its document describes",
                uninstantiated.Count == 0 ? "a template nobody instantiates is dead" : $"never instantiated: {string.Join(", ", uninstantiated)}");
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
