#!/usr/bin/env python3
"""HarfBuzz as the oracle for the shapers (TextShaping/*.cs).

Three jobs:

  python harfbuzz-expectations.py derive
      Build the old-specification test fonts from the Noto ones in tests/.../TestData/Fonts:
      the same lookups, with the "2" script tag removed or renamed (dev2 → nothing, so deva
      is what is left; knd2 → knda…), subset to their block. HarfBuzz and our shaper then
      both take the old-specification path on them — the only way to check that path, no
      shipped font of that kind being at hand. Rerun after replacing a source font.

  python harfbuzz-expectations.py write
      Shape the word lists below with each font and write tests/.../TestData/Shaping/
      <Name>.txt — one "word<TAB>expected" line per word, the expected being HarfBuzz's
      glyphs and positions in font units. IndicShaperChecks reads those files: what the
      checks prove is agreement with the reference shaper the fonts were built against,
      word by word, glyph by glyph, unit by unit.

  python harfbuzz-expectations.py compare <font.ttf> [Name ...]
      Diff our shaper against HarfBuzz on any font — including ones that cannot be committed
      (a system font such as Nirmala UI, extracted from its .ttc with fontTools). Needs the
      checks project built (dotnet build in tests/UnityGameTranslator.Core.Checks): it runs
      its `shape` verb.

Needs uharfbuzz and fontTools (pip install uharfbuzz fonttools) — in a venv, never in the
main installation. Glyph ids and positions depend on the exact font file: regenerate after
replacing a font.

Notation: glyph(advance,xOffset,yOffset), space separated. HarfBuzz keeps ZWJ/ZWNJ as
zero-width glyphs; our shaper drops them (they have done their work) — the oracle drops
them too so the two compare. A right-to-left script is recorded in HarfBuzz's visual order,
the reverse of ours; the file's header says so (rtl=1) and the check reverses.
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

# The font behind each list: NotoSans<Name>.ttf unless said otherwise (Noto ships Tibetan as
# Serif only; the old-specification variants are derived, see `derive`).
FONT_FILE = {
    "Tibetan": "NotoSerifTibetan.ttf",
    "Devanagari-oldspec": "NotoSansDevanagari-deva.ttf",
    "Bengali-oldspec": "NotoSansBengali-beng.ttf",
    "Kannada-oldspec": "NotoSansKannada-knda.ttf",
    "Malayalam-oldspec": "NotoSansMalayalam-mlym.ttf",
}
RTL = {"Hebrew", "Adlam"}

# How each old-specification font is derived: (source, script tag to drop, tag to rename
# {old: new}, Unicode ranges to keep). Dropping dev2 leaves deva; a font with a single "2"
# tag has it renamed. The scripts stay sorted, as the ScriptList's binary search requires.
OLD_SPEC = {
    "NotoSansDevanagari-deva.ttf": ("NotoSansDevanagari.ttf", "dev2", {}, [(0x0900, 0x097F), (0xA8E0, 0xA8FF)]),
    "NotoSansBengali-beng.ttf": ("NotoSansBengali.ttf", None, {"bng2": "beng"}, [(0x0980, 0x09FF)]),
    "NotoSansKannada-knda.ttf": ("NotoSansKannada.ttf", None, {"knd2": "knda"}, [(0x0C80, 0x0CFF)]),
    "NotoSansMalayalam-mlym.ttf": ("NotoSansMalayalam.ttf", "mlm2", {}, [(0x0D00, 0x0D7F)]),
}
COMMON_KEEP = [(0x0020, 0x0020), (0x200C, 0x200D), (0x25CC, 0x25CC), (0x0964, 0x0965), (0x0030, 0x0039)]


def font_for(name):
    return os.path.join(FONTS, FONT_FILE.get(name, f"NotoSans{name}.ttf"))


# Words chosen to exercise what each script's shaping does: conjuncts, half forms, reph,
# below- and post-base forms, pre-base and two-part vowel signs, nukta, joiners, marks — and
# the vowel sequences the USE specification forbids (a dotted circle goes between them).
WORDS = {
    "Devanagari": ["ज्ञान", "त्रिशूल", "श्रम", "बुद्ध", "ट्टा", "ङ्क", "ऋषि", "ॐ", "कँ", "र्क", "र्कि", "र्कं", "क्‍ष", "क्‌ष", "प्रकाशन", "राष्ट्रीय", "संस्कृति", "अनुवाद", "विश्वविद्यालय", "हिंदी", "ऑफ़", "ज़्यादा", "क़लम", "द्वारा", "पूर्व", "र्‍य", "कर्त्ता", "स्त्री", "ह्रस्व", "द्य", "क्त", "ङ्ग", "प्त", "न्न", "ल्ल", "श्च", "ष्ट", "ट्ठ", "द्म", "ह्म", "ह्न", "ह्य", "ह्र", "ह्ल", "ह्व", "रु", "रू", "रृ", "छ्र", "ठ्र", "ढ्र", "ड्र", "गृह", "नृत्य", "वृक्ष", "पृथ्वी", "किंतु", "सर्दी", "मुर्गी", "अर्जुन", "कार्य", "धर्म", "र्धी", "गर्व", "उर्दू", "दिल", "बिल्कुल", "किताब", "लिखना", "सिर्फ", "मिर्च", "प्रिंट", "स्क्रिप्ट", "ऐसे", "औरत", "ओर", "ऊपर", "इसे", "ईद", "अंग्रेज़ी", "क्ष", "कि", "किं", "प्रिय", "कर्म", "हिन्दी", "अतिरिक्त", "दिल्ली", "कृपया", "कैं", "क्रम", "रुपया", "विकल्प", "ट्रक", "शक्ति", "सर्वोत्तम", "र्",
                   "अा", "अॅ", "उु", "एे", "र्इ", "आै", "अो", "अाा", "क्‍", "क्ष्‍", "प्र्‍"],
    "Bengali": ["বাংলা", "কর্ম", "প্রিয়", "ক্ষমা", "স্ত্রী", "বিশ্ব", "ন্ত্র", "হৃদয়", "দুর্গা", "শ্রী", "ক্রিয়া", "উৎসব", "সূর্য", "ট্রেন", "গ্রন্থ", "আন্তর্জাতিক", "র্যা", "কো", "কৌ", "কে", "কৈ", "কি", "কী", "কু", "কূ", "কৃ", "ক্র", "র্ক", "র্কো", "ঙ্ক", "ঞ্চ", "ণ্ড", "ন্দ", "ম্ব", "ল্ল", "ষ্ট", "স্ব", "হ্ম", "ক্ত", "জ্ঞ", "ত্ত", "দ্ধ", "ব্দ", "শ্চ", "ন্ত্রী", "ক্র্য", "খ্রিস্ট", "অন্ত", "উপন্যাস", "চিত্র", "মন্ত্রী", "কার্য", "র্‍য",
                "অা", "ঋৃ", "ক্‍"],
    "Tamil": ["தமிழ்", "க்ஷ", "ஸ்ரீ", "கொ", "கோ", "கை", "கௌ", "கெ", "கே", "கி", "கீ", "கு", "கூ", "விளையாட்டு", "மொழி", "பெயர்", "தொடர்", "நன்றி", "சிறப்பு", "க்ஷேத்திரம்", "ஔ", "ணை", "ளை", "னை", "டி", "டீ", "டு", "டூ", "ஜொ", "ஷொ", "ஹோ", "ஸ்",
              "அூ", "க்‍"],
    "Malayalam": ["മലയാളം", "ക്ഷ", "ന്റെ", "ക്ര", "പ്ര", "കൊ", "കോ", "കൌ", "കൗ", "ർ", "ൽ", "സ്ത്രീ", "ക്ക", "ഹൃദയം", "ശ്രീ", "ദ്ധ", "ങ്ക", "ഞ്ച", "ണ്ട", "ന്ന", "മ്പ", "ല്ല", "വ്യ", "ര്യ", "ക്യ", "ക്വ", "ഗ്ര", "ത്ര", "ദ്ര", "ബ്ര", "ക്ല", "പ്ല", "കെ", "കേ", "കൈ", "കി", "കീ", "കു", "കൂ", "കൃ", "ക്‍", "ന്‍", "ണ്‍", "ല്‍", "ള്‍", "ര്‍", "ക്‌ഷ", "ത്ത", "ച്ച", "പ്പ", "ട്ട", "ദ്ദ", "സ്സ", "ഹ്ന", "സ്ഥ", "ഷ്ട", "ന്ധ", "മ്മ", "യ്യ", "വ്വ", "ന്മ", "ഗ്ന", "ന്ത", "ണ്മ",
                  "ഇൗ", "ഒാ"],
    "Kannada": ["ಕನ್ನಡ", "ಕ್ಷ", "ರ್ಕ", "ಕ್ರ", "ಪ್ರೀತಿ", "ಸ್ತ್ರೀ", "ಶ್ರೀ", "ಕೊ", "ಕೋ", "ಕೈ", "ಕೌ", "ಕೆ", "ಕೇ", "ಕಿ", "ಕೀ", "ಕು", "ಕೂ", "ಕೃ", "ಅರ್ಥ", "ವಿಶ್ವ", "ದ್ವಾರ", "ಜ್ಞಾನ", "ಟ್ಟ", "ಗ್ಗ", "ದ್ದ", "ನ್ನ", "ಲ್ಲ", "ಂ", "ಃ", "ರ್ಕೆ", "ರ್ಕೊ", "ಕ್ರೊ", "ಕ್ರೈ", "ಸ್ಕ್ರೀನ್", "ಬ್ಯಾಂಕ್", "ಪ್ಲೇ",
                "ಉಾ", "ಒೌ", "ಕ್‍"],
    "Telugu": ["తెలుగు", "క్ష", "ర్క", "క్ర", "ప్రేమ", "స్త్రీ", "శ్రీ", "కొ", "కో", "కై", "కౌ", "కె", "కే", "కి", "కీ", "కు", "కూ", "కృ", "అర్థం", "విశ్వం", "ద్వారా", "ట్రక్", "జ్ఞానం", "ట్ట", "గ్గ", "ద్ద", "న్న", "ల్ల", "ర్కె", "ర్కొ", "క్రొ", "క్రై", "స్క్రీన్", "బ్యాంక్", "ప్లే", "ర్‌", "ఱ",
               "ఒౕ", "కిౕ", "క్‍"],
    "Gujarati": ["ગુજરાતી", "ક્ષ", "પ્રિય", "કર્મ", "સ્ત્રી", "વિશ્વ", "શ્રી", "ટ્રેન", "હૃદય", "દ્વારા", "ર્યુ", "કો", "કૌ", "કે", "કૈ", "કિ", "કી", "કુ", "કૂ", "કૃ", "ક્ર", "ર્ક", "ર્કિ", "જ્ઞ", "ત્ર", "દ્ધ", "ટ્ટ", "ન્ન", "લ્લ", "રુ", "રૂ", "દ્ર", "ડ્ર", "ઢ્ર", "છ્ર", "ઠ્ર", "કિં",
                 "અા", "અાૅ", "અૅ", "કૅા", "ક્‍"],
    "Gurmukhi": ["ਪੰਜਾਬੀ", "ਪ੍ਰ", "ਸ੍ਵ", "ਕ੍ਰਿਪਾ", "ਵਿਸ਼ਵ", "ਸ਼੍ਰੀ", "ਸਿੱਖ", "ਗੁਰੂ", "ਹੱਥ", "ਕਿ", "ਸਿੰਘ", "ਨ੍ਹ", "ਕੋ", "ਕੌ", "ਕੇ", "ਕੈ", "ਕੀ", "ਕੁ", "ਕੂ", "ਕਿੰ", "ਕਿੱ", "ਦ੍ਰ", "ਤ੍ਰ", "ਸ੍ਯ", "ਪ੍ਯ", "ਯ", "ਲ਼", "ਸ਼", "ਖ਼", "ਗ਼", "ਜ਼", "ਫ਼",
                 "ਅਾ", "ੲਿ", "ੳੁ", "ਕ੍‍"],
    "Oriya": ["ଓଡ଼ିଆ", "କ୍ଷ", "ପ୍ର", "କର୍ମ", "ସ୍ତ୍ରୀ", "ବିଶ୍ୱ", "ଶ୍ରୀ", "ଟ୍ରେନ", "ହୃଦୟ", "ଦ୍ୱାରା", "କୋ", "କୈ", "କୌ", "କେ", "କି", "କୀ", "କୁ", "କୂ", "କୃ", "କ୍ର", "ର୍କ", "ର୍କି", "ଜ୍ଞ", "ତ୍ର", "ଦ୍ଧ", "ଟ୍ଟ", "ନ୍ନ", "ଲ୍ଲ", "ଙ୍କ", "ଞ୍ଚ", "ଣ୍ଡ", "ନ୍ଦ", "ମ୍ବ", "ର୍କୋ", "ର୍କୌ",
              "ଅା", "ଏୗ", "କ୍‍"],
    "Thai": ["สวัสดี", "เริ่ม", "น้ำ", "ป่า", "ปู่", "กี่", "กุ๊ก", "ฎู", "ญุ", "ที่นี่", "ตั้ง", "เกม", "ตัวเลือก", "ข้อเสนอแนะ", "ไม่มี", "ผู้เล่น", "ดำ", "ทำ", "จำ", "ก่ำ", "ปิ่น", "ป้า", "ปั้น", "ฟ้า", "ฝั่ง", "อยู่", "ผู้", "ฐ", "ฐุ", "ญู", "ฏุ", "ฟิ้ว", "ป๋า", "ป๊า", "ปํ", "กํา"],
    "Lao": ["ສະບາຍດີ", "ນ້ຳ", "ເຈົ້າ", "ກຳ", "ບໍ່", "ຢູ່", "ຫຼື", "ຫຼວງ", "ຂ້ອຍ", "ຕັ້ງ", "ໜ້າ", "ໝາ", "ຕົວເລືອກ", "ເລີ່ມ", "ຊ່ວຍ", "ດຳ", "ຈຳ", "ກໍ່"],
    "Hebrew": ["שָׁלוֹם", "בְּרֵאשִׁית", "נִקּוּד", "יִשְׂרָאֵל", "הַמֶּלֶךְ", "שֶׁלִּי", "אֱלֹהִים", "וַיֹּאמֶר", "מִשְׁפָּט", "עֲבֹדָה", "בָּרוּךְ", "חֶסֶד", "יְרוּשָׁלַיִם", "שָׁבוּעַ", "לְמַעַן", "כֹּהֵן", "מִצְוָה", "וַיְהִי", "בַּיִת", "שָּׁ", "בְּ", "וֹ", "פָֽ"],
    "Khmer": ["ភាសាខ្មែរ", "ក្រុង", "ស្រី", "ញ្ញ", "កោ", "កៅ", "ក្សត្រ", "ព្រះ", "កំពុង", "ខ្ញុំ", "ស្អាត", "ជម្រាប", "ទាំង", "ការ", "កែ", "កើ", "កៀ", "កេ", "កុ", "កូ", "កួ", "កំ", "កះ", "ក្ក", "ក្រ", "ខ្មែរ", "ស្ត្រី", "ប្រ", "ក្ត", "ង្ក", "ណ្ត", "ន្ត", "ម្ព", "ល្អ", "អ្ន", "ក៏", "ក័", "ក៌", "ក៍", "ក៊", "ក់", "ខ្មែ", "ចំណុច", "ជ្រើសរើស", "ចាប់ផ្តើម", "ត្រឡប់"],
    "Myanmar": ["မြန်မာ", "ကျောင်း", "ခြင်္သေ့", "ဩ", "ကြွ", "ဏ္ဍ", "ချစ်", "ကို", "ကော", "ကေ", "ကဲ", "ကံ", "ကး", "ကျ", "ကြ", "ကွ", "ကှ", "ကျွ", "ကြွ", "ကျွှ", "ကြွှ", "ငြိမ်း", "စက္ကူ", "သင်္ဘော", "ဗုဒ္ဓ", "ဝိဇ္ဇာ", "ကုန်", "ကြိုး", "ကွေ့", "ရွေး", "ဂိမ်း", "ဆက်တင်", "ထွက်", "ပြန်", "အစ", "ဦး", "ဣ", "ဤ", "ကၠ", "ၐ", "ၚ", "ၜ", "ႀ", "ကႇ"],
    "Tibetan": ["བཀྲ་ཤིས", "སྒྲ", "རྒྱ", "བོད", "ཀྱི", "སྐད", "འབྲུག", "གཉིས", "བསྒྲུབས", "ཧཱུྃ", "ཨོཾ", "ཀྵ", "ཊ", "རྫོང", "ལྷ", "ཕྱུ", "སྤྲུལ", "གྲྭ", "ཀོ", "ཀེ", "ཀུ", "ཀི", "ཀཿ", "ཀྃ", "ཀྲྀ", "རྙིང"],
    "Javanese": ["ꦗꦮ", "ꦱꦸꦫꦧꦪ", "ꦲꦏ꧀ꦱꦫ", "ꦏ꧀ꦫ", "ꦏꦺ", "ꦏꦼ", "ꦏꦶ", "ꦏꦸ", "ꦏꦺꦴ", "ꦏꦻ", "ꦏꦴ", "ꦏꦽ", "ꦏꦾ", "ꦏꦿ", "ꦔ꧀ꦒ", "ꦤ꧀ꦠ", "ꦥ꧀ꦥ", "ꦧꦸꦢꦪ", "ꦧꦱ", "ꦲꦸꦩ꧀ꦥꦸꦭ꧀", "ꦯ", "ꦏꦁ", "ꦏꦃ", "ꦏꦀ", "ꦥꦼꦂ"],
    "Balinese": ["ᬩᬮᬶ", "ᬅᬓ᭄ᬱᬭ", "ᬓ᭄ᬭ", "ᬓᬾ", "ᬓᭀ", "ᬓᭁ", "ᬓᬶ", "ᬓᬸ", "ᬓᬵ", "ᬓᬃ", "ᬓᬂ", "ᬓᬄ", "ᬓᬁ", "ᬓ᭄ᬬ", "ᬓ᭄ᬯ", "ᬦ᭄ᬢ", "ᬩ᭄ᬯ", "ᬲ᭄ᬯᬭ", "ᬧᬢ᭄ᬦᬶ", "ᬒᬁ", "ᬓᬺ", "ᬓᬻ"],
    "Sinhala": ["සිංහල", "ක්‍ෂ", "ශ්‍රී", "ක්‍ර", "ප්‍ර", "කො", "කෝ", "කෞ", "කෛ", "ස්ත්‍රී", "ර්‍ය", "ද්‍ව", "ක්‍ෂේත්‍ර", "කෙ", "කේ", "කි", "කී", "කු", "කූ", "කෘ", "ක්", "ර්", "න්", "ල්", "ම්", "ට්", "ර්‍ණ", "ක්‍ය", "ත්‍ය", "න්‍ය", "ද්‍ය", "ඳ", "ඟ", "ඬ", "ඹ", "රු", "රූ", "ළු", "ළූ", "ගු", "ගූ", "තු", "තූ", "භූ",
                "අා", "එ්", "උෟ"],
    # Supplementary-plane scripts through the universal engine: Adlam (right to left, cursive
    # joining, combining marks), Chakma (virama, two-part vowel signs that decompose), Khojki
    # (the forbidden vowel sequences of an astral script).
    "Adlam": ["𞤀𞤣𞤤𞤢𞤥", "𞤆𞤵𞤤𞤢𞤪", "𞤬𞤵𞤤𞤬𞤵𞤤𞤣𞤫", "𞤅𞤫𞤲𞤫𞤺𞤢𞤤", "𞤃𞤢𞤤𞤭", "𞤢𞤤𞤴𞤢𞤦𞤢", "𞤁𞤫𞤱", "𞤶𞤢𞤲𞤺𞤮", "𞤳𞤢𞤲", "𞤢𞥄", "𞤢𞥅𞤪𞤫", "𞤫𞥆𞤫", "𞤢𞤣𞤵𞥅𞤯𞤫", "𞤴𞤫𞥆𞤴𞤫", "𞤀𞤤𞤤𞤢", "𞤶𞤢𞤥𞤢𞥄", "𞥑𞥒𞥓", "𞤢𞥇", "𞤦𞥈", "𞤦𞥉", "𞤣𞥊", "𞤀𞤁𞤂𞤃", "𞤪𞤢𞤱𞤲𞤣𞤫", "𞤲𞤺𞤢𞤤"],
    "Chakma": ["𑄌𑄋𑄴𑄟𑄳𑄦", "𑄇𑄨", "𑄇𑄩", "𑄇𑄪", "𑄇𑄫", "𑄇𑄬", "𑄇𑄮", "𑄇𑄯", "𑄇𑄳𑄠", "𑄇𑄳𑄢", "𑄇𑄴", "𑄇𑄁", "𑄇𑄂", "𑄇𑄃", "𑄃𑄨𑄉𑄦𑄴", "𑄟𑄧𑄚𑄴", "𑄝𑄧𑄢𑄴", "𑄠𑄪𑄉𑄴", "𑄖𑄧𑄁", "𑄚𑄳𑄠𑄌𑄴", "𑄇𑄳𑄡", "𑄖𑄳𑄢", "𑄥𑄳𑄢", "𑄦𑄳𑄘", "𑄎𑄪𑄟𑄴", "𑄇𑄭", "𑄇𑄰", "𑄇𑄨𑄁", "𑄇𑄮𑄴"],
    "Khojki": ["𑈀𑈬", "𑈀𑈱", "𑈬𑈰", "𑈀𑈳", "𑈈𑈵𑈈", "𑈈𑈬", "𑈈𑈭", "𑈈𑈮", "𑈈𑈯", "𑈈𑈰", "𑈈𑈱", "𑈈𑈲", "𑈈𑈳", "𑈈𑈴", "𑈈𑈵", "𑈈𑈶", "𑈈𑈷", "𑈈𑈸", "𑈉𑈵𑈦", "𑈐𑈵𑈐", "𑈁𑈬𑈧𑈵𑈇", "𑈕𑈵𑈚𑈬", "𑈥𑈵𑈧𑈮"],
}
# The old-specification fonts: the script's whole list plus the sequences the old rules
# treat differently — a Halant after the base moved behind the last consonant, an eyelash Ra
# (Ra + Halant before the base) formed through 'blwf' unless a ZWJ asks for it explicitly,
# Kannada's double halants.
WORDS["Devanagari-oldspec"] = WORDS["Devanagari"] + ["त्र्क", "त्र्‍क", "ट्र्", "ट्र्क", "र्क्", "क्र्", "स्र्", "कर्त्र्य", "र्‍क", "प्र्", "क्र्क"]
WORDS["Bengali-oldspec"] = WORDS["Bengali"] + ["ক্র্", "র্ক্", "ঘ্য্", "ন্ত্র্"]
WORDS["Kannada-oldspec"] = WORDS["Kannada"] + ["ಕ್ಕ್", "ಕ್ಷ್", "ಕ್ರ್", "ರ್ಕ್", "ಕ್ಕ್ಕ"]
WORDS["Malayalam-oldspec"] = WORDS["Malayalam"] + ["ക്ര്", "ന്ത്", "ര്യ്", "ക്ഷ്"]


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


def ours(path, words, rtl):
    res = subprocess.run(["dotnet", DLL, "shape", path] + words, capture_output=True, encoding="utf-8")
    if res.returncode != 0:
        print(res.stderr[:2000])
        return {}
    out = {}
    for line in res.stdout.splitlines():
        if "\t" not in line:
            continue
        w, rest = line.split("\t", 1)
        parts = [t.split("@")[0] + t[t.index("("):] for t in rest.split()] if rest else []
        if rtl:
            parts.reverse()
        out[w] = " ".join(parts)
    return out


def derive():
    from fontTools import subset
    from fontTools.ttLib import TTFont
    from fontTools.varLib.instancer import instantiateVariableFont

    for target, (source, drop, rename, keep) in OLD_SPEC.items():
        font = TTFont(os.path.join(FONTS, source))
        if "fvar" in font:
            font = instantiateVariableFont(font, {a.axisTag: a.defaultValue for a in font["fvar"].axes})
        options = subset.Options(layout_features=["*"], hinting=False, notdef_outline=True, name_IDs=["*"])
        subsetter = subset.Subsetter(options)
        unicodes = [cp for a, b in keep + COMMON_KEEP for cp in range(a, b + 1)]
        subsetter.populate(unicodes=unicodes)
        subsetter.subset(font)
        for table in ("GSUB", "GPOS"):
            if table not in font:
                continue
            script_list = font[table].table.ScriptList
            # ⚠ str() everywhere: a fontTools Tag compared with None answers NotImplemented,
            # which `!=` reads as False — a bare `r.ScriptTag != drop` emptied the list.
            records = [r for r in script_list.ScriptRecord if str(r.ScriptTag) != drop]
            present = {str(r.ScriptTag) for r in records}
            renamed = []
            for r in records:
                new = rename.get(str(r.ScriptTag))
                if new is None:
                    renamed.append(r)
                elif new not in present:
                    r.ScriptTag = new
                    renamed.append(r)
                # A record whose new tag the table already carries (Noto's GPOS has both
                # beng and bng2) is dropped: the old-tag one is what HarfBuzz would pick.
            script_list.ScriptRecord = renamed
            script_list.ScriptCount = len(renamed)
            tags = [str(r.ScriptTag) for r in renamed]
            assert tags == sorted(tags), (target, tags)
        # A derived font is not the original: say so in its names.
        for rec in font["name"].names:
            if rec.nameID in (1, 3, 4, 6):
                rec.string = rec.toUnicode().replace("Noto Sans", "Noto Sans OldSpec").replace("NotoSans", "NotoSansOldSpec")
        font.save(os.path.join(FONTS, target))
        tags = [r.ScriptTag for r in font["GSUB"].table.ScriptList.ScriptRecord]
        print(f"{target}: from {source}, {len(font.getGlyphOrder())} glyphs, GSUB scripts {tags}, {os.path.getsize(os.path.join(FONTS, target))} bytes")


def header(name):
    return (f"# font={os.path.basename(font_for(name))} rtl={1 if name in RTL else 0} HarfBuzz {hb.version_string()} — "
            f"glyph(advance,xOffset,yOffset) in font units. Generated by tools/shaping-oracle/harfbuzz-expectations.py; do not edit.\n")


def write():
    os.makedirs(OUT, exist_ok=True)
    for name, words in WORDS.items():
        path = font_for(name)
        if not os.path.exists(path):
            print(f"{name}: no font at {path}")
            continue
        expected = harfbuzz(path, words)
        with io.open(os.path.join(OUT, f"{name}.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write(header(name))
            for w in words:
                f.write(f"{w}\t{expected[w]}\n")
        print(f"{name}: {len(words)} words")


def compare(path, names):
    total = bad = 0
    for name in names:
        words = WORDS[name]
        h = harfbuzz(path, words)
        o = ours(path, words, name in RTL)
        diffs = [(w, h[w], o.get(w, "<missing>")) for w in words if h[w] != o.get(w)]
        total += len(words)
        bad += len(diffs)
        print(f"== {name}: {len(words) - len(diffs)}/{len(words)} agree")
        for w, hh, oo in diffs:
            print(f"   {w}\t{' '.join('%04X' % ord(c) for c in w)}\n      hb : {hh}\n      us : {oo}")
    print(f"\nTOTAL {total - bad}/{total} agree")


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "derive":
        derive()
    elif len(sys.argv) >= 2 and sys.argv[1] == "write":
        write()
    elif len(sys.argv) >= 3 and sys.argv[1] == "compare":
        compare(sys.argv[2], sys.argv[3:] or list(WORDS))
    else:
        print(__doc__)
        sys.exit(2)
