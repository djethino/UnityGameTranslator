using System;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The family and style a font is known by to the system (TtfParser: name ids 16/17, else 1/2) —
    /// what an engine's "create a font asset from a family name" is given. The full name (id 4) is no
    /// family: "Adobe Arabic Regular" was asked for as a family, nothing was found, and UI Toolkit drew
    /// nothing (bench, 2026-10-03). Expected names read off the same file by its name table.
    /// </summary>
    internal static class FontNamesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", "NotoSansDevanagari.ttf");
            if (!File.Exists(path)) { check(false, "NotoSansDevanagari.ttf present", path); return; }
            var m = new TtfParser(File.ReadAllBytes(path)).Metrics;
            check(m.Family == "Noto Sans Devanagari" && m.Style == "Regular", "the family and the style, apart",
                  $"family '{m.Family}', style '{m.Style}'");
            check(m.FontName == "Noto Sans Devanagari Regular" && m.FontName != m.Family, "the full name stays the full name, never the family",
                  m.FontName);
            check(!m.Names.Contains("Regular"), "a style is no name the font is known by", string.Join(", ", m.Names));
        }
    }
}
