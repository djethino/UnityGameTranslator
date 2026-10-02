using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Topten.RichTextKit;

// The bidi data of every code point, from Unicode's own files, in RichTextKit's trie: value =
// bidi class << 24 | paired-bracket type << 16 | paired bracket (UnicodeClasses reads it so).
//
// ⚠ PINNED to the version of the mod's other Unicode tables (tools/generate-shaping-tables.py,
// tools/generate-indic-tables.py). The vendored trie came from RichTextKit's own generator, of a
// Unicode nobody had written down: one version for the whole mod means regenerating it here.
// Also refreshes the conformance files the checks replay the bidi algorithm against.
internal static class Program
{
    private const string Ucd = "https://www.unicode.org/Public/18.0.0/ucd/";

    private static int Main()
    {
        string here = AppContext.BaseDirectory;
        string core = Find(here, "UnityGameTranslator.Core");
        string checks = Find(here, Path.Combine("tests", "UnityGameTranslator.Core.Checks"));
        if (core == null || checks == null) { Console.Error.WriteLine("the Core or its checks are not above this tool"); return 1; }
        string triePath = Path.Combine(core, "TextShaping", "RichTextKit", "Resources", "BidiClasses.trie");

        using var http = new HttpClient();
        string Fetch(string name) => http.GetStringAsync(Ucd + name).GetAwaiter().GetResult();

        // 1. Bidi classes: every code point, defaults first (DerivedBidiClass's @missing lines,
        //    in order, the later ones narrower), then the explicit assignments.
        var classes = new byte[0x110000];
        string derived = Fetch("extracted/DerivedBidiClass.txt");
        foreach (var line in derived.Split('\n'))
        {
            const string missing = "# @missing:";
            if (!line.StartsWith(missing)) continue;
            var parts = line.Substring(missing.Length).Split(';');
            Fill(classes, parts[0].Trim(), ClassOf(parts[1].Trim()));
        }
        foreach (var line in derived.Split('\n'))
        {
            string body = line.Split('#')[0].Trim();
            if (body.Length == 0) continue;
            var parts = body.Split(';');
            Fill(classes, parts[0].Trim(), ClassOf(parts[1].Trim()));
        }

        // 2. Paired brackets.
        var bracketType = new byte[0x110000];
        var bracket = new int[0x110000];
        foreach (var line in Fetch("BidiBrackets.txt").Split('\n'))
        {
            string body = line.Split('#')[0].Trim();
            if (body.Length == 0) continue;
            var parts = body.Split(';');
            int cp = Convert.ToInt32(parts[0].Trim(), 16);
            bracket[cp] = Convert.ToInt32(parts[1].Trim(), 16);
            bracketType[cp] = (byte)(parts[2].Trim() == "o" ? PairedBracketType.o : PairedBracketType.c);
        }

        // 3. The trie, and what changed against the one it replaces.
        var builder = new UnicodeTrieBuilder();
        for (int cp = 0; cp < 0x110000; cp++)
        {
            uint value = (uint)classes[cp] << 24 | (uint)bracketType[cp] << 16 | (uint)(bracket[cp] & 0xFFFF);
            if (value != 0) builder.Set(cp, value);
        }
        var trie = builder.Freeze();
        UnicodeTrie old = File.Exists(triePath) ? new UnicodeTrie(File.ReadAllBytes(triePath)) : null;
        int changed = 0;
        var sample = new List<string>();
        for (int cp = 0; cp < 0x110000 && old != null; cp++)
        {
            uint was = old.Get(cp), now = trie.Get(cp);
            if (was == now) continue;
            changed++;
            if (sample.Count < 12) sample.Add($"U+{cp:X4} {(Directionality)(was >> 24)}->{(Directionality)(now >> 24)}");
        }
        using (var file = File.Create(triePath)) trie.Save(file);
        Console.WriteLine($"{triePath}: written from Unicode {Ucd}; {changed} code point(s) differ from the trie replaced"
                          + (sample.Count > 0 ? ": " + string.Join(", ", sample) : ""));

        // 4. The conformance files the checks replay (gzipped, as the checks read them).
        foreach (var source in new[] { "BidiTest.txt", "BidiCharacterTest.txt", "auxiliary/LineBreakTest.txt" })
        {
            string name = Path.GetFileName(source);
            string target = Path.Combine(checks, "TestData", name + ".gz");
            using var output = File.Create(target);
            using var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal);
            var bytes = http.GetByteArrayAsync(Ucd + source).GetAwaiter().GetResult();
            gzip.Write(bytes, 0, bytes.Length);
            Console.WriteLine($"{target}: {bytes.Length} bytes of Unicode's {name}");
        }
        return 0;
    }

    private static void Fill(byte[] classes, string range, byte value)
    {
        var ends = range.Split("..");
        int first = Convert.ToInt32(ends[0], 16), last = ends.Length > 1 ? Convert.ToInt32(ends[1], 16) : first;
        for (int cp = first; cp <= last; cp++) classes[cp] = value;
    }

    // Unicode's long and short names of a bidi class, to RichTextKit's numbering.
    private static byte ClassOf(string name)
    {
        switch (name)
        {
            case "L": case "Left_To_Right": return (byte)Directionality.L;
            case "R": case "Right_To_Left": return (byte)Directionality.R;
            case "AL": case "Arabic_Letter": return (byte)Directionality.AL;
            case "EN": case "European_Number": return (byte)Directionality.EN;
            case "ES": case "European_Separator": return (byte)Directionality.ES;
            case "ET": case "European_Terminator": return (byte)Directionality.ET;
            case "AN": case "Arabic_Number": return (byte)Directionality.AN;
            case "CS": case "Common_Separator": return (byte)Directionality.CS;
            case "NSM": case "Nonspacing_Mark": return (byte)Directionality.NSM;
            case "BN": case "Boundary_Neutral": return (byte)Directionality.BN;
            case "B": case "Paragraph_Separator": return (byte)Directionality.B;
            case "S": case "Segment_Separator": return (byte)Directionality.S;
            case "WS": case "White_Space": return (byte)Directionality.WS;
            case "ON": case "Other_Neutral": return (byte)Directionality.ON;
            case "LRE": case "Left_To_Right_Embedding": return (byte)Directionality.LRE;
            case "LRO": case "Left_To_Right_Override": return (byte)Directionality.LRO;
            case "RLE": case "Right_To_Left_Embedding": return (byte)Directionality.RLE;
            case "RLO": case "Right_To_Left_Override": return (byte)Directionality.RLO;
            case "PDF": case "Pop_Directional_Format": return (byte)Directionality.PDF;
            case "LRI": case "Left_To_Right_Isolate": return (byte)Directionality.LRI;
            case "RLI": case "Right_To_Left_Isolate": return (byte)Directionality.RLI;
            case "FSI": case "First_Strong_Isolate": return (byte)Directionality.FSI;
            case "PDI": case "Pop_Directional_Isolate": return (byte)Directionality.PDI;
            default: throw new InvalidDataException("unknown bidi class " + name);
        }
    }

    private static string Find(string from, string relative)
    {
        for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "UnityGameTranslator", relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
