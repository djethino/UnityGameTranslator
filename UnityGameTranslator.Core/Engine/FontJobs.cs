using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The font work running in the background right now — a font file converted into an atlas, the
    /// game's own fonts read from its data files — and how far each has got: what the corner
    /// notification shows while the game keeps running.
    ///
    /// 🔴 Why (user, 2026-09-28): a conversion ran on the game's thread and froze it for as long as it
    /// took — a CJK font is some thirty thousand letters — with nothing on screen to say why. It now
    /// runs in the background (CustomFontLoader.LoadCustomFont); reading the game's fonts does too
    /// (GameFontFiles). This is what they report, written by the worker, read by the interface.
    ///
    /// ⚠ Pure: no Unity. The counters are written from the worker thread, hence Interlocked/volatile.
    /// </summary>
    public static class FontJobs
    {
        /// <summary>One piece of work under way.</summary>
        public sealed class Job
        {
            /// <summary>What the notification says it is ("Converting font: Noto Sans").</summary>
            public string Title { get; }
            /// <summary>What <see cref="Done"/> counts, in the plural ("characters", "files").</summary>
            public string Unit { get; }
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private int _done, _total;
            private volatile string _step = "Reading";

            internal Job(string title, string unit) { Title = title; Unit = unit; }

            /// <summary>Units done so far, and how many there are.</summary>
            public int Done => Volatile.Read(ref _done);
            public int Total => Volatile.Read(ref _total);

            /// <summary>What it is doing: reading, drawing letters, packing, saving.</summary>
            public string Step => _step;

            public double Seconds => _clock.Elapsed.TotalSeconds;

            public void SetTotal(int total) => Volatile.Write(ref _total, total);
            public void SetDone(int done) => Volatile.Write(ref _done, done);
            public void SetStep(string step) => _step = step;
        }

        private static readonly List<Job> Running = new List<Job>();
        private static readonly object Gate = new object();

        public static Job Start(string title, string unit)
        {
            var job = new Job(title, unit);
            lock (Gate) Running.Add(job);
            return job;
        }

        public static void Finish(Job job)
        {
            lock (Gate) Running.Remove(job);
        }

        /// <summary>The work under way, oldest first — a copy, safe to read on any thread.</summary>
        public static List<Job> Current()
        {
            lock (Gate) return new List<Job>(Running);
        }
    }
}
