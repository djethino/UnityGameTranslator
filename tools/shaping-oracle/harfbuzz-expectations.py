#!/usr/bin/env python3
"""HarfBuzz as the oracle for the Indic shaper (IndicShaper.cs).

Two jobs:

  python harfbuzz-expectations.py write
      Shape the word lists below with each Noto font in tests/.../TestData/Fonts and write
      tests/.../TestData/Shaping/<Script>.txt — one "word<TAB>expected" line per word, the
      expected being HarfBuzz's glyphs and positions in font units. IndicShaperChecks reads
      those files: what the checks prove is agreement with the reference shaper the fonts
      were built against, word by word, glyph by glyph, unit by unit.

  python harfbuzz-expectations.py compare <font.ttf> [Script ...]
      Diff our shaper against HarfBuzz on any font — including ones that cannot be committed
      (a system font such as Nirmala UI, extracted from its .ttc with fontTools). Needs the
      checks project built (dotnet build in tests/UnityGameTranslator.Core.Checks): it runs
      its `shape` verb.

Needs uharfbuzz (pip install uharfbuzz) — in a venv, never in the main installation.
Glyph ids and positions depend on the exact font file: regenerate after replacing a font.

Notation: glyph(advance,xOffset,yOffset), space separated. HarfBuzz keeps ZWJ/ZWNJ as
zero-width glyphs; our shaper drops them (they have done their work) — the oracle drops
them too so the two compare.
"""
import io
import os
import subprocess
import sys

import uharfbuzz as hb

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
CHECKS = os.path.normpath(os.path.join(HERE, "..", "..", "tests", "UnityGameTranslator.Core.Checks"))
FONTS = os.path.join(CHECKS, "TestData", "Fonts")
OUT = os.path.join(CHECKS, "TestData", "Shaping")
DLL = os.path.join(CHECKS, "bin", "Debug", "net8.0", "UnityGameTranslator.Core.Checks.dll")
JOINERS = "‌‍"

# Words chosen to exercise what each script's shaping does: conjuncts, half forms, reph,
# below- and post-base forms, pre-base and two-part vowel signs, nukta, joiners, marks.
WORDS = {
    "Devanagari": ["ज्ञान", "त्रिशूल", "श्रम", "बुद्ध", "ट्टा", "ङ्क", "ऋषि", "ॐ", "कँ", "र्क", "र्कि", "र्कं", "क्‍ष", "क्‌ष", "प्रकाशन", "राष्ट्रीय", "संस्कृति", "अनुवाद", "विश्वविद्यालय", "हिंदी", "ऑफ़", "ज़्यादा", "क़लम", "द्वारा", "पूर्व", "र्‍य", "कर्त्ता", "स्त्री", "ह्रस्व", "द्य", "क्त", "ङ्ग", "प्त", "न्न", "ल्ल", "श्च", "ष्ट", "ट्ठ", "द्म", "ह्म", "ह्न", "ह्य", "ह्र", "ह्ल", "ह्व", "रु", "रू", "रृ", "छ्र", "ठ्र", "ढ्र", "ड्र", "गृह", "नृत्य", "वृक्ष", "पृथ्वी", "किंतु", "सर्दी", "मुर्गी", "अर्जुन", "कार्य", "धर्म", "र्धी", "गर्व", "उर्दू", "दिल", "बिल्कुल", "किताब", "लिखना", "सिर्फ", "मिर्च", "प्रिंट", "स्क्रिप्ट", "ऐसे", "औरत", "ओर", "ऊपर", "इसे", "ईद", "अंग्रेज़ी", "क्ष", "कि", "किं", "प्रिय", "कर्म", "हिन्दी", "अतिरिक्त", "दिल्ली", "कृपया", "कैं", "क्रम", "रुपया", "विकल्प", "ट्रक", "शक्ति", "सर्वोत्तम", "र्"],
    "Bengali": ["বাংলা", "কর্ম", "প্রিয়", "ক্ষমা", "স্ত্রী", "বিশ্ব", "ন্ত্র", "হৃদয়", "দুর্গা", "শ্রী", "ক্রিয়া", "উৎসব", "সূর্য", "ট্রেন", "গ্রন্থ", "আন্তর্জাতিক", "র্যা", "কো", "কৌ", "কে", "কৈ", "কি", "কী", "কু", "কূ", "কৃ", "ক্র", "র্ক", "র্কো", "ঙ্ক", "ঞ্চ", "ণ্ড", "ন্দ", "ম্ব", "ল্ল", "ষ্ট", "স্ব", "হ্ম", "ক্ত", "জ্ঞ", "ত্ত", "দ্ধ", "ব্দ", "শ্চ", "ন্ত্রী", "ক্র্য", "খ্রিস্ট", "অন্ত", "উপন্যাস", "চিত্র", "মন্ত্রী", "কার্য", "র্‍য"],
    "Tamil": ["தமிழ்", "க்ஷ", "ஸ்ரீ", "கொ", "கோ", "கை", "கௌ", "கெ", "கே", "கி", "கீ", "கு", "கூ", "விளையாட்டு", "மொழி", "பெயர்", "தொடர்", "நன்றி", "சிறப்பு", "க்ஷேத்திரம்", "ஔ", "ணை", "ளை", "னை", "டி", "டீ", "டு", "டூ", "ஜொ", "ஷொ", "ஹோ", "ஸ்"],
    "Malayalam": ["മലയാളം", "ക്ഷ", "ന്റെ", "ക്ര", "പ്ര", "കൊ", "കോ", "കൌ", "കൗ", "ർ", "ൽ", "സ്ത്രീ", "ക്ക", "ഹൃദയം", "ശ്രീ", "ദ്ധ", "ങ്ക", "ഞ്ച", "ണ്ട", "ന്ന", "മ്പ", "ല്ല", "വ്യ", "ര്യ", "ക്യ", "ക്വ", "ഗ്ര", "ത്ര", "ദ്ര", "ബ്ര", "ക്ല", "പ്ല", "കെ", "കേ", "കൈ", "കി", "കീ", "കു", "കൂ", "കൃ", "ക്‍", "ന്‍", "ണ്‍", "ല്‍", "ള്‍", "ര്‍", "ക്‌ഷ", "ത്ത", "ച്ച", "പ്പ", "ട്ട", "ദ്ദ", "സ്സ", "ഹ്ന", "സ്ഥ", "ഷ്ട", "ന്ധ", "മ്മ", "യ്യ", "വ്വ", "ന്മ", "ഗ്ന", "ന്ത", "ണ്മ"],
    "Kannada": ["ಕನ್ನಡ", "ಕ್ಷ", "ರ್ಕ", "ಕ್ರ", "ಪ್ರೀತಿ", "ಸ್ತ್ರೀ", "ಶ್ರೀ", "ಕೊ", "ಕೋ", "ಕೈ", "ಕೌ", "ಕೆ", "ಕೇ", "ಕಿ", "ಕೀ", "ಕು", "ಕೂ", "ಕೃ", "ಅರ್ಥ", "ವಿಶ್ವ", "ದ್ವಾರ", "ಜ್ಞಾನ", "ಟ್ಟ", "ಗ್ಗ", "ದ್ದ", "ನ್ನ", "ಲ್ಲ", "ಂ", "ಃ", "ರ್ಕೆ", "ರ್ಕೊ", "ಕ್ರೊ", "ಕ್ರೈ", "ಸ್ಕ್ರೀನ್", "ಬ್ಯಾಂಕ್", "ಪ್ಲೇ"],
    "Telugu": ["తెలుగు", "క్ష", "ర్క", "క్ర", "ప్రేమ", "స్త్రీ", "శ్రీ", "కొ", "కో", "కై", "కౌ", "కె", "కే", "కి", "కీ", "కు", "కూ", "కృ", "అర్థం", "విశ్వం", "ద్వారా", "ట్రక్", "జ్ఞానం", "ట్ట", "గ్గ", "ద్ద", "న్న", "ల్ల", "ర్కె", "ర్కొ", "క్రొ", "క్రై", "స్క్రీన్", "బ్యాంక్", "ప్లే", "ర్‌", "ఱ"],
    "Gujarati": ["ગુજરાતી", "ક્ષ", "પ્રિય", "કર્મ", "સ્ત્રી", "વિશ્વ", "શ્રી", "ટ્રેન", "હૃદય", "દ્વારા", "ર્યુ", "કો", "કૌ", "કે", "કૈ", "કિ", "કી", "કુ", "કૂ", "કૃ", "ક્ર", "ર્ક", "ર્કિ", "જ્ઞ", "ત્ર", "દ્ધ", "ટ્ટ", "ન્ન", "લ્લ", "રુ", "રૂ", "દ્ર", "ડ્ર", "ઢ્ર", "છ્ર", "ઠ્ર", "કિં"],
    "Gurmukhi": ["ਪੰਜਾਬੀ", "ਪ੍ਰ", "ਸ੍ਵ", "ਕ੍ਰਿਪਾ", "ਵਿਸ਼ਵ", "ਸ਼੍ਰੀ", "ਸਿੱਖ", "ਗੁਰੂ", "ਹੱਥ", "ਕਿ", "ਸਿੰਘ", "ਨ੍ਹ", "ਕੋ", "ਕੌ", "ਕੇ", "ਕੈ", "ਕੀ", "ਕੁ", "ਕੂ", "ਕਿੰ", "ਕਿੱ", "ਦ੍ਰ", "ਤ੍ਰ", "ਸ੍ਯ", "ਪ੍ਯ", "ਯ", "ਲ਼", "ਸ਼", "ਖ਼", "ਗ਼", "ਜ਼", "ਫ਼"],
    "Oriya": ["ଓଡ଼ିଆ", "କ୍ଷ", "ପ୍ର", "କର୍ମ", "ସ୍ତ୍ରୀ", "ବିଶ୍ୱ", "ଶ୍ରୀ", "ଟ୍ରେନ", "ହୃଦୟ", "ଦ୍ୱାରା", "କୋ", "କୈ", "କୌ", "କେ", "କି", "କୀ", "କୁ", "କୂ", "କୃ", "କ୍ର", "ର୍କ", "ର୍କି", "ଜ୍ଞ", "ତ୍ର", "ଦ୍ଧ", "ଟ୍ଟ", "ନ୍ନ", "ଲ୍ଲ", "ଙ୍କ", "ଞ୍ଚ", "ଣ୍ଡ", "ନ୍ଦ", "ମ୍ବ", "ର୍କୋ", "ର୍କୌ"],
    "Sinhala": ["සිංහල", "ක්‍ෂ", "ශ්‍රී", "ක්‍ර", "ප්‍ර", "කො", "කෝ", "කෞ", "කෛ", "ස්ත්‍රී", "ර්‍ය", "ද්‍ව", "ක්‍ෂේත්‍ර", "කෙ", "කේ", "කි", "කී", "කු", "කූ", "කෘ", "ක්", "ර්", "න්", "ල්", "ම්", "ට්", "ර්‍ණ", "ක්‍ය", "ත්‍ය", "න්‍ය", "ද්‍ය", "ඳ", "ඟ", "ඬ", "ඹ", "රු", "රූ", "ළු", "ළූ", "ගු", "ගූ", "තු", "තූ", "භූ"],
}


def harfbuzz(path, words):
    face = hb.Face(hb.Blob.from_file_path(path))
    font = hb.Font(face)
    font.scale = (face.upem, face.upem)
    # What a joiner becomes in HarfBuzz's output: its own glyph, or the space glyph it is
    # hidden behind — blank and zero-width either way.
    blanks = {font.get_nominal_glyph(ord(c)) for c in JOINERS + " "} - {None, 0}
    out = {}
    for w in words:
        buf = hb.Buffer()
        buf.add_str(w)
        buf.guess_segment_properties()
        hb.shape(font, buf)
        parts = []
        for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
            # A joiner HarfBuzz kept as a blank zero-width glyph: not part of the comparison.
            if pos.x_advance == 0 and pos.x_offset == 0 and pos.y_offset == 0 and info.codepoint in blanks and any(c in w for c in JOINERS):
                continue
            parts.append(f"{info.codepoint}({pos.x_advance},{pos.x_offset},{pos.y_offset})")
        out[w] = " ".join(parts)
    return out


def ours(path, words):
    res = subprocess.run(["dotnet", DLL, "shape", path] + words, capture_output=True, encoding="utf-8")
    if res.returncode != 0:
        print(res.stderr[:2000])
        return {}
    out = {}
    for line in res.stdout.splitlines():
        if "\t" not in line:
            continue
        w, rest = line.split("\t", 1)
        out[w] = " ".join(t.split("@")[0] + t[t.index("("):] for t in rest.split()) if rest else ""
    return out


def write():
    os.makedirs(OUT, exist_ok=True)
    for script, words in WORDS.items():
        path = os.path.join(FONTS, f"NotoSans{script}.ttf")
        if not os.path.exists(path):
            print(f"{script}: no font at {path}")
            continue
        expected = harfbuzz(path, words)
        with io.open(os.path.join(OUT, f"{script}.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write(f"# HarfBuzz {hb.version_string()} on NotoSans{script}.ttf — glyph(advance,xOffset,yOffset) in font units. Generated by tools/shaping-oracle/harfbuzz-expectations.py; do not edit.\n")
            for w in words:
                f.write(f"{w}\t{expected[w]}\n")
        print(f"{script}: {len(words)} words")


def compare(path, scripts):
    total = bad = 0
    for script in scripts:
        words = WORDS[script]
        h = harfbuzz(path, words)
        o = ours(path, words)
        diffs = [(w, h[w], o.get(w, "<missing>")) for w in words if h[w] != o.get(w)]
        total += len(words)
        bad += len(diffs)
        print(f"== {script}: {len(words) - len(diffs)}/{len(words)} agree")
        for w, hh, oo in diffs:
            print(f"   {w}\t{' '.join('%04X' % ord(c) for c in w)}\n      hb : {hh}\n      us : {oo}")
    print(f"\nTOTAL {total - bad}/{total} agree")


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "write":
        write()
    elif len(sys.argv) >= 3 and sys.argv[1] == "compare":
        compare(sys.argv[2], sys.argv[3:] or list(WORDS))
    else:
        print(__doc__)
        sys.exit(2)
