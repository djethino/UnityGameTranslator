using System;
using System.IO;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether a string from a translation file may be used as a file name inside one of our
    /// folders — and nothing more than a file name.
    ///
    /// 🔴 **A translation file is downloaded from another player, so every name it carries is
    /// input, never data we wrote.** Handed to <c>Path.Combine</c> as it is, a name is a path:
    /// <c>..\..\Desktop\x</c> leaves the folder, <c>C:\x</c> ignores it, and the file then read —
    /// or worse, written — sits wherever the author of the file decided. This rule was first
    /// written for image replacements; font names go through the same doors (a system font is
    /// looked up on disk by name, and its atlas cache is written under that name), so the rule
    /// lives here once and both use it.
    ///
    /// ⚠ Pure on purpose: no Unity, no logging, no state — so the checks project can link it and
    /// prove what it refuses AND what it lets through. A font is named ("Noto Sans CJK SC",
    /// "AdobeDevanagari-Italic"), never located: no real name contains a separator, and refusing
    /// them loses nothing.
    /// </summary>
    public static class PlainFileName
    {
        public static bool Accepts(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return name.IndexOf('/') < 0
                && name.IndexOf('\\') < 0
                && name.IndexOf("..", StringComparison.Ordinal) < 0
                && !Path.IsPathRooted(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }
}
