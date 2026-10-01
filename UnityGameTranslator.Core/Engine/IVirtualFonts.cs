using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Font files the engine is shown that do not exist as such (FontPool): FontFolderRedirect lists
    /// their names, opens <see cref="PathOf"/> when the engine opens one, and — when
    /// <see cref="ContentOf"/> gives bytes — makes every read of that file return those bytes instead
    /// (same length as the file, so sizes and seeks stay the file's own).
    ///
    /// Called from the engine's threads. PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal interface IVirtualFonts
    {
        /// <summary>Every name to list, in order.</summary>
        IEnumerable<string> Names();

        bool Has(string name);

        /// <summary>The size the listing says.</summary>
        long ListedLength(string name);

        /// <summary>The real file opened for this name; null when the name is not one of these.</summary>
        string PathOf(string name);

        /// <summary>The bytes read instead of <see cref="PathOf"/>'s own, or null to read the file as it is.</summary>
        byte[] ContentOf(string name);
    }
}
