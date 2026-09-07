using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The frontier between the panels and what draws them, read off the source files as text.
    ///
    /// 🔴 Why text and not types. The panels reference Unity, so this project cannot compile
    /// them — and that is the point: the rule is that a panel names NOTHING of Unity or
    /// UniverseLib, so the only honest check is to look for those names in the file. Same
    /// protocol as the AddListener rule in CLAUDE.md: a grep that finds something is a bug.
    ///
    /// Two rules, and the second is what makes the first hold:
    ///   1. a PANEL holds handles only — no UIFactory, no GameObject, no Text, no Color, no
    ///      `.gameObject`/`.transform`/`.GetComponent` on anything;
    ///   2. a COMPONENT's public surface takes and returns handles only — otherwise a panel gets
    ///      a GameObject back from a component and writes on it without ever naming UniverseLib,
    ///      which is exactly how the frontier stayed open for a year.
    ///
    /// ⚠ The lists below name the files NOT YET MOVED. They shrink as each panel and component
    /// migrates, and a file removed from them can never go back: the day the lists are empty,
    /// the frontier is closed. Adding a file to a list is how a regression would be smuggled in,
    /// so that needs a commit message explaining why.
    /// </summary>
    internal static class UiBoundaryChecks
    {
        /// <summary>Panels still written against UniverseLib. Remove one when it moves.</summary>
        private static readonly HashSet<string> PanelsStillOpen = new HashSet<string>(StringComparer.Ordinal)
        {
            // The base carries the rendering half of every panel — sizing, persistence, the
            // scroll skeleton — and will keep Unity on purpose. It moves to a whitelist of its
            // own the day its declarative half is split from it.
            "TranslatorPanelBase.cs",
        };

        /// <summary>Components whose public surface still speaks Unity. Remove one when it moves.</summary>
        private static readonly HashSet<string> ComponentsStillOpen = new HashSet<string>(StringComparer.Ordinal)
        {
            "BadgeStrip.cs", "HelpZone.cs", "HotkeyCapture.cs", "LanguageMark.cs", "LanguageSelector.cs",
            "PendingMarks.cs", "QualityBar.cs", "ScopeMarks.cs", "SearchableDropdown.cs", "StatusCard.cs",
            "TabBar.cs", "TranslationList.cs", "VoteButtons.cs",
        };

        /// <summary>What a panel may not name. Each entry: the pattern, and what it catches.</summary>
        private static readonly (Regex pattern, string what)[] PanelForbidden =
        {
            (new Regex(@"^\s*using\s+UnityEngine", RegexOptions.Multiline), "a using of UnityEngine"),
            (new Regex(@"^\s*using\s+UniverseLib\.UI\.Models", RegexOptions.Multiline), "a using of UniverseLib.UI.Models"),
            (new Regex(@"\bUIFactory\b"), "UIFactory"),
            (new Regex(@"\bUniverseLib\.(?!UI;)"), "a qualified UniverseLib name"),
            (new Regex(@"\bUnityEngine\."), "a qualified UnityEngine name"),
            (new Regex(@"\bGameObject\b"), "GameObject"),
            (new Regex(@"\bRectTransform\b"), "RectTransform"),
            (new Regex(@"\bButtonRef\b"), "ButtonRef"),
            (new Regex(@"\bInputFieldRef\b"), "InputFieldRef"),
            (new Regex(@"\bTextAnchor\b"), "TextAnchor"),
            (new Regex(@"\bFontStyle\b"), "FontStyle"),
            (new Regex(@"\bVector[234]\b"), "a Vector"),
            (new Regex(@"\bColor\b"), "Color"),
            (new Regex(@"\bText\s+_\w+|List<Text>|\(Text\s|<Text>"), "a Text held as a field or parameter"),
            (new Regex(@"\bToggle\s+_\w+|\bSlider\s+_\w+|\bImage\s+_\w+"), "a Toggle, Slider or Image held as a field"),
            (new Regex(@"\.gameObject\b"), ".gameObject"),
            (new Regex(@"\.transform\b"), ".transform"),
            (new Regex(@"\.GetComponent\b"), ".GetComponent"),
            (new Regex(@"\bUIStyles\.(Create|SetBackground|ClearRowBackground|Configure|Apply)\w*"), "a UIStyles factory (the vocabulary has one)"),
            (new Regex(@"\bSetDynamicText\b|\bRegisterUIText\b|\bRegisterExcluded\b"), "a raw Text write or registration (a label's policy does that)"),
        };

        private const string EngineTypes =
            @"(GameObject|Text|ButtonRef|InputFieldRef|Toggle|Slider|Image|RectTransform|Color\??|TextAnchor|FontStyle|Vector[234])";

        /// <summary>
        /// A public member RETURNING an engine type: the type sits right after the modifiers,
        /// bare, in a list, or first in a tuple. The member's NAME is not judged — a property
        /// called Text of type LabelHandle is the vocabulary, not a leak.
        /// </summary>
        private static readonly Regex ReturnLeak = new Regex(
            @"^\s*public\s+(?:(?:static|override|virtual|readonly|abstract|event)\s+)*(?:List<|IEnumerable<|\(\s*)?" + EngineTypes + @"\b",
            RegexOptions.Multiline);

        /// <summary>A public method TAKING an engine type: `Type name` inside its parameter list.</summary>
        private static readonly Regex ParameterLeak = new Regex(
            @"^\s*public\s+[^;{=\n]*?\w\s*\((?:[^)]*?)\b" + EngineTypes + @"\s+\w+",
            RegexOptions.Multiline);

        private static Match ComponentLeak(string source)
        {
            var m = ReturnLeak.Match(source);
            return m.Success ? m : ParameterLeak.Match(source);
        }

        public static void Run(Action<bool, string, string> check)
        {
            string ui = FindUiFolder();
            check(ui != null, "the UI sources are found", "the check reads files; without them it proves nothing");
            if (ui == null) return;

            // ── Rule 1: the panels ────────────────────────────────────────────
            string panels = Path.Combine(ui, "Panels");
            int moved = 0;
            foreach (var file in Directory.GetFiles(panels, "*.cs"))
            {
                string name = Path.GetFileName(file);
                if (PanelsStillOpen.Contains(name)) continue;
                moved++;

                string source = StripComments(File.ReadAllText(file));
                var found = new List<string>();
                foreach (var (pattern, what) in PanelForbidden)
                    if (pattern.IsMatch(source)) found.Add(what);

                check(found.Count == 0,
                    $"{name} names nothing of the engine",
                    found.Count == 0 ? "a panel holds handles, and only handles"
                                     : "found: " + string.Join(", ", found));
            }

            check(moved > 0, "at least one panel has crossed the frontier",
                "a rule with nothing to guard is decoration");

            foreach (var listed in PanelsStillOpen)
                check(File.Exists(Path.Combine(panels, listed)),
                    $"{listed} still exists",
                    "a file renamed away from the list would escape the rule");

            // ── Rule 2: the components ────────────────────────────────────────
            string components = Path.Combine(ui, "Components");
            foreach (var file in Directory.GetFiles(components, "*.cs"))
            {
                string name = Path.GetFileName(file);
                if (ComponentsStillOpen.Contains(name)) continue;

                string source = StripComments(File.ReadAllText(file));
                var leak = ComponentLeak(source);
                check(!leak.Success,
                    $"{name} lets no engine type through its public surface",
                    leak.Success ? "leaks: " + leak.Value.Trim() : "what a panel receives is a handle");
            }

            // ── The alarm test: the rule must be able to fire ─────────────────
            check(PanelForbidden[2].pattern.IsMatch("var x = UIFactory.CreateLabel(a, b, c);"),
                "the rule sees UIFactory", "a pattern that never matches guards nothing");
            check(PanelForbidden[13].pattern.IsMatch("private Text _title;"),
                "the rule sees a Text field", "the commonest leak: a label held raw");
            check(ComponentLeak("        public GameObject Root => _root;").Success,
                "the rule sees a GameObject property", "the commonest component leak");
            check(ComponentLeak("        public void Describe(GameObject control, string helpText)\n        {").Success,
                "the rule sees a GameObject parameter", "the other way a type gets through");
            check(!ComponentLeak("        public Host Handle => new Host(_root);").Success,
                "and lets a Host through", "the rule must accept the vocabulary it protects");
            check(!ComponentLeak("        public LabelHandle Text => Ref?.ButtonText != null ? new LabelHandle(Ref.ButtonText, TextPolicy.Dynamic) : null;").Success,
                "a member NAMED Text of a vocabulary type is not a leak",
                "the type is judged, never the name — this fired once as a false alarm");
        }

        /// <summary>Comments may name anything; only code is judged.</summary>
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        /// <summary>Up from the binary until the Core's UI folder is found.</summary>
        private static string FindUiFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core", "UI");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
