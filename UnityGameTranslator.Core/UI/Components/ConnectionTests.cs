namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// What a "Test" button says once its connection test is back — one wording for the options
    /// window and the first-run wizard, and for the AI server, Google and DeepL alike.
    ///
    /// 🔴 **The answer says what was found, never what is usually wrong.** Every failure used to
    /// read "Failed - check API key": a firewall blocking the game sent the player to re-type a
    /// key that was right. The key is named only when the server said it refused it (400, 401,
    /// 403); a request that never got an answer says why (see <c>Connectivity</c>); any other
    /// answer gives its status code.
    /// </summary>
    public static class ConnectionTests
    {
        /// <param name="whenKeyRefused">
        /// The sentence for a server that refused the key, or null when there is no key to blame.
        /// </param>
        public static void Tell(LabelHandle label, TranslatorCore.ConnectionTestResult result,
                                string whenConnected, string whenKeyRefused)
        {
            if (label == null || result == null) return;

            if (result.Success)
            {
                label.Say(whenConnected);
                label.Tone = Tone.Success;
                return;
            }

            label.Tone = Tone.Error;

            if (result.Error != null)
            {
                label.Show(TranslatorCore.TranslateOwnUIDynamic("Error:") + " " + result.Error);
                return;
            }

            if (whenKeyRefused != null && (result.Status == 400 || result.Status == 401 || result.Status == 403))
            {
                label.Say(whenKeyRefused);
                return;
            }

            label.Show(TranslatorCore.TranslateOwnUIDynamic("Connection failed") + $" (HTTP {result.Status})");
        }
    }
}
