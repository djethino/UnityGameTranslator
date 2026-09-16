using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The three fact sheets the socle composes a translation's standing from, read off what the
    /// engine holds right now — the one place that knows which engine member answers which fact.
    ///
    /// 🔴 **One reader, for every screen.** The main screen, the upload window and the corner
    /// notification each gathered these facts for themselves and each got one of them slightly
    /// differently — "exists on the server" with or without a site id, the role with or without
    /// the ownership test. What the socle then decided differed with the gathering. Reading them
    /// here means every screen asks <see cref="Standings.From"/> the same question.
    ///
    /// ⚠ The content hash costs a pass over every line, so it is computed only when there is a
    /// published content to compare it with — the one case the sync verdict needs it — and only
    /// when the caller asks: a screen deciding an act on a background thread must not walk the
    /// cache.
    /// </summary>
    public static class StandingFacts
    {
        /// <param name="withContentHash">Compute the content hash when a published content exists to compare it with.</param>
        public static LocalFacts Local(bool withContentHash = true)
        {
            var server = TranslatorCore.ServerState;

            return new LocalFacts
            {
                Lines = TranslatorCore.TranslationCache.Count,
                LocalChanges = TranslatorCore.LocalChangesCount,
                MetadataDirty = TranslatorCore.MetadataDirty,
                LastSyncedHash = TranslatorCore.LastSyncedHash,
                ContentHash = withContentHash && server != null && server.Exists
                    ? TranslatorCore.ComputeContentHash()
                    : null,
                ForkStillTheCopy = TranslatorCore.ForkIsStillTheCopy,
            };
        }

        public static ServerFacts Server() => ServerTranslationState.FactsOf(TranslatorCore.ServerState);

        /// <summary>
        /// ⚠ From the point of view of the game itself, which holds its own credential: the
        /// question the manager asks — is this somebody else's game — cannot arise here.
        /// </summary>
        public static AccountFacts Account() => new AccountFacts
        {
            SignedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token),
            Online = TranslatorCore.Config.online_mode,
        };

        /// <summary>The standing now, with the sheets it was read from — a screen keeps them beside it.</summary>
        public static Standing Now(out LocalFacts local, out ServerFacts server, out AccountFacts account,
                                   bool withContentHash = true)
        {
            local = Local(withContentHash);
            server = Server();
            account = Account();
            return Standings.From(local, server, account);
        }
    }
}
