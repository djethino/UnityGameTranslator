using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// Tells a tremble from a movement: a value that goes A, B, A, B… one frame after the other.
    ///
    /// 🔴 **Why it exists** (2026-09-30): a window's whole content was seen alternating between two
    /// layouts every frame — « comme si ça se dédoublait » — on a game nobody could name afterwards,
    /// and it never came back on demand. Reading the code gave three suspects and no proof. This
    /// is what makes the next occurrence say which value fights which, wherever it happens.
    ///
    /// ⚠ **A movement is not a tremble.** Opening grows the scale one way, a scroll moves the
    /// content one way, a drag follows the hand: each frame differs from the last, none comes back
    /// to the one before. Only a return to the value of two frames ago, several times running,
    /// counts — and several, because a single back-and-forth is somebody scrolling up again.
    ///
    /// ⚠ **Reported once per episode, re-armed by the value settling** — an event, never a delay:
    /// a tremble that lasts a minute is one line in the log, and the next one is a line of its own.
    /// </summary>
    public sealed class Oscillation
    {
        /// <summary>Returns in a row that make a tremble: past any single gesture going back.</summary>
        public const int Frames = 6;

        /// <summary>Below half a pixel, two layouts are the same layout.</summary>
        public const float Tolerance = 0.5f;

        /// <summary>One value caught trembling: its name and the two values it goes between.</summary>
        public struct Tremble
        {
            public string Channel;
            public float A;
            public float B;
        }

        private readonly string[] _channels;
        private readonly float[] _previous;
        private readonly float[] _beforePrevious;
        private readonly int[] _returns;
        private readonly bool[] _reported;
        private int _seen;

        /// <param name="channels">What each position of the values fed means, for the log line.</param>
        public Oscillation(params string[] channels)
        {
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _previous = new float[channels.Length];
            _beforePrevious = new float[channels.Length];
            _returns = new int[channels.Length];
            _reported = new bool[channels.Length];
        }

        /// <summary>
        /// One frame's values, in the order of the channels. Returns the values that have just
        /// become a tremble — empty on almost every frame, and never the same episode twice.
        /// </summary>
        public IReadOnlyList<Tremble> Feed(float[] values)
        {
            if (values == null || values.Length != _channels.Length)
                throw new ArgumentException("one value per channel", nameof(values));

            List<Tremble> found = null;

            for (int i = 0; i < values.Length; i++)
            {
                float now = values[i];

                if (_seen >= 2)
                {
                    bool moved = Math.Abs(now - _previous[i]) > Tolerance;
                    bool cameBack = Math.Abs(now - _beforePrevious[i]) <= Tolerance;

                    if (!moved)
                    {
                        // Settled: the episode, if any, is over — the next one is news again.
                        _returns[i] = 0;
                        _reported[i] = false;
                    }
                    else if (cameBack)
                    {
                        if (++_returns[i] >= Frames && !_reported[i])
                        {
                            _reported[i] = true;
                            if (found == null) found = new List<Tremble>();
                            found.Add(new Tremble { Channel = _channels[i], A = _previous[i], B = now });
                        }
                    }
                    else
                    {
                        // Moving on, not back: a movement, whatever came before.
                        _returns[i] = 0;
                    }
                }

                _beforePrevious[i] = _previous[i];
                _previous[i] = now;
            }

            if (_seen < 2) _seen++;
            return (IReadOnlyList<Tremble>)found ?? None;
        }

        /// <summary>Forgets everything — a window shown again starts from nothing.</summary>
        public void Reset()
        {
            _seen = 0;
            Array.Clear(_returns, 0, _returns.Length);
            Array.Clear(_reported, 0, _reported.Length);
        }

        private static readonly Tremble[] None = new Tremble[0];
    }
}
