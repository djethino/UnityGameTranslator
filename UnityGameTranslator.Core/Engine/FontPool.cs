using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Font NAMES the engine lists at start and the mod fills later — so what used to wait for the
    /// next launch happens in the session (user, 2026-10-01: « une sorte de pool […] pour l'utilisateur
    /// ça doit être transparent »). The engine lists its font folder once (FontFolderRedirect) and, on
    /// the IL2CPP path, keeps in memory a family it has opened (probe 2 N_B): a font that must reach
    /// legacy text during the session needs a name that was listed at start and never opened. Each
    /// slot is an empty font of family "UGT Pool k" (DerivedFontWriter.Placeholder) until
    /// <see cref="Take"/> hands it out; the caller writes the real font into its file, under that family.
    ///
    /// Proven on the probe bench (analyse/ecritures-complexes-etat-reel.md, the pool): an empty font
    /// listed at start, written later, is drawn — Unity 2018.4, 2021.3, 6000.6, Mono and IL2CPP paths.
    /// Listing 1000 of them costs about 0.15 s at start.
    ///
    /// Size: <see cref="FirstSize"/> the first time (the user's figure), and doubled at the next launch
    /// when a session used more than half — a long session is followed by a larger pool, never a
    /// fixed ceiling. A slot used in the previous session is emptied again at start.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal sealed class FontPool
    {
        /// <summary>The pool's size the first time (user, 2026-10-01).</summary>
        internal const int FirstSize = 1000;

        internal const string FilePrefix = "ugt-pool-";
        internal const string FamilyPrefix = "UGT Pool ";

        internal struct Slot
        {
            public string File;
            public string Family;
        }

        private int _next = 1;

        /// <summary>Every slot's file, to show the engine (FontFolderRedirect.Install).</summary>
        internal readonly List<string> Files = new List<string>();
        internal int Size => Files.Count;
        /// <summary>Slots used in the previous session (their files held a real font).</summary>
        internal int UsedLastSession { get; }
        internal int Taken => _next - 1;
        internal bool Spent => _next > Size;

        internal static string FileOf(string folder, int k) => Path.Combine(folder, FilePrefix + k + ".ttf");
        internal static string FamilyOf(int k) => FamilyPrefix + k;

        /// <summary>
        /// Writes the pool in <paramref name="folder"/>: every slot an empty font, the ones filled last
        /// session emptied again, grown when that session used more than half. Throws on a file error:
        /// the caller says it and shows no pool.
        /// </summary>
        internal FontPool(string folder)
        {
            Directory.CreateDirectory(folder);

            // One listing of the folder gives every slot's length (a file asked one by one costs ten times more).
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in new DirectoryInfo(folder).GetFiles(FilePrefix + "*.ttf"))
                lengths[info.Name] = info.Length;

            var filled = new List<bool>();   // index k-1: slot k held a real font
            while (lengths.TryGetValue(Path.GetFileName(FileOf(folder, filled.Count + 1)), out long length))
                filled.Add(!IsEmpty(length, FamilyOf(filled.Count + 1)));
            int previous = filled.Count;
            UsedLastSession = filled.FindAll(f => f).Count;

            int size = Math.Max(FirstSize, previous);
            if (UsedLastSession * 2 > previous && previous > 0) size = Math.Max(size, previous * 2);

            for (int k = 1; k <= size; k++)
            {
                string file = FileOf(folder, k);
                if (k > previous || filled[k - 1])
                    File.WriteAllBytes(file, DerivedFontWriter.Placeholder(FamilyOf(k)));
                Files.Add(file);
            }
        }

        // An empty slot's length depends only on the length of its family's name.
        private readonly Dictionary<int, long> _emptyLength = new Dictionary<int, long>();

        /// <summary>
        /// Whether a slot is still empty — by its length alone: a font written into a slot is a real
        /// font, thousands of bytes, and a slot taken for empty by mistake would only stay what it is,
        /// a valid font of the slot's own family. Reading 1000 files at every launch is what this spares.
        /// </summary>
        private bool IsEmpty(long length, string family)
        {
            if (!_emptyLength.TryGetValue(family.Length, out long expected))
                _emptyLength[family.Length] = expected = DerivedFontWriter.Placeholder(family).Length;
            return length == expected;
        }

        /// <summary>
        /// A name never opened in this session, or null when the pool is spent (its size grows at the
        /// next launch). The caller writes its font into the slot's file under the slot's family.
        /// </summary>
        internal Slot? Take()
        {
            if (Spent) return null;
            int k = _next++;
            return new Slot { File = Files[k - 1], Family = FamilyOf(k) };
        }
    }
}
