using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The font files being converted right now (a .ttf/.otf drawn into an atlas), and how far each
    /// one has got — what the corner notification shows while the game keeps running.
    ///
    /// 🔴 Why (user, 2026-09-28): a conversion ran on the game's thread and froze it for as long as it
    /// took — a CJK font is some thirty thousand letters — with nothing on screen to say why. It now
    /// runs in the background (CustomFontLoader.LoadCustomFont); this is what it reports, written by
    /// the worker, read by the interface.
    ///
    /// ⚠ Pure: no Unity. The counters are written from the worker thread, hence Interlocked/volatile.
    /// </summary>
    public static class FontConversions
    {
        /// <summary>One conversion under way.</summary>
        public sealed class Conversion
        {
            public string FontName { get; }
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private int _done, _total;
            private volatile string _step = "Reading";

            internal Conversion(string fontName) { FontName = fontName; }

            /// <summary>Letters drawn so far, and how many there are.</summary>
            public int Done => Volatile.Read(ref _done);
            public int Total => Volatile.Read(ref _total);

            /// <summary>What it is doing: reading, drawing letters, packing, saving.</summary>
            public string Step => _step;

            public double Seconds => _clock.Elapsed.TotalSeconds;

            public void SetTotal(int total) => Volatile.Write(ref _total, total);
            public void SetDone(int done) => Volatile.Write(ref _done, done);
            public void SetStep(string step) => _step = step;
        }

        private static readonly List<Conversion> Running = new List<Conversion>();
        private static readonly object Gate = new object();

        public static Conversion Start(string fontName)
        {
            var conversion = new Conversion(fontName);
            lock (Gate) Running.Add(conversion);
            return conversion;
        }

        public static void Finish(Conversion conversion)
        {
            lock (Gate) Running.Remove(conversion);
        }

        /// <summary>The conversions under way, oldest first — a copy, safe to read on any thread.</summary>
        public static List<Conversion> Current()
        {
            lock (Gate) return new List<Conversion>(Running);
        }
    }
}
