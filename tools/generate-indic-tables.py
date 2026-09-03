#!/usr/bin/env python3
"""Generate UnityGameTranslator.Core/TextShaping/IndicTables.g.cs from the Unicode Character Database.

The reordering of pre-base vowel signs (IndicReorderer.cs) is one rule driven by two Unicode
tables — which signs carry a part written to the LEFT of the consonant they follow in the
string, and which code points make up the consonant cluster that part jumps over. Nothing in
the rule names a script: a script is covered the day Unicode publishes its categories.

The OpenType shaper for the classic Indic scripts (IndicShaper.cs) needs the SAME two tables
in full for the ten blocks it covers (Devanagari to Sinhala, U+0900..U+0DFF): one syllabic
and one positional category per code point, emitted as byte arrays with the category names
as constants, so its syllable grammar is written against Unicode's vocabulary and not ours.

Inputs (downloaded from https://www.unicode.org/Public/UCD/latest/ucd/ unless given as paths):
  IndicPositionalCategory.txt   which visual position a dependent sign takes
  IndicSyllabicCategory.txt     what role a code point plays in a syllable
plus Python's unicodedata for the canonical decomposition of two-part signs.

Usage:  python tools/generate-indic-tables.py [dir-with-the-two-txt-files]
"""
import io
import os
import sys
import unicodedata
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "UnityGameTranslator.Core", "TextShaping", "IndicTables.g.cs")
BASE_URL = "https://www.unicode.org/Public/UCD/latest/ucd/"
FILES = ("IndicPositionalCategory.txt", "IndicSyllabicCategory.txt")

# A dependent sign with one of these positions carries a part drawn to the LEFT of the
# consonant cluster it follows in logical order. Visual_Order_Left is the opposite case —
# Thai, Lao, Tai Viet, New Tai Lue store such signs BEFORE the consonant already — and must
# never be touched.
LEFT_CATEGORIES = {
    "Left", "Left_And_Right", "Top_And_Left", "Top_And_Left_And_Right",
    "Top_And_Bottom_And_Left", "Bottom_And_Left",
}

# What a left part jumps over, scanning back from the sign: the cluster it belongs to. A
# cluster is a BASE consonant with what attaches to it (nukta, medials, subjoined and final
# consonants, killers, joiners, Khmer register shifters), bound to the previous base only by a
# BINDER (virama, invisible stacker). Two bare consonants in a row are two syllables: the sign
# after the second must not jump the first (अतिरिक्त has two i signs, one per consonant).
BASE_CATEGORIES = {
    "Consonant", "Consonant_Dead", "Consonant_With_Stacker", "Consonant_Placeholder",
    "Consonant_Prefixed", "Consonant_Head_Letter", "Consonant_Initial_Postfixed",
    "Consonant_Preceding_Repha",
}
BINDER_CATEGORIES = {"Virama", "Invisible_Stacker"}
ATTACH_CATEGORIES = {
    "Nukta", "Consonant_Medial", "Consonant_Subjoined", "Consonant_Final",
    "Consonant_Succeeding_Repha", "Gemination_Mark", "Register_Shifter", "Pure_Killer",
    "Consonant_Killer", "Reordering_Killer", "Joiner", "Non_Joiner",
}
JOINER_CATEGORIES = {"Joiner", "Non_Joiner"}

# Two-part signs Unicode does not decompose canonically, split here from the standard's own
# description of the glyph. Only where the parts are code points of their own: Khmer OO is
# written E (left) + AA (right); the other Khmer two-part vowels have no code point for their
# right or top part and stay whole (their glyph then sits before the base — the left part,
# the reading cue, in the right place; the rest not).
SYNTHETIC_SPLITS = {
    0x17C4: (0x17C1, 0x17B6),   # KHMER VOWEL SIGN OO = E + AA
}


def load(name, src_dir):
    if src_dir:
        path = os.path.join(src_dir, name)
        with io.open(path, encoding="utf-8") as f:
            return f.read()
    with urllib.request.urlopen(BASE_URL + name) as r:
        return r.read().decode("utf-8")


def parse(text):
    table, version = {}, "?"
    for line in text.splitlines():
        if line.startswith("# Indic") and ".txt" in line:
            version = line.split("-")[-1].replace(".txt", "").strip()
        body = line.split("#")[0].strip()
        if not body:
            continue
        rng, cat = [x.strip() for x in body.split(";")]
        parts = rng.split("..")
        a = int(parts[0], 16)
        b = int(parts[1], 16) if len(parts) > 1 else a
        for cp in range(a, b + 1):
            table[cp] = cat
    return table, version


def full_decomposition(cp):
    """Canonical decomposition applied until nothing decomposes further (Sinhala goes two deep)."""
    out = []
    dec = unicodedata.decomposition(chr(cp))
    if not dec or dec.startswith("<"):
        return None
    for part in dec.split():
        sub = full_decomposition(int(part, 16))
        out.extend(sub if sub else [int(part, 16)])
    return out


def ranges(cps):
    cps = sorted(cps)
    out = []
    for cp in cps:
        if out and out[-1][1] == cp - 1:
            out[-1][1] = cp
        else:
            out.append([cp, cp])
    return out


def main():
    src_dir = sys.argv[1] if len(sys.argv) > 1 else None
    pos, pos_version = parse(load(FILES[0], src_dir))
    syl, syl_version = parse(load(FILES[1], src_dir))

    left = sorted(cp for cp, cat in pos.items() if cat in LEFT_CATEGORIES)
    # Stored before their consonant already (Thai, Lao, Tai Viet, New Tai Lue): never moved —
    # and never the LAST thing before a word break, which the word breaker needs to know.
    visual_left = sorted(cp for cp, cat in pos.items() if cat == "Visual_Order_Left")
    bases = ranges(cp for cp, cat in syl.items() if cat in BASE_CATEGORIES)
    binders = ranges(cp for cp, cat in syl.items() if cat in BINDER_CATEGORIES)
    attach = ranges(cp for cp, cat in syl.items() if cat in ATTACH_CATEGORIES)
    joiners = ranges(cp for cp, cat in syl.items() if cat in JOINER_CATEGORIES)

    splits = {}
    for cp in left:
        parts = full_decomposition(cp)
        if parts is None and cp in SYNTHETIC_SPLITS:
            parts = list(SYNTHETIC_SPLITS[cp])
        if parts and len(parts) > 1:
            # The part that moves must itself be a left sign; the rest stays in place.
            assert pos.get(parts[0]) in LEFT_CATEGORIES, (hex(cp), [hex(p) for p in parts])
            splits[cp] = parts

    # Per-code-point categories for the classic Indic blocks (IndicShaper.cs).
    INDIC_FIRST, INDIC_LAST = 0x0900, 0x0DFF
    syl_names = sorted(set(syl.values()) | {"Other"})
    pos_names = sorted(set(pos.values()) | {"NA"})
    syl_bytes = [syl_names.index(syl.get(cp, "Other")) for cp in range(INDIC_FIRST, INDIC_LAST + 1)]
    pos_bytes = [pos_names.index(pos.get(cp, "NA")) for cp in range(INDIC_FIRST, INDIC_LAST + 1)]
    assert len(syl_names) < 256 and len(pos_names) < 256
    # Every canonical decomposition in those blocks, fully applied: what a shaper takes apart
    # before it reorders (two-part vowel signs, nukta consonants), whether or not the font has
    # the composed glyph — the composed form is never what the font's rules were written for.
    decompositions = {}
    for cp in range(INDIC_FIRST, INDIC_LAST + 1):
        parts = full_decomposition(cp)
        if parts and len(parts) > 1:
            decompositions[cp] = parts

    lines = []
    w = lines.append
    w("// <auto-generated>")
    w(f"//   By tools/generate-indic-tables.py from Unicode {pos_version} (IndicPositionalCategory,")
    w(f"//   IndicSyllabicCategory {syl_version}) and the canonical decompositions of Python's unicodedata.")
    w("//   Do not edit: rerun the generator. See IndicReorderer.cs for what these tables mean.")
    w("// </auto-generated>")
    w("namespace UnityGameTranslator.Core.TextShaping")
    w("{")
    w("    internal static class IndicTables")
    w("    {")
    w(f"        internal const string UnicodeVersion = \"{pos_version}\";")
    w("")
    w("        /// <summary>Signs with a part drawn to the LEFT of the cluster they follow — sorted code points.</summary>")
    w("        internal static readonly int[] LeftSigns =")
    w("        {")
    for i in range(0, len(left), 8):
        w("            " + ", ".join(f"0x{cp:04X}" for cp in left[i:i + 8]) + ",")
    w("        };")
    w("")
    def emit_ranges(name, doc, rs):
        w(f"        /// <summary>{doc} — inclusive ranges, sorted, as (first, last) pairs.</summary>")
        w(f"        internal static readonly int[] {name} =")
        w("        {")
        for i in range(0, len(rs), 4):
            w("            " + ", ".join(f"0x{a:04X}, 0x{b:04X}" for a, b in rs[i:i + 4]) + ",")
        w("        };")
        w("")

    w("        /// <summary>Signs stored in visual order, BEFORE their consonant (Visual_Order_Left) — sorted code points. Never moved; a word never ends on one.</summary>")
    w("        internal static readonly int[] VisualOrderLeft =")
    w("        {")
    for i in range(0, len(visual_left), 8):
        w("            " + ", ".join(f"0x{cp:04X}" for cp in visual_left[i:i + 8]) + ",")
    w("        };")
    w("")
    emit_ranges("BaseRanges", "Base consonants: what a cluster is built on", bases)
    emit_ranges("BinderRanges", "Binders: a virama or stacker joining two bases into one cluster", binders)
    emit_ranges("AttachRanges", "What attaches to a base (nukta, medials, killers, joiners...)", attach)
    emit_ranges("JoinerRanges", "Joiners allowed between a binder and the next base", joiners)
    w("        /// <summary>")
    w("        /// Two-part signs and their parts: the first part is the one that moves, the rest stay")
    w("        /// where the sign was. Flattened as (sign, count, parts...), sorted by sign.")
    w("        /// </summary>")
    w("        internal static readonly int[] Splits =")
    w("        {")
    for cp in sorted(splits):
        parts = splits[cp]
        w(f"            0x{cp:04X}, {len(parts)}, " + ", ".join(f"0x{p:04X}" for p in parts) + ",")
    w("        };")
    w("")
    w("        /// <summary>")
    w("        /// Indic_Syllabic_Category and Indic_Positional_Category of every code point from")
    w("        /// U+0900 to U+0DFF (the classic Indic blocks the OpenType shaper covers), one byte")
    w("        /// each, as indices into the name constants below. Unlisted code points are Other / NA.")
    w("        /// </summary>")
    w(f"        internal const int IndicFirst = 0x{INDIC_FIRST:04X}, IndicLast = 0x{INDIC_LAST:04X};")
    w("")
    w("        internal static class Syllabic")
    w("        {")
    for i, name in enumerate(syl_names):
        w(f"            internal const byte {name} = {i};")
    w("        }")
    w("")
    w("        internal static class Positional")
    w("        {")
    for i, name in enumerate(pos_names):
        w(f"            internal const byte {name} = {i};")
    w("        }")
    w("")
    w("        internal static readonly byte[] SyllabicOf =")
    w("        {")
    for i in range(0, len(syl_bytes), 32):
        w("            " + ", ".join(str(b) for b in syl_bytes[i:i + 32]) + ",")
    w("        };")
    w("")
    w("        internal static readonly byte[] PositionalOf =")
    w("        {")
    for i in range(0, len(pos_bytes), 32):
        w("            " + ", ".join(str(b) for b in pos_bytes[i:i + 32]) + ",")
    w("        };")
    w("")
    w("        /// <summary>")
    w("        /// Full canonical decompositions of U+0900..U+0DFF, flattened as (code point, count,")
    w("        /// parts...), sorted by code point — everything Unicode takes apart, exclusions included.")
    w("        /// </summary>")
    w("        internal static readonly int[] Decompositions =")
    w("        {")
    for cp in sorted(decompositions):
        parts = decompositions[cp]
        w(f"            0x{cp:04X}, {len(parts)}, " + ", ".join(f"0x{p:04X}" for p in parts) + ",")
    w("        };")
    w("    }")
    w("}")
    with io.open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{OUT}: {len(left)} left signs, {len(bases)} base ranges, {len(binders)} binder ranges, {len(attach)} attach ranges, {len(splits)} splits, {len(decompositions)} decompositions, {len(syl_names)} syllabic / {len(pos_names)} positional categories, Unicode {pos_version}")


if __name__ == "__main__":
    main()
