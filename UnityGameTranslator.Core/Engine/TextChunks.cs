using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A long text cut into pieces a label can draw.
    ///
    /// 🔴 A uGUI label draws four vertices per character into one mesh, and a canvas mesh may
    /// hold 65 000 of them: past that the mesh is refused and the label draws NOTHING, while
    /// keeping the height its text asks for — a scroll area with a thumb and a blank trough
    /// (2026-09-17, a proposal of 25 000 characters). The limit is the mesh's, so a text is
    /// shown as several labels, each under it, cut at a line break wherever there is one.
    /// Pure: no Unity — the Core.Checks hold the cut.
    /// </summary>
    public static class TextChunks
    {
        /// <summary>What a canvas mesh holds, and what one drawn character costs.</summary>
        public const int CanvasMeshVertices = 65000;
        public const int VerticesPerCharacter = 4;

        /// <summary>The most characters one label can draw.</summary>
        public const int LabelCharacters = CanvasMeshVertices / VerticesPerCharacter;

        /// <summary>
        /// The text in pieces of at most <paramref name="most"/> characters, cut at the last line
        /// break before the limit when there is one, in the middle of a line otherwise. An empty
        /// text is one empty piece, so a caller always has a row to write into.
        /// </summary>
        public static List<string> Split(string text, int most = LabelCharacters)
        {
            if (most <= 0) throw new ArgumentOutOfRangeException(nameof(most));
            var pieces = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                pieces.Add("");
                return pieces;
            }

            int at = 0;
            while (at < text.Length)
            {
                int left = text.Length - at;
                if (left <= most)
                {
                    pieces.Add(text.Substring(at));
                    break;
                }

                // Cut after the last line break that keeps the piece under the limit; the break
                // itself ends the piece, so the next one starts on a fresh line.
                int cut = text.LastIndexOf('\n', at + most - 1, most);
                int length = cut >= at ? cut - at + 1 : most;
                pieces.Add(text.Substring(at, length));
                at += length;
            }
            return pieces;
        }
    }
}
