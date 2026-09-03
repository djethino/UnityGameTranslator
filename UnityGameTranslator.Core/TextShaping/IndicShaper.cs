using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// What a shaper needs from a font: its cmap, its advances and its layout tables.
    /// Implemented over <see cref="TtfParser"/> below; kept as an interface so checks can hand
    /// the shaper a font without the rasterizer.
    /// </summary>
    internal interface IShapingFont
    {
        int GlyphIndex(int codepoint);
        int AdvanceWidth(int glyph);
        OpenTypeLayout Layout { get; }
    }

    internal sealed class TtfShapingFont : IShapingFont
    {
        private readonly TtfParser _parser;
        public TtfShapingFont(TtfParser parser) { _parser = parser; }
        public int GlyphIndex(int codepoint) => _parser.GetGlyphIndex(codepoint);
        public int AdvanceWidth(int glyph) => _parser.GetAdvanceWidth(glyph);
        public OpenTypeLayout Layout => _parser.Layout;
    }

    /// <summary>
    /// OpenType shaping for the ten classic Indic scripts — Devanagari, Bengali, Gurmukhi,
    /// Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Sinhala (U+0900..U+0DFF): text in,
    /// positioned glyphs of a given font out. Codepoints outside those blocks pass through as
    /// single glyphs no feature touches.
    ///
    /// The model is the one every shaping engine implements for these scripts — Microsoft's
    /// "Developing OpenType Fonts for Devanagari Script" (the dev2 specification) as HarfBuzz
    /// realises it (hb-ot-shaper-indic.cc), which is what the fonts were tested against. A font
    /// that only carries the old script tag (deva without dev2) gets the old specification's
    /// three differences: the first post-base Halant moved after the last consonant, no
    /// below-base forms before the base, and Devanagari's eyelash Ra formed through 'blwf'.
    /// (HarfBuzz also merges the clusters of an old-spec syllable; clusters carry nothing
    /// here but the source index, so there is nothing to merge.) The stages:
    ///   1. cut the text into syllables (a grammar over Unicode's Indic_Syllabic_Category);
    ///   2. per syllable, find the base consonant and reorder around it — reph candidate first,
    ///      pre-base matras to the front, marks travelling with what they follow, a stable
    ///      sort by canonical position; hand each glyph the feature masks its position earns;
    ///   3. the font's basic substitutions in the spec's order (nukt akhn rphf rkrf pref blwf
    ///      abvf half pstf vatu cjct);
    ///   4. final reordering: the reph to its script's place, the pre-base matra next to the
    ///      base, a pre-base-reordering Ra before the base;
    ///   5. the presentation substitutions (init pres abvs blws psts haln calt clig);
    ///   6. positioning (kern dist abvm blwm mark mkmk) and the attachments resolved.
    /// The per-script constants (base position, reph placement, below-form policy, matra
    /// canonical positions) are HarfBuzz's tables, kept verbatim: they encode what the
    /// existing fonts expect, not a preference.
    ///
    /// Categories come from the Unicode tables generated into IndicTables (tools/
    /// generate-indic-tables.py) — no hand-written character class anywhere here.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks, where it is run against a real
    /// font and compared with HarfBuzz's output word by word.
    /// </summary>
    internal static class IndicShaper
    {
        // ───────────────────────────── categories (HarfBuzz's OT_ set) ─────────────────────────────

        private const int CatX = 0, CatC = 1, CatV = 2, CatN = 3, CatH = 4, CatZWNJ = 5, CatZWJ = 6, CatM = 7, CatSM = 8,
            CatA = 9, CatVD = 10, CatPlaceholder = 11, CatDottedCircle = 12, CatRS = 13, CatMPst = 14, CatRepha = 15,
            CatRa = 16, CatCM = 17, CatSymbol = 18, CatCS = 19;

        // One letter per category for the syllable grammar (a regex over this alphabet).
        private const string CategoryLetters = "XCVNHZJMSADPORYErmsc";

        // ───────────────────────────── positions (canonical order) ─────────────────────────────

        private const int PosStart = 0, PosRaToBecomeReph = 1, PosPreM = 2, PosPreC = 3, PosBaseC = 4, PosAfterMain = 5,
            PosAboveC = 6, PosBeforeSub = 7, PosBelowC = 8, PosAfterSub = 9, PosBeforePost = 10, PosPostC = 11,
            PosAfterPost = 12, PosFinalC = 13, PosSmvd = 14, PosEnd = 15;

        // ───────────────────────────── per-script configuration ─────────────────────────────

        private enum RephMode { Implicit, Explicit, LogRepha }
        private enum BlwfMode { PreAndPost, PostOnly }

        private sealed class ScriptConfig
        {
            public string Name;
            public int Block;             // first codepoint of the block
            public string NewTag, OldTag; // OpenType script tags
            public int Virama;
            public bool BasePosLastSinhala;
            public int RephPos;
            public RephMode RephMode;
            public BlwfMode BlwfMode;
            /// <summary>
            /// Marks (GDEF class) get a zero advance before positioning — what the universal
            /// shaping engine does, which is where HarfBuzz sends Sinhala; the classic Indic
            /// engine leaves advances to the font.
            /// </summary>
            public bool ZeroMarkWidths;
        }

        // HarfBuzz indic_configs, verbatim (has_old_spec, virama, base_pos, reph_pos, reph_mode, blwf_mode).
        private static readonly ScriptConfig[] Scripts =
        {
            new ScriptConfig { Name = "Devanagari", Block = 0x0900, NewTag = "dev2", OldTag = "deva", Virama = 0x094D, RephPos = PosBeforePost, RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Bengali",    Block = 0x0980, NewTag = "bng2", OldTag = "beng", Virama = 0x09CD, RephPos = PosAfterSub,   RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Gurmukhi",   Block = 0x0A00, NewTag = "gur2", OldTag = "guru", Virama = 0x0A4D, RephPos = PosBeforeSub,  RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Gujarati",   Block = 0x0A80, NewTag = "gjr2", OldTag = "gujr", Virama = 0x0ACD, RephPos = PosBeforePost, RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Oriya",      Block = 0x0B00, NewTag = "ory2", OldTag = "orya", Virama = 0x0B4D, RephPos = PosAfterMain,  RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Tamil",      Block = 0x0B80, NewTag = "tml2", OldTag = "taml", Virama = 0x0BCD, RephPos = PosAfterPost,  RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Telugu",     Block = 0x0C00, NewTag = "tel2", OldTag = "telu", Virama = 0x0C4D, RephPos = PosAfterPost,  RephMode = RephMode.Explicit, BlwfMode = BlwfMode.PostOnly },
            new ScriptConfig { Name = "Kannada",    Block = 0x0C80, NewTag = "knd2", OldTag = "knda", Virama = 0x0CCD, RephPos = PosAfterPost,  RephMode = RephMode.Implicit, BlwfMode = BlwfMode.PostOnly },
            new ScriptConfig { Name = "Malayalam",  Block = 0x0D00, NewTag = "mlm2", OldTag = "mlym", Virama = 0x0D4D, RephPos = PosAfterMain,  RephMode = RephMode.LogRepha, BlwfMode = BlwfMode.PreAndPost },
            new ScriptConfig { Name = "Sinhala",    Block = 0x0D80, NewTag = "sinh", OldTag = "sinh", Virama = 0x0DCA, BasePosLastSinhala = true, RephPos = PosAfterPost, RephMode = RephMode.Explicit, BlwfMode = BlwfMode.PreAndPost, ZeroMarkWidths = true },
        };

        // Ra of each script: the consonant that forms a reph (and a rakar / pre-base-reordering form).
        private static readonly int[] RaCodepoints = { 0x0930, 0x09B0, 0x0A30, 0x0AB0, 0x0B30, 0x0BB0, 0x0C30, 0x0CB0, 0x0D30, 0x0DBB };

        private static ScriptConfig ScriptOf(int cp)
        {
            if (cp < IndicTables.IndicFirst || cp > IndicTables.IndicLast) return null;
            return Scripts[(cp - 0x0900) >> 7];
        }

        /// <summary>Does this text hold anything the shaper would act on?</summary>
        internal static bool NeedsShaping(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (c >= IndicTables.IndicFirst && c <= IndicTables.IndicLast) return true;
            return false;
        }

        // ───────────────────────────── features ─────────────────────────────

        // Order is the specification's. Basic features are masked per glyph position; the
        // rest apply everywhere. 'ccmp' and 'locl' are the common preliminaries every shaper runs.
        private static readonly string[] PreFeatures = { "ccmp", "locl" };
        private static readonly string[] BasicFeatures = { "nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct" };
        private static readonly string[] PresentationFeatures = { "init", "pres", "abvs", "blws", "psts", "haln", "calt", "clig" };
        private static readonly string[] PositioningFeatures = { "kern", "dist", "abvm", "blwm", "mark", "mkmk" };

        private const uint MaskRphf = 1u << 0, MaskPref = 1u << 1, MaskBlwf = 1u << 2, MaskAbvf = 1u << 3, MaskHalf = 1u << 4,
            MaskPstf = 1u << 5, MaskInit = 1u << 6, MaskGlobal = 1u << 7;

        private static uint MaskOf(string feature)
        {
            switch (feature)
            {
                case "rphf": return MaskRphf;
                case "pref": return MaskPref;
                case "blwf": return MaskBlwf;
                case "abvf": return MaskAbvf;
                case "half": return MaskHalf;
                case "pstf": return MaskPstf;
                case "init": return MaskInit;
                default: return MaskGlobal;
            }
        }

        /// <summary>The font's plan for one script: which lookups each feature names.</summary>
        private sealed class Plan
        {
            public ScriptConfig Script;
            public bool IsOldSpec;
            public Dictionary<string, int[]> Gsub, Gpos;
            public int[] Rphf, Pref, Blwf, Pstf, Vatu;
            public int ViramaGlyph;
            /// <summary>
            /// Whether "would substitute" demands a rule with no context around the sequence.
            /// New-spec fonts: yes; old-spec fonts and Malayalam: no — HarfBuzz's setting, and
            /// Malayalam fonts do write their below forms as contextual rules.
            /// </summary>
            public bool ZeroContext;
        }

        private static readonly Dictionary<OpenTypeLayout, Dictionary<ScriptConfig, Plan>> _plans = new Dictionary<OpenTypeLayout, Dictionary<ScriptConfig, Plan>>();

        private static Plan PlanFor(IShapingFont font, ScriptConfig script)
        {
            var layout = font.Layout;
            if (!_plans.TryGetValue(layout, out var byScript)) _plans[layout] = byScript = new Dictionary<ScriptConfig, Plan>();
            if (byScript.TryGetValue(script, out var plan)) return plan;

            plan = new Plan { Script = script, ViramaGlyph = font.GlyphIndex(script.Virama) };
            string used = null;
            plan.Gsub = layout.Gsub?.CollectFeatures(new[] { script.NewTag, script.OldTag, "DFLT" }, null, out used) ?? new Dictionary<string, int[]>();
            plan.IsOldSpec = used == script.OldTag && script.OldTag != script.NewTag;
            plan.Gpos = layout.Gpos?.CollectFeatures(new[] { script.NewTag, script.OldTag, "DFLT" }, null, out _) ?? new Dictionary<string, int[]>();
            plan.Gsub.TryGetValue("rphf", out plan.Rphf);
            plan.Gsub.TryGetValue("pref", out plan.Pref);
            plan.Gsub.TryGetValue("blwf", out plan.Blwf);
            plan.Gsub.TryGetValue("pstf", out plan.Pstf);
            plan.Gsub.TryGetValue("vatu", out plan.Vatu);
            plan.ZeroContext = !plan.IsOldSpec && script.Block != 0x0D00;
            byScript[script] = plan;
            return plan;
        }

        // ───────────────────────────── the syllable grammar ─────────────────────────────

        // HarfBuzz's indic machine (hb-ot-shaper-indic-machine.rl) over the category letters.
        private const string RxC = "[Cr]";
        private const string RxN = "(?:Z?R)?(?:NN?)?";
        private const string RxZ = "[JZ]";
        private const string RxReph = "(?:rH|E)";
        private const string RxCn = RxC + "J?" + RxN;
        private const string RxForcedRakar = "JHJr";
        private const string RxMatraGroup = RxZ + "*(?:M|S?Y)N?(?:H|" + RxForcedRakar + ")?";
        private const string RxSyllableTail = "(?:" + RxZ + "?SS?Z?)?[AD]*";
        private const string RxHalantGroup = RxZ + "?H(?:JN?)?";
        private const string RxFinalHalantGroup = "(?:" + RxHalantGroup + "|HZ)";
        private const string RxMedialGroup = "m?";
        private const string RxHalantOrMatraGroup = "(?:" + RxFinalHalantGroup + "|(?:" + RxMatraGroup + "){0,4})";
        private const string RxBody = "(?:" + RxHalantGroup + RxCn + "){0,4}" + RxMedialGroup + RxHalantOrMatraGroup + RxSyllableTail;

        private enum SyllableType { ConsonantSyllable, VowelSyllable, StandaloneCluster, SymbolCluster, BrokenCluster, NonIndic }

        private sealed class SyllableRule
        {
            public SyllableType Type; public Regex Regex;
            public SyllableRule(SyllableType type, string pattern) { Type = type; Regex = new Regex("\\G" + pattern, RegexOptions.Compiled); }
        }

        private static readonly SyllableRule[] SyllableRules =
        {
            new SyllableRule(SyllableType.ConsonantSyllable, "[Ec]?" + RxCn + RxBody),
            new SyllableRule(SyllableType.VowelSyllable,     RxReph + "?V" + RxN + "(?:J|" + RxBody + ")"),
            new SyllableRule(SyllableType.StandaloneCluster, "(?:[Ec]?P|" + RxReph + "?O)" + RxN + RxBody),
            new SyllableRule(SyllableType.SymbolCluster,     "sN?" + RxSyllableTail),
            new SyllableRule(SyllableType.BrokenCluster,     RxReph + "?" + RxN + RxBody),
        };

        private sealed class Syllable
        {
            public int Start, End; public SyllableType Type; public ScriptConfig Script;
        }

        // ───────────────────────────── classification ─────────────────────────────

        private static void Classify(int cp, out int category, out int position)
        {
            category = CatX; position = PosEnd;
            if (cp == 0x200C) { category = CatZWNJ; return; }
            if (cp == 0x200D) { category = CatZWJ; return; }
            if (cp == 0x25CC) { category = CatDottedCircle; position = PosBaseC; return; }
            if (cp == 0x00A0 || (cp >= 0x2010 && cp <= 0x2014)) { category = CatPlaceholder; position = PosBaseC; return; }
            var script = ScriptOf(cp);
            if (script == null) return;

            byte syl = IndicTables.SyllabicOf[cp - IndicTables.IndicFirst];
            byte pos = IndicTables.PositionalOf[cp - IndicTables.IndicFirst];
            if (Array.IndexOf(RaCodepoints, cp) >= 0) { category = CatRa; position = PosBaseC; return; }

            if (syl == IndicTables.Syllabic.Consonant || syl == IndicTables.Syllabic.Consonant_Dead
                || syl == IndicTables.Syllabic.Consonant_With_Stacker || syl == IndicTables.Syllabic.Consonant_Subjoined)
            { category = CatC; position = PosBaseC; }
            else if (syl == IndicTables.Syllabic.Consonant_Placeholder) { category = CatPlaceholder; position = PosBaseC; }
            else if (syl == IndicTables.Syllabic.Consonant_Medial) { category = CatCM; position = PosBelowC; }
            else if (syl == IndicTables.Syllabic.Consonant_Preceding_Repha || syl == IndicTables.Syllabic.Consonant_Prefixed) { category = CatRepha; position = PosRaToBecomeReph; }
            else if (syl == IndicTables.Syllabic.Vowel_Independent || syl == IndicTables.Syllabic.Vowel) { category = CatV; position = PosBaseC; }
            else if (syl == IndicTables.Syllabic.Vowel_Dependent) { category = CatM; position = MatraPosition(cp, pos, script); }
            else if (syl == IndicTables.Syllabic.Nukta) { category = CatN; }
            else if (syl == IndicTables.Syllabic.Virama || syl == IndicTables.Syllabic.Pure_Killer) { category = CatH; }
            else if (syl == IndicTables.Syllabic.Bindu || syl == IndicTables.Syllabic.Visarga || syl == IndicTables.Syllabic.Syllable_Modifier
                     || syl == IndicTables.Syllabic.Gemination_Mark || syl == IndicTables.Syllabic.Modifying_Letter)
            { category = CatSM; position = PosSmvd; }
            else if (syl == IndicTables.Syllabic.Avagraha) { category = CatA; position = PosSmvd; }
            else if (syl == IndicTables.Syllabic.Cantillation_Mark) { category = CatVD; position = PosSmvd; }
            else if (syl == IndicTables.Syllabic.Register_Shifter) { category = CatRS; }
            else if (syl == IndicTables.Syllabic.Joiner) { category = CatZWJ; }
            else if (syl == IndicTables.Syllabic.Non_Joiner) { category = CatZWNJ; }
            // Numbers, tone letters and the rest stay X: never part of a syllable.
        }

        /// <summary>
        /// Canonical position of a dependent vowel sign: which side of the base it sits on, then
        /// where that side lands in the script's order relative to below- and post-base forms.
        /// HarfBuzz's matra_position_indic, verbatim — these are the choices the fonts were
        /// built against, deviations from the specification included.
        /// </summary>
        private static int MatraPosition(int cp, byte positional, ScriptConfig script)
        {
            // Two-part signs are decomposed before this is asked; the composed sign's own side,
            // for the ones that are not, is where its right part goes (HarfBuzz's table).
            int side;
            if (positional == IndicTables.Positional.Left) side = PosPreC;
            else if (positional == IndicTables.Positional.Top || positional == IndicTables.Positional.Top_And_Left) side = PosAboveC;
            else if (positional == IndicTables.Positional.Bottom || positional == IndicTables.Positional.Top_And_Bottom) side = PosBelowC;
            else if (positional == IndicTables.Positional.Overstruck) return PosAfterMain;
            else side = PosPostC; // Right, Top_And_Right, Bottom_And_Right, Top_And_Bottom_And_Right

            int block = script.Block;
            switch (side)
            {
                case PosPreC: return PosPreM;
                case PosPostC:
                    switch (block)
                    {
                        case 0x0900: return PosAfterSub;
                        case 0x0980: case 0x0A00: case 0x0A80: case 0x0B00: case 0x0B80: case 0x0D00: return PosAfterPost;
                        case 0x0C00: return cp <= 0x0C42 ? PosBeforeSub : PosAfterSub;
                        case 0x0C80: return cp < 0x0CC3 || cp > 0x0CD6 ? PosBeforeSub : PosAfterSub;
                        default: return PosAfterSub;
                    }
                case PosAboveC:
                    switch (block)
                    {
                        case 0x0A00: return PosAfterPost;
                        case 0x0B00: return PosAfterMain;
                        case 0x0C00: case 0x0C80: return PosBeforeSub;
                        default: return PosAfterSub;
                    }
                default: // below
                    switch (block)
                    {
                        case 0x0A00: case 0x0A80: case 0x0B80: case 0x0D00: return PosAfterPost;
                        case 0x0C00: case 0x0C80: return PosBeforeSub;
                        default: return PosAfterSub;
                    }
            }
        }

        // ───────────────────────────── shaping ─────────────────────────────

        /// <summary>
        /// Shape a whole string for one font. Canonically decomposable characters are taken
        /// apart first (two-part vowel signs, nukta consonants — the font's rules are written
        /// for the parts), every codepoint becomes a glyph through the cmap, and only the Indic
        /// syllables get the treatment above.
        /// </summary>
        internal static List<ShapedGlyph> Shape(string text, IShapingFont font)
        {
            var cps = new List<int>(text.Length);
            var clusters = new List<int>(text.Length);
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                cps.Add(cp); clusters.Add(i);
                i += cp > 0xFFFF ? 2 : 1;
            }
            var result = new List<ShapedGlyph>(cps.Count);
            ShapeRun(cps, clusters, font, result);
            return result;
        }

        /// <summary>
        /// One run of code points (with their source indices), the result appended. The lists
        /// are the run's own and are edited in place (the forbidden vowel sequences get their
        /// dotted circle before anything else looks at the text).
        /// </summary>
        internal static void ShapeRun(List<int> input, List<int> inputClusters, IShapingFont font, List<ShapedGlyph> result)
        {
            var layout = font.Layout;
            ShapingCommon.ApplyVowelConstraints(input, inputClusters);
            var cps = Decompose(input, inputClusters, font, out var clusters);
            var buf = new L.GlyphBuffer();
            var categories = new StringBuilder(cps.Count);
            for (int i = 0; i < cps.Count; i++) AddGlyph(buf, categories, cps[i], clusters[i], font);

            // 1. Syllables — over the categories, the longest matching rule; anything else is one glyph.
            //    A broken cluster (marks with nothing to carry them) gets a dotted circle to sit on,
            //    as every shaper draws it — after a logical repha when there is one.
            var syllables = new List<Syllable>();
            int dottedCircle = font.GlyphIndex(0x25CC);
            int p = 0, syllableNumber = 0;
            while (p < categories.Length)
            {
                string cats = categories.ToString();
                int bestLen = 0; SyllableType bestType = SyllableType.NonIndic;
                foreach (var rule in SyllableRules)
                {
                    var m = rule.Regex.Match(cats, p);
                    if (m.Success && m.Length > bestLen) { bestLen = m.Length; bestType = rule.Type; }
                }
                if (bestLen == 0) { bestLen = 1; bestType = SyllableType.NonIndic; }
                if (bestType == SyllableType.BrokenCluster && dottedCircle > 0)
                {
                    int at = p;
                    while (at < p + bestLen && buf[at].Category == CatRepha) at++;
                    cps.Insert(at, 0x25CC);
                    clusters.Insert(at, clusters[p]);
                    var inserted = new L.GlyphBuffer();
                    var insertedCats = new StringBuilder();
                    AddGlyph(inserted, insertedCats, 0x25CC, clusters[p], font);
                    buf.Glyphs.Insert(at, inserted[0]);
                    categories.Insert(at, insertedCats[0]);
                    bestLen++;
                }
                syllableNumber++;
                ScriptConfig script = null;
                for (int k = p; k < p + bestLen && script == null; k++) script = ScriptOf(cps[k]);
                for (int k = p; k < p + bestLen; k++) buf[k].Syllable = syllableNumber;
                syllables.Add(new Syllable { Start = p, End = p + bestLen, Type = bestType, Script = script });
                p += bestLen;
            }

            // 2. Initial reordering and masks, per syllable.
            var plans = new Plan[syllables.Count];
            for (int s = 0; s < syllables.Count; s++)
            {
                var syl = syllables[s];
                for (int k = syl.Start; k < syl.End; k++) buf[k].Mask = MaskGlobal;
                if (syl.Script == null || syl.Type == SyllableType.NonIndic || syl.Type == SyllableType.SymbolCluster) continue;
                var plan = plans[s] = PlanFor(font, syl.Script);
                InitialReordering(buf, syl.Start, syl.End, plan, font);
            }

            // 3. Substitutions: preliminaries, then the basic features, then final reordering, then
            //    presentation. After the final reordering the syllable boundaries are released —
            //    presentation and positioning see the whole run, as in HarfBuzz.
            ApplyStage(layout, layout.Gsub, buf, plans, PreFeatures, gsub: true);
            ApplyStage(layout, layout.Gsub, buf, plans, BasicFeatures, gsub: true);
            FinalReorderingAll(buf, plans);
            for (int i = 0; i < buf.Count; i++) buf[i].Syllable = 0;
            ApplyStage(layout, layout.Gsub, buf, plans, PresentationFeatures, gsub: true);
            // The common features last, on the whole run (HarfBuzz collects them after the shaper's).
            ApplyStage(layout, layout.Gsub, buf, plans, ShapingCommon.CommonGsubFeatures, gsub: true);

            // 4. Positioning.
            bool zeroMarks = false;
            foreach (var plan in DistinctPlans(plans)) zeroMarks |= plan.Script.ZeroMarkWidths;
            for (int i = 0; i < buf.Count; i++)
            {
                buf[i].XAdvance = font.AdvanceWidth(buf[i].Glyph);
                if (zeroMarks && layout.GlyphClass(buf[i].Glyph) == L.ClassMark) buf[i].XAdvance = 0;
            }
            if (layout.Gpos != null)
            {
                ApplyStage(layout, layout.Gpos, buf, plans, PositioningFeatures, gsub: false);
                buf.ResolveAttachments();
            }

            // Joiners have done their work: they leave the output, as every shaper hides them.
            for (int i = 0; i < buf.Count; i++)
            {
                var g = buf[i];
                if (g.Category == CatZWJ || g.Category == CatZWNJ) continue;
                result.Add(new ShapedGlyph { Glyph = g.Glyph, Cluster = g.Cluster, XAdvance = g.XAdvance, XOffset = g.XOffset, YOffset = g.YOffset });
            }
        }

        private static void AddGlyph(L.GlyphBuffer buf, StringBuilder categories, int cp, int cluster, IShapingFont font)
        {
            Classify(cp, out int category, out int position);
            int glyph = font.GlyphIndex(cp);
            buf.Glyphs.Add(new L.ShapedGlyph
            {
                Glyph = glyph,
                Cluster = cluster,
                Category = category,
                Position = position,
                XAdvance = font.AdvanceWidth(glyph),
                UnicodeMark = category == CatM || category == CatN || category == CatH || category == CatSM || category == CatVD,
                Mask = 0,
            });
            categories.Append(CategoryLetters[category]);
        }

        private static IEnumerable<Plan> DistinctPlans(Plan[] plans)
        {
            var seen = new HashSet<Plan>();
            foreach (var plan in plans) if (plan != null && seen.Add(plan)) yield return plan;
        }

        /// <summary>
        /// One stage over the whole buffer: the lookups of every listed feature of every script
        /// present, each once, in lookup-index order, with the masks of the features naming it
        /// merged — the way OpenType engines run a stage. Masks decide where a lookup applies.
        /// </summary>
        private static void ApplyStage(OpenTypeLayout layout, L.LayoutTable table, L.GlyphBuffer buf, Plan[] plans, string[] features, bool gsub)
        {
            if (table == null) return;
            var masks = new SortedDictionary<int, uint>();
            foreach (string feature in features)
            {
                uint mask = gsub ? MaskOf(feature) : uint.MaxValue;
                foreach (var plan in DistinctPlans(plans))
                {
                    var byFeature = gsub ? plan.Gsub : plan.Gpos;
                    if (!byFeature.TryGetValue(feature, out var lookups)) continue;
                    foreach (int li in lookups)
                        masks[li] = masks.TryGetValue(li, out uint m) ? (m | mask) : mask;
                }
            }
            foreach (var kv in masks) layout.ApplyLookup(table, kv.Key, buf, kv.Value);
        }

        /// <summary>
        /// Canonical decomposition of the text, with each output's source index. Everything
        /// Unicode takes apart is taken apart (the generated Decompositions table), with the
        /// exceptions every shaper makes for compatibility with the fonts: four letters left
        /// whole, and Bengali ya + nukta put back together as yya.
        /// </summary>
        private static List<int> Decompose(List<int> input, List<int> inputClusters, IShapingFont font, out List<int> clusters)
        {
            var cps = new List<int>(input.Count + 4);
            clusters = new List<int>(input.Count + 4);
            for (int k = 0; k < input.Count; k++)
            {
                int cp = input[k], i = inputClusters[k];
                int[] parts = DecompositionOf(cp, font);
                if (parts != null) foreach (int part in parts) { cps.Add(part); clusters.Add(i); }
                else { cps.Add(cp); clusters.Add(i); }
            }
            // Recompose the one pair the fonts want composed (a composition exclusion in Unicode).
            for (int i = 0; i + 1 < cps.Count; i++)
                if (cps[i] == 0x09AF && cps[i + 1] == 0x09BC && font.GlyphIndex(0x09DF) > 0)
                {
                    cps[i] = 0x09DF;
                    cps.RemoveAt(i + 1);
                    clusters.RemoveAt(i + 1);
                }
            return cps;
        }

        private static int[] DecompositionOf(int cp, IShapingFont font)
        {
            switch (cp)
            {
                case 0x0931: // DEVANAGARI LETTER RRA
                case 0x09DC: // BENGALI LETTER RRA
                case 0x09DD: // BENGALI LETTER RHA
                case 0x0B94: // TAMIL LETTER AU
                    return null;
            }
            var table = IndicTables.Decompositions;
            for (int i = 0; i + 1 < table.Length;)
            {
                int sign = table[i], count = table[i + 1];
                if (sign == cp)
                {
                    var parts = new int[count];
                    Array.Copy(table, i + 2, parts, 0, count);
                    return parts;
                }
                if (sign > cp) return null;
                i += 2 + count;
            }
            return null;
        }

        // ───────────────────────────── helpers over the buffer ─────────────────────────────

        private static bool IsConsonant(L.ShapedGlyph g)
        {
            int c = g.Category;
            return c == CatC || c == CatCS || c == CatRa || c == CatCM || c == CatV || c == CatPlaceholder || c == CatDottedCircle;
        }
        private static bool IsJoiner(L.ShapedGlyph g) => g.Category == CatZWJ || g.Category == CatZWNJ;
        private static bool IsHalant(L.ShapedGlyph g) => g.Category == CatH;
        private static bool LigatedAndDidntMultiply(L.ShapedGlyph g) => g.Ligated && !g.Multiplied;

        /// <summary>Where a consonant's form goes: below, post, or base — asked of the font (HarfBuzz consonant_position_from_face).</summary>
        private static int ConsonantPositionFromFace(Plan plan, int consonant, IShapingFont font)
        {
            var layout = font.Layout;
            int virama = plan.ViramaGlyph;
            var cv = new[] { consonant, virama };
            var vc = new[] { virama, consonant };
            bool zc = plan.ZeroContext;
            if (layout.WouldSubstitute(layout.Gsub, plan.Blwf, vc, zc) || layout.WouldSubstitute(layout.Gsub, plan.Blwf, cv, zc)
                || layout.WouldSubstitute(layout.Gsub, plan.Vatu, vc, zc) || layout.WouldSubstitute(layout.Gsub, plan.Vatu, cv, zc)) return PosBelowC;
            if (layout.WouldSubstitute(layout.Gsub, plan.Pstf, vc, zc) || layout.WouldSubstitute(layout.Gsub, plan.Pstf, cv, zc)) return PosPostC;
            if (layout.WouldSubstitute(layout.Gsub, plan.Pref, vc, zc) || layout.WouldSubstitute(layout.Gsub, plan.Pref, cv, zc)) return PosPostC;
            return PosBaseC;
        }

        private static void Move(L.GlyphBuffer buf, int from, int to)
        {
            if (from == to) return;
            var g = buf.Glyphs[from];
            buf.Glyphs.RemoveAt(from);
            buf.Glyphs.Insert(to, g);
        }

        /// <summary>Stable sort of a slice by position (insertion sort, as HarfBuzz's bsort).</summary>
        private static void SortByPosition(L.GlyphBuffer buf, int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                var g = buf.Glyphs[i];
                int j = i - 1;
                while (j >= start && buf.Glyphs[j].Position > g.Position) { buf.Glyphs[j + 1] = buf.Glyphs[j]; j--; }
                buf.Glyphs[j + 1] = g;
            }
        }

        // ───────────────────────────── initial reordering ─────────────────────────────

        private static void InitialReordering(L.GlyphBuffer buf, int start, int end, Plan plan, IShapingFont font)
        {
            var script = plan.Script;
            var layout = font.Layout;

            // Consonant positions from the font: which ones have below/post forms.
            for (int i = start; i < end; i++)
            {
                var g = buf[i];
                if (g.Category == CatC || g.Category == CatRa || g.Category == CatCS)
                    g.Position = ConsonantPositionFromFace(plan, g.Glyph, font);
            }

            // 1. Find base consonant. None until one is found: a syllable that ends in Halant +
            //    ZWJ asks for explicit half forms of everything, so its consonants all sit
            //    before a base that is not there (HarfBuzz starts at `end` for that reason —
            //    starting at `start` made the first consonant the base and denied it 'half').
            int b = end;
            bool hasReph = false;
            int limit = start;

            // Reph candidate: Ra + Halant at the start (implicit), Ra + H + ZWJ (explicit), or a
            // logical repha character — if the font would form it.
            if (plan.Rphf != null && start + 3 <= end
                && ((script.RephMode == RephMode.Implicit && !IsJoiner(buf[start + 2]))
                    || (script.RephMode == RephMode.Explicit && buf[start + 2].Category == CatZWJ)))
            {
                var two = new[] { buf[start].Glyph, buf[start + 1].Glyph };
                bool forms = buf[start].Category == CatRa && buf[start + 1].Category == CatH
                    && (layout.WouldSubstitute(layout.Gsub, plan.Rphf, two, plan.ZeroContext)
                        || (script.RephMode == RephMode.Explicit && layout.WouldSubstitute(layout.Gsub, plan.Rphf, new[] { two[0], two[1], buf[start + 2].Glyph }, plan.ZeroContext)));
                if (forms)
                {
                    limit += 2;
                    while (limit < end && IsJoiner(buf[limit])) limit++;
                    b = start;
                    hasReph = true;
                }
            }
            else if (script.RephMode == RephMode.LogRepha && buf[start].Category == CatRepha)
            {
                limit += 1;
                while (limit < end && IsJoiner(buf[limit])) limit++;
                b = start;
                hasReph = true;
            }

            if (!script.BasePosLastSinhala)
            {
                // Starting from the end, move backwards until a consonant that has no below- or
                // post-base form (post-base forms have to follow below-base forms), or the first.
                int i = end;
                bool seenBelow = false;
                do
                {
                    i--;
                    if (IsConsonant(buf[i]))
                    {
                        if (buf[i].Position != PosBelowC && (buf[i].Position != PosPostC || seenBelow)) { b = i; break; }
                        if (buf[i].Position == PosBelowC) seenBelow = true;
                        b = i;
                    }
                    else
                    {
                        // A ZWJ after a Halant stops the base search and asks for an explicit half form.
                        if (start < i && buf[i].Category == CatZWJ && buf[i - 1].Category == CatH) break;
                    }
                } while (i > limit);
            }
            else
            {
                // Sinhala: the first consonant not followed by ZWJ is the base.
                if (!hasReph) b = limit;
                for (int i = limit; i < end; i++)
                    if (IsConsonant(buf[i]))
                    {
                        if (limit < i && buf[i - 1].Category == CatZWJ) break;
                        b = i;
                    }
            }

            // Ra + Halant with no other consonant: no reph, Ra is the base.
            if (hasReph && b == start && limit - b <= 2) hasReph = false;

            // 2/3. Positions before and at the base; final consonants; the reph candidate.
            for (int i = start; i < b; i++) buf[i].Position = Math.Min(PosPreC, buf[i].Position);
            if (b < end) buf[b].Position = PosBaseC;
            if (script.Block != 0x0D00 && script.Block != 0x0B80)
                for (int i = b + 1; i < end; i++)
                    if (buf[i].Category == CatM)
                    {
                        for (int j = i + 1; j < end; j++)
                            if (IsConsonant(buf[j])) { buf[j].Position = PosFinalC; break; }
                        break;
                    }
            if (hasReph) buf[start].Position = PosRaToBecomeReph;

            // Old-style script tags: move the first post-base Halant after the last consonant.
            if (plan.IsOldSpec)
            {
                bool disallowDoubleHalants = script.Block == 0x0C80;
                for (int i = b + 1; i < end; i++)
                    if (buf[i].Category == CatH)
                    {
                        int j;
                        for (j = end - 1; j > i; j--)
                            if (IsConsonant(buf[j]) || (disallowDoubleHalants && buf[j].Category == CatH)) break;
                        if (buf[j].Category != CatH && j > i) Move(buf, i, j);
                        break;
                    }
            }

            // Attach misc marks to the previous character so they move with it.
            {
                int lastPos = PosStart;
                for (int i = start; i < end; i++)
                {
                    var g = buf[i];
                    if (IsJoiner(g) || g.Category == CatN || g.Category == CatRS || g.Category == CatCM || g.Category == CatH)
                    {
                        g.Position = lastPos;
                        if (g.Category == CatH && g.Position == PosPreM)
                        {
                            // A Halant does not move with a left matra.
                            for (int j = i; j > start; j--)
                                if (buf[j - 1].Position != PosPreM) { g.Position = buf[j - 1].Position; break; }
                        }
                    }
                    else if (g.Position != PosSmvd) lastPos = g.Position;
                }
            }
            // Post-base consonants own anything before them since the last consonant or matra.
            {
                int last = b;
                for (int i = b + 1; i < end; i++)
                {
                    if (IsConsonant(buf[i]))
                    {
                        for (int j = last + 1; j < i; j++)
                            if (buf[j].Position < PosSmvd) buf[j].Position = buf[i].Position;
                        last = i;
                    }
                    else if (buf[i].Category == CatM) last = i;
                }
            }

            SortByPosition(buf, start, end);

            // Base may have moved: find it again.
            b = end;
            for (int i = start; i < end; i++) if (buf[i].Position == PosBaseC) { b = i; break; }

            // Masks.
            for (int i = start; i < end && buf[i].Position == PosRaToBecomeReph; i++) buf[i].Mask |= MaskRphf;
            {
                uint mask = MaskHalf;
                if (!plan.IsOldSpec && script.BlwfMode == BlwfMode.PreAndPost) mask |= MaskBlwf;
                for (int i = start; i < b; i++) buf[i].Mask |= mask;
                mask = MaskBlwf | MaskAbvf | MaskPstf;
                for (int i = b + 1; i < end; i++) buf[i].Mask |= mask;
            }

            // Old-spec Devanagari: an eyelash Ra — Ra + Halant before the base — takes the
            // below-base form, the old specification applying 'blwf' to every such Ra as well
            // as to below-base consonants. Not when a ZWJ follows: Ra, Halant, ZWJ is the
            // explicit request for the eyelash, and the font handles it (HarfBuzz).
            if (plan.IsOldSpec && script.Block == 0x0900)
                for (int i = start; i + 1 < b; i++)
                    if (buf[i].Category == CatRa && buf[i + 1].Category == CatH && (i + 2 == b || buf[i + 2].Category != CatZWJ))
                    {
                        buf[i].Mask |= MaskBlwf;
                        buf[i + 1].Mask |= MaskBlwf;
                    }

            // Pre-base-reordering Ra: a Halant + Ra pair after the base the font would ligate through 'pref'.
            if (plan.Pref != null && b + 2 <= end)
            {
                for (int i = b + 1; i + 1 < end; i++)
                {
                    var pair = new[] { buf[i].Glyph, buf[i + 1].Glyph };
                    if (layout.WouldSubstitute(layout.Gsub, plan.Pref, pair, plan.ZeroContext))
                    {
                        buf[i].Mask |= MaskPref;
                        buf[i + 1].Mask |= MaskPref;
                        break;
                    }
                }
            }

            // ZWJ / ZWNJ effects: a ZWNJ disables the half form of the consonant before it.
            for (int i = start + 1; i < end; i++)
                if (IsJoiner(buf[i]))
                {
                    bool nonJoiner = buf[i].Category == CatZWNJ;
                    int j = i;
                    do
                    {
                        j--;
                        if (nonJoiner) buf[j].Mask &= ~MaskHalf;
                    } while (j > start && !IsConsonant(buf[j]));
                }
        }

        // ───────────────────────────── final reordering ─────────────────────────────

        private static void FinalReorderingAll(L.GlyphBuffer buf, Plan[] plans)
        {
            // Syllables are re-read from the buffer: substitution may have merged glyphs.
            int i = 0;
            while (i < buf.Count)
            {
                int syllable = buf[i].Syllable;
                int end = i + 1;
                while (end < buf.Count && buf[end].Syllable == syllable) end++;
                var plan = syllable > 0 && syllable - 1 < plans.Length ? plans[syllable - 1] : null;
                if (plan != null) FinalReordering(buf, i, end, plan);
                i = end;
            }
        }

        private static void FinalReordering(L.GlyphBuffer buf, int start, int end, Plan plan)
        {
            var script = plan.Script;
            bool tryPref = plan.Pref != null;

            // Find the base again.
            int b;
            for (b = start; b < end; b++)
                if (buf[b].Position >= PosBaseC)
                {
                    if (tryPref && b + 1 < end)
                    {
                        for (int i = b + 1; i < end; i++)
                            if ((buf[i].Mask & MaskPref) != 0)
                            {
                                if (!(buf[i].Substituted && LigatedAndDidntMultiply(buf[i])))
                                {
                                    // A 'pref' candidate that formed nothing: the base is around here.
                                    b = i;
                                    while (b < end && IsHalant(buf[b])) b++;
                                    if (b < end) buf[b].Position = PosBaseC;
                                    tryPref = false;
                                }
                                break;
                            }
                        // Malayalam: skip over unformed below- (but not post-) forms.
                        if (script.Block == 0x0D00)
                        {
                            for (int i = b + 1; i < end; i++)
                            {
                                while (i < end && IsJoiner(buf[i])) i++;
                                if (i == end || !IsHalant(buf[i])) break;
                                i++;
                                while (i < end && IsJoiner(buf[i])) i++;
                                if (i < end && IsConsonant(buf[i]) && buf[i].Position == PosBelowC)
                                {
                                    b = i;
                                    buf[b].Position = PosBaseC;
                                }
                            }
                        }
                    }
                    if (start < b && buf[b].Position > PosBaseC) b--;
                    break;
                }
            if (b == end && start < b && buf[b - 1].Category == CatZWJ) b--;
            if (b < end) while (start < b && (buf[b].Category == CatN || buf[b].Category == CatH)) b--;

            // Reorder matras: a pre-base matra moves next to the base when half forms were made
            // — right after the last Halant before the base.
            if (start + 1 < end && start < b)
            {
                int newPos = b == end ? b - 2 : b - 1;
                if (script.Block != 0x0D00 && script.Block != 0x0B80)
                {
                    while (newPos > start && !(buf[newPos].Category == CatM || IsHalant(buf[newPos]))) newPos--;
                    // Only proceed past a Halant that does not belong to the matra itself, and is not followed by ZWJ.
                    if (IsHalant(buf[newPos]) && buf[newPos].Position != PosPreM)
                    {
                        if (newPos + 1 < end && buf[newPos + 1].Category == CatZWJ) newPos = start;
                    }
                    else newPos = start;
                }
                if (start < newPos && buf[newPos].Position != PosPreM)
                {
                    for (int i = newPos; i > start; i--)
                        if (buf[i - 1].Position == PosPreM)
                        {
                            int oldPos = i - 1;
                            if (oldPos < b && b <= newPos) b--;
                            Move(buf, oldPos, newPos);
                            newPos--;
                        }
                }
            }

            // Reorder reph: from the start of the syllable to its script's place.
            if (start + 1 < end && buf[start].Position == PosRaToBecomeReph
                && ((buf[start].Category == CatRepha) ^ LigatedAndDidntMultiply(buf[start])))
            {
                int newRephPos = -1;
                int rephPos = script.RephPos;
                if (rephPos != PosAfterPost)
                {
                    // 2. After the first explicit Halant between the reph and the base.
                    int np = start + 1;
                    while (np < b && !IsHalant(buf[np])) np++;
                    if (np < b && IsHalant(buf[np]))
                    {
                        if (np + 1 < b && IsJoiner(buf[np + 1])) np++;
                        newRephPos = np;
                    }
                    // 3. After the main consonant.
                    if (newRephPos < 0 && rephPos == PosAfterMain)
                    {
                        np = b;
                        while (np + 1 < end && buf[np + 1].Position <= PosAfterMain) np++;
                        if (np < end) newRephPos = np;
                    }
                    // 4. Before the first post-base consonant form.
                    if (newRephPos < 0 && rephPos == PosAfterSub)
                    {
                        np = b;
                        while (np + 1 < end && !(buf[np + 1].Position == PosPostC || buf[np + 1].Position == PosAfterPost || buf[np + 1].Position == PosSmvd)) np++;
                        if (np < end) newRephPos = np;
                    }
                }
                // 5. (step 2 again, for the after-post case)
                if (newRephPos < 0)
                {
                    int np = start + 1;
                    while (np < b && !IsHalant(buf[np])) np++;
                    if (np < b && IsHalant(buf[np]))
                    {
                        if (np + 1 < b && IsJoiner(buf[np + 1])) np++;
                        newRephPos = np;
                    }
                }
                // 6. The end of the syllable, before syllable modifiers — and before a Halant that
                //    follows a matra, so the reph can interact with the matra.
                if (newRephPos < 0)
                {
                    int np = end - 1;
                    while (np > start && buf[np].Position == PosSmvd) np--;
                    if (IsHalant(buf[np]))
                        for (int i = b + 1; i < np; i++)
                            if (buf[i].Category == CatM) { np--; break; }
                    newRephPos = np;
                }
                Move(buf, start, newRephPos);
                if (start < b && b <= newRephPos) b--;
            }

            // Reorder a pre-base-reordering consonant the font formed through 'pref': before the base.
            if (tryPref && b + 1 < end)
            {
                for (int i = b + 1; i < end; i++)
                    if ((buf[i].Mask & MaskPref) != 0)
                    {
                        if (LigatedAndDidntMultiply(buf[i]))
                        {
                            int newPos = b;
                            if (script.Block != 0x0D00 && script.Block != 0x0B80)
                                while (newPos > start && !(buf[newPos - 1].Category == CatM || IsHalant(buf[newPos - 1]))) newPos--;
                            if (newPos > start && IsHalant(buf[newPos - 1]))
                                if (newPos < end && IsJoiner(buf[newPos])) newPos++;
                            int oldPos = i;
                            Move(buf, oldPos, newPos);
                            if (newPos <= b && b < oldPos) b++;
                        }
                        break;
                    }
            }

            // 'init' on a left matra that starts a word.
            if (buf[start].Position == PosPreM)
            {
                if (start == 0 || !IsFormatOrMark(buf[start - 1])) buf[start].Mask |= MaskInit;
            }
        }

        private static bool IsFormatOrMark(L.ShapedGlyph g)
        {
            int c = g.Category;
            return c == CatZWJ || c == CatZWNJ || c == CatM || c == CatN || c == CatH || c == CatSM || c == CatVD || c == CatA;
        }
    }
}
