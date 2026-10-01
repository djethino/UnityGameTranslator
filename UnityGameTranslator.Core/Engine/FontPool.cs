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
    /// legacy text during the session needs a name that was listed at start and never opened.
    ///
    /// VIRTUAL (user, 2026-10-01: « on ne peut pas créer 1000 fichiers par jeu chez les utilisateurs »):
    /// the names exist only in the engine's listing (IVirtualFonts, FontFolderRedirect). One real file,
    /// the TEMPLATE — an empty font — stands behind every empty slot: the engine opens it, and what it
    /// reads is the empty font of THAT slot (<see cref="ContentOf"/>), same length since every family
    /// name has the same width ("UGT Pool 0001"). A filled slot opens the file it was given
    /// (<see cref="Fill"/>). Proven on the probe bench that an empty font listed at start and filled
    /// later is drawn (analyse/ecritures-complexes-etat-reel.md, the pool).
    ///
    /// Size: <see cref="FirstSize"/> (the user's figure), doubled at the next launch when a session
    /// used more than half — read from one line kept next to the template (<see cref="StateFile"/>).
    ///
    /// Thread-safe: the engine asks from its own threads. PURE by contract (no Unity) — linked into
    /// Core.Checks.
    /// </summary>
    internal sealed class FontPool : IVirtualFonts
    {
        /// <summary>
        /// The pool's size the first time (user, 2026-10-01: « j'imagine mal un jeu avec plus de 100
        /// fonts ; pour l'hindi, 1 font = 30 » — 3000 for about a hundred).
        /// </summary>
        internal const int FirstSize = 3000;

        internal const string FilePrefix = "ugt-pool-";
        internal const string FamilyPrefix = "UGT Pool ";
        internal const string TemplateFile = "pool-template.ttf";
        internal const string StateFile = "pool.state";

        internal struct Slot
        {
            public string Name;     // the file name the engine lists
            public string Family;   // the family the engine knows it by
        }

        private readonly object _gate = new object();
        private readonly string _statePath;
        private readonly int _width;
        private readonly string[] _filled;   // index k-1: the real file slot k opens, null while empty
        private int _next = 1;

        internal int Size { get; }
        /// <summary>Slots used in the previous session, as it recorded them.</summary>
        internal int UsedLastSession { get; }
        internal int Taken { get { lock (_gate) return _next - 1; } }
        internal bool Spent { get { lock (_gate) return _next > Size; } }

        /// <summary>The real file behind every empty slot.</summary>
        internal string TemplatePath { get; }
        internal long TemplateLength { get; }

        internal string NameOf(int k) => FilePrefix + k.ToString("D" + _width) + ".ttf";
        internal string FamilyOf(int k) => FamilyPrefix + k.ToString("D" + _width);

        /// <summary>
        /// The pool for this launch, in <paramref name="folder"/>: its size from what the previous
        /// session recorded, the template written when missing or of another width. Throws on a file
        /// error: the caller says it and shows no pool.
        /// </summary>
        internal FontPool(string folder)
        {
            Directory.CreateDirectory(folder);
            _statePath = Path.Combine(folder, StateFile);

            int previous = 0, used = 0;
            if (File.Exists(_statePath))
            {
                var parts = File.ReadAllText(_statePath).Split(' ');
                if (parts.Length != 2 || !int.TryParse(parts[0], out previous) || !int.TryParse(parts[1], out used))
                    throw new InvalidDataException($"{StateFile} is not '<size> <used>'");
            }
            UsedLastSession = used;
            int size = Math.Max(FirstSize, previous);
            if (previous > 0 && used * 2 > previous) size = Math.Max(size, previous * 2);
            Size = size;
            _width = size.ToString().Length;
            _filled = new string[size];

            TemplatePath = Path.Combine(folder, TemplateFile);
            byte[] template = DerivedFontWriter.Placeholder(FamilyOf(0));
            TemplateLength = template.Length;
            if (!File.Exists(TemplatePath) || new FileInfo(TemplatePath).Length != template.Length)
                File.WriteAllBytes(TemplatePath, template);
            WriteState(0);
        }

        private void WriteState(int used) => File.WriteAllText(_statePath, Size + " " + used);

        /// <summary>
        /// A name never opened in this session, or null when the pool is spent (it grows at the next
        /// launch). The caller writes its font somewhere and gives it to the slot (<see cref="Fill"/>).
        /// </summary>
        internal Slot? Take()
        {
            int k;
            lock (_gate)
            {
                if (_next > Size) return null;
                k = _next++;
            }
            // Recorded as it happens: a session that ends by a crash still counts.
            WriteState(k);
            return new Slot { Name = NameOf(k), Family = FamilyOf(k) };
        }

        /// <summary>From now on the engine opening this slot opens <paramref name="realFile"/>.</summary>
        internal void Fill(Slot slot, string realFile)
        {
            int k = IndexOf(slot.Name);
            if (k <= 0) throw new ArgumentException($"{slot.Name} is not a slot of this pool");
            lock (_gate) _filled[k - 1] = realFile;
        }

        private int IndexOf(string name)
        {
            if (name == null || name.Length != NameOf(1).Length
                || !name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) return -1;
            return int.TryParse(name.Substring(FilePrefix.Length, _width), out int k) && k >= 1 && k <= Size ? k : -1;
        }

        // ── What the engine is shown (FontFolderRedirect) ──────────────────────────────────────

        public IEnumerable<string> Names()
        {
            for (int k = 1; k <= Size; k++) yield return NameOf(k);
        }

        public bool Has(string name) => IndexOf(name) > 0;

        public long ListedLength(string name) => TemplateLength;

        public string PathOf(string name)
        {
            int k = IndexOf(name);
            if (k <= 0) return null;
            lock (_gate) return _filled[k - 1] ?? TemplatePath;
        }

        public byte[] ContentOf(string name)
        {
            int k = IndexOf(name);
            if (k <= 0) return null;
            lock (_gate) if (_filled[k - 1] != null) return null;
            return DerivedFontWriter.Placeholder(FamilyOf(k));
        }
    }
}
