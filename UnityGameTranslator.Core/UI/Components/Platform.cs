using UnityEngine;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The machine, as a panel reaches it — the clipboard, for now.
    ///
    /// A panel may not name UnityEngine (see Handles.cs), and copying a pairing code is the one
    /// thing a panel asks of the operating system directly. It goes through here, beside the
    /// widgets, so the panel keeps saying WHAT it wants done and never how the engine does it.
    /// </summary>
    public static class Platform
    {
        /// <summary>Put text on the system clipboard, for the person to paste elsewhere.</summary>
        public static void CopyToClipboard(string text)
        {
            GUIUtility.systemCopyBuffer = text ?? "";
        }
    }
}
