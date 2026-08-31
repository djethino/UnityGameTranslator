// RichTextKit
// Copyright © 2019-2020 Topten Software. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may
// not use this product except in compliance with the License. You may obtain
// a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
// License for the specific language governing permissions and limitations
// under the License.
//
// [UGT local change — see ../VENDORED.md] Trimmed to the bidi trie alone: the original loaded
// four tries (line break, word boundary, grapheme clusters) that belong to subjects this
// vendored set does not carry, and anchored the resource lookup on the LineBreaker type, which
// is not part of the set either. The resource keeps its ORIGINAL logical name, so this file's
// loading line is the upstream one with only the anchor type changed.

namespace Topten.RichTextKit
{
    /// <summary>
    /// Helper for looking up unicode character class information
    /// </summary>
    internal static class UnicodeClasses
    {
        static UnicodeClasses()
        {
            // Load trie resources
            _bidiTrie = new UnicodeTrie(typeof(UnicodeClasses).Assembly.GetManifestResourceStream("Topten.RichTextKit.Resources.BidiClasses.trie"));
        }

        static UnicodeTrie _bidiTrie;

        /// <summary>
        /// Get the directionality of a Unicode Code Point
        /// </summary>
        /// <param name="codePoint">The code point in question</param>
        /// <returns>The code point's directionality</returns>
        public static Directionality Directionality(int codePoint)
        {
            return (Directionality)(_bidiTrie.Get(codePoint) >> 24);
        }

        /// <summary>
        /// Get the directionality of a Unicode Code Point
        /// </summary>
        /// <param name="codePoint">The code point in question</param>
        /// <returns>The code point's directionality</returns>
        public static uint BidiData(int codePoint)
        {
            return _bidiTrie.Get(codePoint);
        }

        /// <summary>
        /// Get the bracket type for a Unicode Code Point
        /// </summary>
        /// <param name="codePoint">The code point in question</param>
        /// <returns>The code point's paired bracked type</returns>
        public static PairedBracketType PairedBracketType(int codePoint)
        {
            return (PairedBracketType)((_bidiTrie.Get(codePoint) >> 16) & 0xFF);
        }

        /// <summary>
        /// Get the associated bracket type for a Unicode Code Point
        /// </summary>
        /// <param name="codePoint">The code point in question</param>
        /// <returns>The code point's opposite bracket, or 0 if not a bracket</returns>
        public static int AssociatedBracket(int codePoint)
        {
            return (int)(_bidiTrie.Get(codePoint) & 0xFFFF);
        }
    }
}
