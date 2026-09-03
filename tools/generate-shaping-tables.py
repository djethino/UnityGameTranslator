#!/usr/bin/env python3
"""Generate UnityGameTranslator.Core/TextShaping/ShapingTables.g.cs from the Unicode Character Database.

What the shapers other than the classic Indic one need, and IndicTables.g.cs does not carry —
every table covers the whole code space, the supplementary planes included:

  CombiningClasses        canonical combining class of every combining mark, with HarfBuzz's
                          modifications (Hebrew, Arabic, Telugu, Thai, Lao, Tibetan) — the order
                          marks are sorted into before shaping
  Decompositions          first-level canonical decompositions, with whether the pair
                          recomposes (composition exclusions) — the normalizer's two directions
  DefaultIgnorable        what a shaper hides (joiners, variation selectors, format controls)
  MyanmarKhmerCategories  HarfBuzz's Indic-table categories for the Myanmar and Khmer blocks
                          (gen-indic-table.py's mapping and overrides, reproduced)
  UseCategories           the Universal Shaping Engine categories (gen-use-table.py, reproduced)
                          for every script HarfBuzz routes to it
  UseScripts              which scripts those are (hb-ot-shaper.hh's list, minus Sinhala, which
                          the classic Indic shaper keeps here)
  JoiningTypes            Arabic-style joining types of the USE scripts that join
  Scripts                 the script of every code point, for cutting a string into runs
  ScriptTags              the OpenType script tag of every script (ISO 15924 in lower case, with
                          HarfBuzz's five exceptions) — the Indic and Myanmar shapers add their
                          own "2" tags on top
  RightToLeftScripts      the scripts whose letters carry a right-to-left bidi class
  VowelConstraints        the vowel-sign sequences the USE specification forbids, between which
                          every shaper inserts a dotted circle (hb-ot-shaper-vowel-constraints)

Inputs (downloaded from https://www.unicode.org/Public/UCD/latest/ucd/ unless given a dir):
  IndicSyllabicCategory.txt IndicPositionalCategory.txt ArabicShaping.txt
  DerivedCoreProperties.txt UnicodeData.txt Blocks.txt Scripts.txt PropertyValueAliases.txt
plus HarfBuzz's three ms-use files (from its repository, MIT): the two "Additional" property
files and IndicShapingInvalidCluster.txt, and Python's unicodedata for combining classes,
decompositions and NFC.

Usage:  python tools/generate-shaping-tables.py [dir-with-the-txt-files]
"""
import io
import os
import sys
import unicodedata
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "UnityGameTranslator.Core", "TextShaping", "ShapingTables.g.cs")
UCD = "https://www.unicode.org/Public/UCD/latest/ucd/"
HB = "https://raw.githubusercontent.com/harfbuzz/harfbuzz/main/src/ms-use/"
FILES = ["IndicSyllabicCategory.txt", "IndicPositionalCategory.txt", "ArabicShaping.txt",
         "DerivedCoreProperties.txt", "UnicodeData.txt", "Blocks.txt", "Scripts.txt", "PropertyValueAliases.txt"]
MS_FILES = ["IndicSyllabicCategory-Additional.txt", "IndicPositionalCategory-Additional.txt", "IndicShapingInvalidCluster.txt"]
CODE_SPACE = 0x110000


def load(name, src_dir, base):
    if src_dir:
        path = os.path.join(src_dir, name)
        if os.path.exists(path):
            with io.open(path, encoding="utf-8") as f:
                return f.read()
    with urllib.request.urlopen(base + name) as r:
        return r.read().decode("utf-8")


def parse_props(text, field=1):
    """UCD 'range ; value' files -> {cp: value}; version from the header."""
    table, version = {}, "?"
    for line in text.splitlines():
        if line.startswith("#") and ".txt" in line and "-" in line and version == "?":
            version = line.split("-")[-1].replace(".txt", "").strip()
        body = line.split("#")[0].strip()
        if not body:
            continue
        fields = [x.strip() for x in body.split(";")]
        if len(fields) <= field:
            continue
        parts = fields[0].split("..")
        a = int(parts[0], 16)
        b = int(parts[1], 16) if len(parts) > 1 else a
        for cp in range(a, b + 1):
            table[cp] = fields[field]
    return table, version


def parse_derived(text, wanted):
    table = {}
    for line in text.splitlines():
        body = line.split("#")[0].strip()
        if not body:
            continue
        fields = [x.strip() for x in body.split(";")]
        if len(fields) < 2 or fields[1] != wanted:
            continue
        parts = fields[0].split("..")
        a = int(parts[0], 16)
        b = int(parts[1], 16) if len(parts) > 1 else a
        for cp in range(a, b + 1):
            table[cp] = True
    return table


def parse_unicode_data(text):
    """General category and bidi class per code point, First/Last ranges expanded."""
    gc, bidi = {}, {}
    first = None
    for line in text.splitlines():
        fields = line.split(";")
        if len(fields) < 5:
            continue
        cp = int(fields[0], 16)
        name, cat, bc = fields[1], fields[2], fields[4]
        if name.endswith(", First>"):
            first = cp
            continue
        if name.endswith(", Last>"):
            for c in range(first, cp + 1):
                gc[c] = cat
                bidi[c] = bc
            first = None
            continue
        gc[cp] = cat
        bidi[cp] = bc
    return gc, bidi


def parse_blocks(text):
    blocks = []
    for line in text.splitlines():
        body = line.split("#")[0].strip()
        if not body:
            continue
        rng, name = [x.strip() for x in body.split(";")]
        a, b = rng.split("..")
        blocks.append((int(a, 16), int(b, 16), name))
    return blocks


def parse_script_aliases(text):
    """PropertyValueAliases.txt: long script name -> ISO 15924 code."""
    codes = {}
    for line in text.splitlines():
        body = line.split("#")[0].strip()
        if not body.startswith("sc "):
            continue
        fields = [x.strip() for x in body.split(";")]
        if len(fields) >= 3:
            codes[fields[2]] = fields[1]
    return codes


def parse_invalid_clusters(text):
    """IndicShapingInvalidCluster.txt: the forbidden sequences, each a list of code points."""
    seqs = []
    for line in text.splitlines():
        body = line.split("#")[0].strip()
        if not body:
            continue
        cps = [int(x, 16) for x in body.split(";")[0].split()]
        if len(cps) >= 2:
            seqs.append(cps)
    return seqs


def block_of(blocks, cp):
    for a, b, name in blocks:
        if a <= cp <= b:
            return name
    return "No_Block"


def ranges(cps):
    out = []
    for cp in sorted(cps):
        if out and out[-1][1] == cp - 1:
            out[-1][1] = cp
        else:
            out.append([cp, cp])
    return out


def runs(mapping):
    """(start, end, value) runs over the sorted keys with equal values."""
    out = []
    for cp in sorted(mapping):
        v = mapping[cp]
        if out and out[-1][1] == cp - 1 and out[-1][2] == v:
            out[-1][1] = cp
        else:
            out.append([cp, cp, v])
    return out


def code_points():
    for cp in range(CODE_SPACE):
        if 0xD800 <= cp <= 0xDFFF:
            continue
        yield cp


# HarfBuzz's modified combining classes (hb-unicode.hh): the reorderings Uniscribe applies.
MODIFIED_CCC = {
    10: 22, 11: 15, 12: 16, 13: 17, 14: 23, 15: 18, 16: 19, 17: 20, 18: 21, 19: 14,
    20: 24, 21: 12, 22: 25, 23: 13, 24: 10, 25: 11, 26: 26,
    27: 28, 28: 29, 29: 30, 30: 31, 31: 32, 32: 33, 33: 27, 34: 34, 35: 35, 36: 36,
    84: 4, 91: 5, 103: 3, 107: 107, 118: 118, 122: 122, 129: 129, 130: 132, 132: 131,
}

# gen-indic-table.py, reproduced for the Myanmar and Khmer blocks.
INDIC_CATEGORY_MAP = {
    "Other": "X", "Avagraha": "Symbol", "Bindu": "SM", "Brahmi_Joining_Number": "PLACEHOLDER",
    "Cantillation_Mark": "A", "Consonant": "C", "Consonant_Dead": "C", "Consonant_Final": "CM",
    "Consonant_Head_Letter": "C", "Consonant_Initial_Postfixed": "C", "Consonant_Killer": "M",
    "Consonant_Medial": "CM", "Consonant_Placeholder": "PLACEHOLDER",
    "Consonant_Preceding_Repha": "Repha", "Consonant_Prefixed": "X", "Consonant_Subjoined": "CM",
    "Consonant_Succeeding_Repha": "CM", "Consonant_With_Stacker": "CS", "Gemination_Mark": "SM",
    "Invisible_Stacker": "H", "Joiner": "ZWJ", "Modifying_Letter": "X", "Non_Joiner": "ZWNJ",
    "Nukta": "N", "Number": "PLACEHOLDER", "Number_Joiner": "PLACEHOLDER", "Pure_Killer": "M",
    "Register_Shifter": "RS", "Syllable_Modifier": "SM", "Tone_Letter": "X", "Tone_Mark": "N",
    "Virama": "H", "Visarga": "SM", "Vowel": "V", "Vowel_Dependent": "M", "Vowel_Independent": "V",
}
INDIC_POSITION_MAP = {
    "Not_Applicable": "END", "Left": "PRE_C", "Top": "ABOVE_C", "Bottom": "BELOW_C", "Right": "POST_C",
    "Bottom_And_Right": "POST_C", "Left_And_Right": "POST_C", "Top_And_Bottom": "BELOW_C",
    "Top_And_Bottom_And_Left": "BELOW_C", "Top_And_Bottom_And_Right": "POST_C", "Top_And_Left": "ABOVE_C",
    "Top_And_Left_And_Right": "POST_C", "Top_And_Right": "POST_C", "Overstruck": "AFTER_MAIN",
    "Visual_Order_Left": "PRE_M",
}
INDIC_CATEGORY_OVERRIDES = {
    0x2015: "PLACEHOLDER", 0x2022: "PLACEHOLDER", 0x25FB: "PLACEHOLDER", 0x25FC: "PLACEHOLDER",
    0x25FD: "PLACEHOLDER", 0x25FE: "PLACEHOLDER", 0x25CC: "DOTTEDCIRCLE",
    # Khmer
    0x179A: "Ra", 0x17CC: "Robatic", 0x17C9: "Robatic", 0x17CA: "Robatic",
    0x17C6: "Xgroup", 0x17CB: "Xgroup", 0x17CD: "Xgroup", 0x17CE: "Xgroup", 0x17CF: "Xgroup",
    0x17D0: "Xgroup", 0x17D1: "Xgroup", 0x17C7: "Ygroup", 0x17C8: "Ygroup", 0x17DD: "Ygroup",
    0x17D3: "Ygroup", 0x17D9: "PLACEHOLDER",
    # Myanmar
    0x104E: "C", 0x1004: "Ra", 0x101B: "Ra", 0x105A: "Ra", 0x1032: "A", 0x1036: "A", 0x103A: "As",
    0x103E: "MH", 0x1060: "ML", 0x103C: "MR", 0x103D: "MW", 0x1082: "MW", 0x103B: "MY", 0x105E: "MY",
    0x105F: "MY", 0x1063: "PT", 0x1064: "PT", 0x1069: "PT", 0x106A: "PT", 0x106B: "PT", 0x106C: "PT",
    0x106D: "PT", 0xAA7B: "PT", 0x1038: "SM", 0x1087: "SM", 0x1088: "SM", 0x1089: "SM", 0x108A: "SM",
    0x108B: "SM", 0x108C: "SM", 0x108D: "SM", 0x108F: "SM", 0x109A: "SM", 0x109B: "SM", 0x109C: "SM",
    0x104A: "PLACEHOLDER",
}
for _vs in range(0xFE00, 0xFE10):
    INDIC_CATEGORY_OVERRIDES[_vs] = "VS"
SYLLABIC_NAMES = ["X", "C", "V", "N", "H", "ZWNJ", "ZWJ", "M", "SM", "A", "VD", "PLACEHOLDER", "DOTTEDCIRCLE",
                  "RS", "MPst", "Repha", "Ra", "CM", "Symbol", "CS", "SMPst",
                  "VAbv", "VBlw", "VPre", "VPst", "Robatic", "Xgroup", "Ygroup",
                  "As", "MH", "MR", "MW", "MY", "PT", "VS", "ML"]
MYANMAR_KHMER_BLOCKS = {"Myanmar", "Myanmar Extended-A", "Myanmar Extended-B", "Myanmar Extended-C", "Khmer"}

# The scripts HarfBuzz routes to the universal engine (hb-ot-shaper.hh, hb_ot_shaper_categorize),
# in its order, all planes. Sinhala is on its list and not on this one: the classic Indic shaper
# keeps it here (its own configuration line, HarfBuzz's). A name absent from Scripts.txt is a
# script newer than the UCD in hand — reported, not fatal.
USE_SCRIPTS = [
    "Tibetan", "Mongolian", "Buhid", "Hanunoo", "Tagalog", "Tagbanwa", "Limbu", "Tai_Le", "Buginese",
    "Kharoshthi", "Syloti_Nagri", "Tifinagh", "Balinese", "Nko", "Phags_Pa", "Cham", "Kayah_Li", "Lepcha",
    "Rejang", "Saurashtra", "Sundanese", "Egyptian_Hieroglyphs", "Javanese", "Kaithi", "Meetei_Mayek",
    "Tai_Tham", "Tai_Viet", "Batak", "Brahmi", "Mandaic", "Chakma", "Miao", "Sharada", "Takri", "Duployan",
    "Grantha", "Khojki", "Khudawadi", "Mahajani", "Manichaean", "Modi", "Pahawh_Hmong", "Psalter_Pahlavi",
    "Siddham", "Tirhuta", "Ahom", "Multani", "Adlam", "Bhaiksuki", "Marchen", "Newa", "Masaram_Gondi",
    "Soyombo", "Zanabazar_Square", "Dogra", "Gunjala_Gondi", "Hanifi_Rohingya", "Makasar", "Medefaidrin",
    "Old_Sogdian", "Sogdian", "Elymaic", "Nandinagari", "Nyiakeng_Puachue_Hmong", "Wancho", "Chorasmian",
    "Dives_Akuru", "Khitan_Small_Script", "Yezidi", "Cypro_Minoan", "Old_Uyghur", "Tangsa", "Toto",
    "Vithkuqi", "Kawi", "Nag_Mundari", "Garay", "Gurung_Khema", "Kirat_Rai", "Ol_Onal", "Sunuwar", "Todhri",
    "Tulu_Tigalari", "Beria_Erfe", "Sidetic", "Tai_Yo", "Tolong_Siki",
]

# hb-ot-tag.cc hb_ot_old_tag_from_script: the ISO 15924 code in lower case, except these.
SCRIPT_TAG_EXCEPTIONS = {"Hiragana": "kana", "Lao": "lao ", "Yi": "yi  ", "Nko": "nko ", "Vai": "vai "}

USE_VALUES = {
    "O": 0, "B": 1, "N": 4, "GB": 5, "CGJ": 6, "SUB": 11, "H": 12, "HN": 13, "ZWNJ": 14, "WJ": 16, "R": 18,
    "CS": 43, "IS": 44, "Sk": 48, "G": 49, "J": 50, "SB": 51, "SE": 52, "HVM": 53, "HM": 54, "HR": 55, "RK": 56,
    "FAbv": 24, "FBlw": 25, "FPst": 26, "MAbv": 27, "MBlw": 28, "MPst": 29, "MPre": 30, "CMAbv": 31, "CMBlw": 32,
    "VAbv": 33, "VBlw": 34, "VPst": 35, "VPre": 22, "VMAbv": 37, "VMBlw": 38, "VMPst": 39, "VMPre": 23,
    "SMAbv": 41, "SMBlw": 42, "FMAbv": 45, "FMBlw": 46, "FMPst": 47,
}
USE_POSITIONS = {
    "F": {"Abv": ["Top"], "Blw": ["Bottom"], "Pst": ["Right"]},
    "M": {"Abv": ["Top"], "Blw": ["Bottom", "Bottom_And_Left", "Bottom_And_Right"], "Pst": ["Right"],
          "Pre": ["Left", "Top_And_Bottom_And_Left"]},
    "CM": {"Abv": ["Top"], "Blw": ["Bottom", "Overstruck"]},
    "V": {"Abv": ["Top", "Top_And_Bottom", "Top_And_Bottom_And_Right", "Top_And_Right"],
          "Blw": ["Bottom", "Overstruck", "Bottom_And_Right"], "Pst": ["Right"],
          "Pre": ["Left", "Top_And_Left", "Top_And_Left_And_Right", "Left_And_Right"]},
    "VM": {"Abv": ["Top"], "Blw": ["Bottom", "Overstruck"], "Pst": ["Right"], "Pre": ["Left"]},
    "SM": {"Abv": ["Top"], "Blw": ["Bottom"]},
    "FM": {"Abv": ["Top"], "Blw": ["Bottom"], "Pst": ["Not_Applicable"]},
}


def use_category(U, UISC, UIPC, AJT, UDI, UGC):
    def is_base():
        return (UISC in ("Number", "Consonant", "Consonant_Head_Letter", "Tone_Letter", "Vowel_Independent")
                or (AJT in ("C", "D", "L", "R") and UISC != "Joiner")
                or (UGC == "Lo" and UISC in ("Avagraha", "Bindu", "Consonant_Final", "Consonant_Medial",
                                             "Consonant_Subjoined", "Vowel", "Vowel_Dependent")))

    def is_base_other():
        return UISC == "Consonant_Placeholder" or U in (0x2015, 0x2022, 0x25FB, 0x25FC, 0x25FD, 0x25FE)

    def is_cgj():
        return UISC == "Joiner" or (UDI and UGC in ("Mc", "Me", "Mn"))

    def is_sym_mod():
        return UISC == "Symbol_Modifier"

    def is_sakot():
        return U == 0x1A60

    def is_word_joiner():
        return (UDI and U not in (0x115F, 0x1160, 0x3164, 0xFFA0, 0x1BCA0, 0x1BCA1, 0x1BCA2, 0x1BCA3)
                and UISC == "Other" and not is_cgj()) or UGC == "Cn"

    tests = [
        ("B", is_base),
        ("N", lambda: UISC == "Brahmi_Joining_Number"),
        ("GB", is_base_other),
        ("CGJ", is_cgj),
        ("F", lambda: (UISC == "Consonant_Final" and UGC != "Lo") or UISC == "Consonant_Succeeding_Repha"),
        ("FM", lambda: UISC == "Syllable_Modifier"),
        ("M", lambda: (UISC == "Consonant_Medial" and UGC != "Lo") or UISC == "Consonant_Initial_Postfixed"),
        ("CM", lambda: UISC in ("Nukta", "Gemination_Mark", "Consonant_Killer")),
        ("SUB", lambda: UISC == "Consonant_Subjoined" and UGC != "Lo"),
        ("CS", lambda: UISC == "Consonant_With_Stacker"),
        ("H", lambda: UISC == "Virama" and U != 0x0DCA),
        ("HVM", lambda: U == 0x0DCA),
        ("HN", lambda: UISC == "Number_Joiner"),
        ("G", lambda: UISC == "Hieroglyph"),
        ("HM", lambda: UISC == "Hieroglyph_Modifier"),
        ("HR", lambda: UISC == "Hieroglyph_Mirror"),
        ("J", lambda: UISC == "Hieroglyph_Joiner"),
        ("SB", lambda: UISC in ("Hieroglyph_Mark_Begin", "Hieroglyph_Segment_Begin")),
        ("SE", lambda: UISC in ("Hieroglyph_Mark_End", "Hieroglyph_Segment_End")),
        ("IS", lambda: UISC == "Invisible_Stacker" and not is_sakot()),
        ("ZWNJ", lambda: UISC == "Non_Joiner"),
        ("O", lambda: (UGC == "Po" or UISC in ("Consonant_Dead", "Joiner", "Modifying_Letter", "Other"))
                      and not is_base() and not is_base_other() and not is_cgj() and not is_sym_mod()
                      and not is_word_joiner()),
        ("RK", lambda: UISC == "Reordering_Killer"),
        ("R", lambda: UISC in ("Consonant_Preceding_Repha", "Consonant_Prefixed")),
        ("Sk", is_sakot),
        ("SM", is_sym_mod),
        ("V", lambda: UISC == "Pure_Killer" or (UGC != "Lo" and UISC in ("Vowel", "Vowel_Dependent"))),
        ("VM", lambda: UISC in ("Tone_Mark", "Cantillation_Mark", "Register_Shifter", "Visarga")
                       or (UGC != "Lo" and UISC == "Bindu")),
        ("WJ", is_word_joiner),
    ]
    values = [k for k, f in tests if f()]
    if len(values) != 1:
        return None
    use = values[0]
    pos = USE_POSITIONS.get(use)
    if pos:
        found = [k for k, v in pos.items() if UIPC in v]
        if len(found) != 1:
            return None
        use += found[0]
    return use


def myanmar_khmer_category(cp, syl, pos):
    cat = INDIC_CATEGORY_MAP.get(syl.get(cp, "Other"), "X")
    p = pos.get(cp, "Not_Applicable")
    if cat == "SM" and p == "Not_Applicable":
        cat = "SMPst"
    ipos = INDIC_POSITION_MAP.get(p, "END")
    cat = INDIC_CATEGORY_OVERRIDES.get(cp, cat)
    if cat in ("M", "MPst"):
        cat = {"PRE_C": "VPre", "ABOVE_C": "VAbv", "BELOW_C": "VBlw", "POST_C": "VPst"}.get(ipos, "VPst")
    return cat


def main():
    src_dir = sys.argv[1] if len(sys.argv) > 1 else None
    syl, version = parse_props(load(FILES[0], src_dir, UCD))
    pos, _ = parse_props(load(FILES[1], src_dir, UCD))
    ajt, _ = parse_props(load(FILES[2], src_dir, UCD), field=2)
    udi = parse_derived(load(FILES[3], src_dir, UCD), "Default_Ignorable_Code_Point")
    gc, bidi = parse_unicode_data(load(FILES[4], src_dir, UCD))
    blocks = parse_blocks(load(FILES[5], src_dir, UCD))
    scripts, _ = parse_props(load(FILES[6], src_dir, UCD))
    iso_codes = parse_script_aliases(load(FILES[7], src_dir, UCD))
    # HarfBuzz's additions: Microsoft's USE data for characters Unicode leaves unassigned.
    syl_add, _ = parse_props(load(MS_FILES[0], src_dir, HB))
    pos_add, _ = parse_props(load(MS_FILES[1], src_dir, HB))
    for cp, v in syl_add.items():
        syl[cp] = "Syllable_Modifier" if v == "Consonant_Final_Modifier" else v
    for cp, v in pos_add.items():
        pos[cp] = "Not_Applicable" if v == "NA" else v
    invalid_clusters = parse_invalid_clusters(load(MS_FILES[2], src_dir, HB))

    script_names = sorted(set(scripts.values()) | {"Unknown"})
    script_index = {name: i for i, name in enumerate(script_names)}
    use_names = [s for s in USE_SCRIPTS if s in script_index]
    missing_use = [s for s in USE_SCRIPTS if s not in script_index]
    use_set = set(use_names)

    # 1. Combining classes, HarfBuzz-modified, every plane.
    ccc = {}
    for cp in code_points():
        c = unicodedata.combining(chr(cp))
        if c:
            ccc[cp] = MODIFIED_CCC.get(c, c)

    # 2. First-level canonical decompositions, every plane, with whether the pair recomposes.
    decomp = {}
    for cp in code_points():
        if cp < 0x00C0:
            continue
        d = unicodedata.decomposition(chr(cp))
        if not d or d.startswith("<"):
            continue
        parts = [int(x, 16) for x in d.split()]
        a, b = parts[0], parts[1] if len(parts) > 1 else 0
        composes = b != 0 and unicodedata.normalize("NFC", chr(a) + chr(b)) == chr(cp)
        decomp[cp] = (a, b, composes)

    # 3. Default ignorable.
    ignorable = ranges(cp for cp in udi)

    # 4. Myanmar / Khmer categories, as HarfBuzz's Indic table classes them.
    mk = {}
    for cp in code_points():
        if scripts.get(cp) not in ("Myanmar", "Khmer") or block_of(blocks, cp) not in MYANMAR_KHMER_BLOCKS:
            continue
        cat = myanmar_khmer_category(cp, syl, pos)
        if cat != "X":
            mk[cp] = cat
    for cp in (0x25CC, 0x2015, 0x2022, 0x25FB, 0x25FC, 0x25FD, 0x25FE):
        mk[cp] = INDIC_CATEGORY_OVERRIDES[cp]
    mk[0x00A0] = "PLACEHOLDER"
    mk[0x200C] = "ZWNJ"
    mk[0x200D] = "ZWJ"

    # 5. USE categories for the scripts routed to it, HarfBuzz's derivation.
    use = {}
    unresolved = []
    for cp in code_points():
        if scripts.get(cp, "Unknown") not in use_set:
            continue
        UISC = syl.get(cp, "Other")
        UIPC = pos.get(cp, "Not_Applicable")
        if 0x0F18 <= cp <= 0x0F19 or 0x0F3E <= cp <= 0x0F3F:
            UISC = "Vowel_Dependent"
        cat = use_category(cp, UISC, UIPC, ajt.get(cp, "U"), cp in udi, gc.get(cp, "Cn"))
        if cat is None:
            unresolved.append(cp)
            cat = "O"
        if cat != "O":
            use[cp] = USE_VALUES[cat]
    # Common characters a cluster may hold, whatever the script.
    for cp, cat in ((0x25CC, "B"), (0x200C, "ZWNJ"), (0x200D, "CGJ"), (0x034F, "CGJ"), (0x2060, "WJ"),
                    (0x00A0, "GB"), (0x2015, "GB"), (0x2022, "GB"), (0x25FB, "GB"), (0x25FC, "GB"),
                    (0x25FD, "GB"), (0x25FE, "GB")):
        use[cp] = USE_VALUES[cat]
    for vs in range(0xFE00, 0xFE10):
        use[vs] = USE_VALUES["CGJ"]

    # 6. Joining types of the USE scripts that join (Arabic-style forms), every plane.
    JT_VALUES = {"U": 0, "C": 1, "D": 2, "L": 3, "R": 4, "T": 5}
    jt = {}
    for cp, t in ajt.items():
        if scripts.get(cp) not in use_set or t == "U":
            continue
        jt[cp] = JT_VALUES.get(t, 0)
    for cp in code_points():
        if scripts.get(cp) in use_set and gc.get(cp) in ("Mn", "Me", "Cf") and cp not in jt:
            jt[cp] = JT_VALUES["T"]

    # 7. Scripts of every code point, as runs; names as constants; their OpenType tags.
    script_runs = runs({cp: script_index[v] for cp, v in scripts.items()})
    tags = []
    for name in script_names:
        if name in SCRIPT_TAG_EXCEPTIONS:
            tags.append(SCRIPT_TAG_EXCEPTIONS[name])
        elif name in ("Common", "Inherited", "Unknown"):
            tags.append("DFLT")
        else:
            tags.append(iso_codes[name].lower())

    # 8. Right-to-left scripts: a letter of theirs carries a right-to-left bidi class.
    rtl_scripts = set()
    for cp, bc in bidi.items():
        if bc in ("R", "AL") and gc.get(cp, "").startswith("L"):
            s = scripts.get(cp, "Unknown")
            if s not in ("Common", "Inherited", "Unknown"):
                rtl_scripts.add(script_index[s])

    # 9. Vowel constraints: sequences sorted; a sequence that is the prefix of a longer one
    #    makes the longer one redundant (HarfBuzz's trie keeps the shorter).
    seqs = sorted(set(tuple(s) for s in invalid_clusters))
    kept = [s for s in seqs if not any(len(t) < len(s) and s[:len(t)] == t for t in seqs)]
    too_long = [s for s in kept if len(s) > 3]
    if too_long:
        sys.exit("vowel constraint longer than three code points: " + ", ".join("%04X" % c for c in too_long[0]))

    use_runs = runs(use)
    jt_runs = runs(jt)

    lines = []
    w = lines.append
    w("// <auto-generated>")
    w(f"//   By tools/generate-shaping-tables.py from Unicode {version} (IndicSyllabicCategory,")
    w("//   IndicPositionalCategory, ArabicShaping, DerivedCoreProperties, UnicodeData, Blocks, Scripts,")
    w("//   PropertyValueAliases), HarfBuzz's ms-use files, and Python's unicodedata. Do not edit: rerun the generator.")
    w("// </auto-generated>")
    w("namespace UnityGameTranslator.Core.TextShaping")
    w("{")
    w("    internal static class ShapingTables")
    w("    {")
    w(f"        internal const string UnicodeVersion = \"{version}\";")
    w("")
    w("        /// <summary>Combining class of every combining mark, HarfBuzz-modified — flattened (code point, class), sorted.</summary>")
    w("        internal static readonly int[] CombiningClasses =")
    w("        {")
    items = sorted(ccc.items())
    for i in range(0, len(items), 8):
        w("            " + ", ".join(f"0x{cp:04X}, {c}" for cp, c in items[i:i + 8]) + ",")
    w("        };")
    w("")
    w("        /// <summary>First-level canonical decompositions — flattened (composed, first, second or 0, 1 when the pair recomposes), sorted.</summary>")
    w("        internal static readonly int[] Decompositions =")
    w("        {")
    items = sorted(decomp.items())
    for i in range(0, len(items), 4):
        w("            " + ", ".join(f"0x{cp:04X}, 0x{a:04X}, 0x{b:04X}, {1 if c else 0}" for cp, (a, b, c) in items[i:i + 4]) + ",")
    w("        };")
    w("")
    w("        /// <summary>Default_Ignorable_Code_Point — inclusive ranges, sorted, as (first, last) pairs.</summary>")
    w("        internal static readonly int[] DefaultIgnorable =")
    w("        {")
    for i in range(0, len(ignorable), 4):
        w("            " + ", ".join(f"0x{a:04X}, 0x{b:04X}" for a, b in ignorable[i:i + 4]) + ",")
    w("        };")
    w("")
    w("        /// <summary>HarfBuzz's Indic-table categories for the Myanmar and Khmer blocks — flattened (code point, category), sorted.</summary>")
    w("        internal static class Syllabic")
    w("        {")
    for i, name in enumerate(SYLLABIC_NAMES):
        w(f"            internal const byte {name} = {i};")
    w("        }")
    w("")
    w("        internal static readonly int[] MyanmarKhmerCategories =")
    w("        {")
    items = sorted(mk.items())
    for i in range(0, len(items), 8):
        w("            " + ", ".join(f"0x{cp:04X}, {SYLLABIC_NAMES.index(c)}" for cp, c in items[i:i + 8]) + ",")
    w("        };")
    w("")
    w("        /// <summary>Universal Shaping Engine categories (HarfBuzz's values) — runs (first, last, category), sorted. Unlisted = O.</summary>")
    w("        internal static class Use")
    w("        {")
    for name, value in sorted(USE_VALUES.items(), key=lambda kv: kv[1]):
        w(f"            internal const byte {name} = {value};")
    w("        }")
    w("")
    w("        internal static readonly int[] UseCategories =")
    w("        {")
    for i in range(0, len(use_runs), 6):
        w("            " + ", ".join(f"0x{a:04X}, 0x{b:04X}, {v}" for a, b, v in use_runs[i:i + 6]) + ",")
    w("        };")
    w("")
    w("        /// <summary>Joining types of the USE scripts that join — runs (first, last, type): U=0 C=1 D=2 L=3 R=4 T=5.</summary>")
    w("        internal static readonly int[] JoiningTypes =")
    w("        {")
    for i in range(0, len(jt_runs), 6):
        w("            " + ", ".join(f"0x{a:04X}, 0x{b:04X}, {v}" for a, b, v in jt_runs[i:i + 6]) + ",")
    w("        };")
    w("")
    w("        /// <summary>Script of every code point — runs (first, last, script), sorted. Unlisted = Unknown.</summary>")
    w("        internal static class Script")
    w("        {")
    for name in script_names:
        w(f"            internal const int {name} = {script_index[name]};")
    w("        }")
    w("")
    w("        internal static readonly int[] Scripts =")
    w("        {")
    for i in range(0, len(script_runs), 6):
        w("            " + ", ".join(f"0x{a:04X}, 0x{b:04X}, {v}" for a, b, v in script_runs[i:i + 6]) + ",")
    w("        };")
    w("")
    w("        /// <summary>OpenType script tag of every script, by script index (ISO 15924 in lower case; HarfBuzz's exceptions kept; DFLT for Common, Inherited and Unknown).</summary>")
    w("        internal static readonly string[] ScriptTags =")
    w("        {")
    for i in range(0, len(tags), 8):
        w("            " + ", ".join(f"\"{t}\"" for t in tags[i:i + 8]) + ",")
    w("        };")
    w("")
    w("        /// <summary>The scripts HarfBuzz routes to the Universal Shaping Engine (script indices, sorted).</summary>")
    w("        internal static readonly int[] UseScripts =")
    w("        {")
    use_indices = sorted(script_index[s] for s in use_names)
    for i in range(0, len(use_indices), 16):
        w("            " + ", ".join(str(v) for v in use_indices[i:i + 16]) + ",")
    w("        };")
    w("")
    w("        /// <summary>The scripts whose letters carry a right-to-left bidi class (script indices, sorted).</summary>")
    w("        internal static readonly int[] RightToLeftScripts =")
    w("        {")
    rtl_indices = sorted(rtl_scripts)
    for i in range(0, len(rtl_indices), 16):
        w("            " + ", ".join(str(v) for v in rtl_indices[i:i + 16]) + ",")
    w("        };")
    w("")
    w("        /// <summary>Vowel-sign sequences the USE specification forbids — flattened (first, second, third or 0), sorted by first; a dotted circle goes before the last one.</summary>")
    w("        internal static readonly int[] VowelConstraints =")
    w("        {")
    for i in range(0, len(kept), 4):
        w("            " + ", ".join(f"0x{s[0]:04X}, 0x{s[1]:04X}, 0x{(s[2] if len(s) > 2 else 0):04X}" for s in kept[i:i + 4]) + ",")
    w("        };")
    w("    }")
    w("}")
    with io.open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{OUT}: {len(ccc)} combining classes, {len(decomp)} decompositions, {len(ignorable)} ignorable ranges, "
          f"{len(mk)} Myanmar/Khmer categories, {len(use_runs)} USE runs over {len(use_names)} scripts ({len(unresolved)} unresolved"
          f"{': ' + ', '.join('%04X' % u for u in unresolved[:8]) if unresolved else ''}), "
          f"{len(jt_runs)} joining runs, {len(script_runs)} script runs, {len(script_names)} scripts, "
          f"{len(rtl_indices)} right-to-left, {len(kept)} vowel constraints, Unicode {version}")
    if missing_use:
        print(f"USE scripts not in this UCD (newer than {version}): {', '.join(missing_use)}")


if __name__ == "__main__":
    main()
