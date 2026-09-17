using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    // What the site's answers become on this side — the records ApiReaders fills and every
    // screen reads. Pure data: no Unity, no clock, no network. They used to sit at the bottom of
    // ApiClient.cs, which references HttpClient and TranslatorCore, so nothing could hold a
    // reader to the contract's cases without a game; now the file is linked into Core.Checks and
    // spec/api-v1/cases.json is read against it (ApiContractChecks).
    //
    // ⚠ The rule every reader here follows, stated once (spec/api-v1/openapi.json, rule 1): a
    // field the server did not send stays NULL where the record has a nullable, and null reads
    // as "unknown" — never as zero, false or "nobody". The non-nullable counters default to 0
    // because a published mod printed them that way before the field existed.

    public class ModNotificationsResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Unread { get; set; }
        public List<ModNotificationItem> Items { get; set; } = new List<ModNotificationItem>();
    }

    public class ModNotificationItem
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Text { get; set; }
        public string Url { get; set; }
        /// <summary>The lineage it is about, or null. Null too on a server that predates the field.</summary>
        public string Uuid { get; set; }

        /// <summary>
        /// A wall on a contribution — its Main closed, or gone. The fact the game's own screen
        /// states, with Fork as the way out, for the translation it holds.
        /// </summary>
        public bool IsWall => Type == "branches_closed" || Type == "branch_orphaned";
    }

    public class TranslationSearchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
        public List<TranslationInfo> Translations { get; set; }
    }

    /// <summary>
    /// The account's own library (<c>GET /me/translations</c>): which lineages it holds a row in,
    /// and on which side. One call for all of them — asking check-uuid per row of a list would
    /// spend the account's budget on a question one answer covers, as the Manager already reads it.
    /// </summary>
    public class MyTranslationsResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>The site refused the token: signed out on its side.</summary>
        public bool TokenRefused { get; set; }
        public List<LineagePosition> Rows { get; set; } = new List<LineagePosition>();
    }

    /// <summary>One row the account holds: the lineage, and whether the account leads it.</summary>
    public class LineagePosition
    {
        public int Id { get; set; }
        public string FileUuid { get; set; }
        public bool IsMain { get; set; }
    }

    public class TranslationInfo
    {
        public int Id { get; set; }
        public string GameName { get; set; }
        public string GameSlug { get; set; }
        public string GameSteamId { get; set; }
        public string GameImageUrl { get; set; }
        public string Uploader { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public int LineCount { get; set; }
        public string Status { get; set; }
        public string Type { get; set; }
        public string Notes { get; set; }
        public string ResourcesUrl { get; set; }
        /// <summary>Whether this lineage takes contributions. Null on an older server, and null
        /// is not "no" — nothing is said rather than inventing somebody's decision.</summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// Which translation this one was forked from. Null when it was forked from none — and on
        /// a server that predates the field, where nothing is said rather than a claim made.
        ///
        /// ⚠ Not derivable from anything else here: a fork leads its own lineage and looks exactly
        /// like a translation somebody wrote from scratch. See <see cref="Origins"/>.
        /// </summary>
        public Origin? Origin { get; set; }

        public int VoteCount { get; set; }
        /// <summary>This user's own vote (+1 / -1), null when they haven't voted, aren't
        /// signed in, or the server predates the field.</summary>
        public int? UserVote { get; set; }
        public int DownloadCount { get; set; }
        public int HumanCount { get; set; }
        public int ValidatedCount { get; set; }
        public int AiCount { get; set; }
        public int CaptureCount { get; set; }
        /// <summary>
        /// Lines the author marked as not to translate (tag S). Outside the composition bar and
        /// the score; shown on its own. Zero on servers that predate the field.
        /// </summary>
        public int SkippedCount { get; set; }
        public string FileHash { get; set; }
        public string FileUuid { get; set; }
        public string UpdatedAt { get; set; }

        /// <summary>
        /// When the translation itself last changed. Distinct from UpdatedAt,
        /// which a vote or a download also moves. Null on older servers.
        /// </summary>
        public string ContentUpdatedAt { get; set; }

        /// <summary>
        /// How much of the game this translation reaches, 0 to 1, measured against the furthest
        /// translation of the same game whatever its language.
        ///
        /// Comes from the server because it cannot be computed here: it needs every other
        /// translation of the game. Null on servers that do not report it — and null must read
        /// as "unknown", never as "covers nothing".
        /// </summary>
        public float? GameCoverage { get; set; }

        /// <summary>
        /// When it was published. Null on servers that do not report it — and absence must read
        /// as "unknown", never as "old".
        /// </summary>
        public string CreatedAt { get; set; }

        /// <summary>
        /// Published within the last week of <paramref name="utcNow"/>, by the same reckoning as
        /// the website. The clock is handed in: this record is read in a project with none.
        /// </summary>
        public bool IsNewAt(DateTime utcNow)
        {
            if (string.IsNullOrEmpty(CreatedAt)) return false;
            DateTime published;
            if (!DateTime.TryParse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out published))
                return false;
            return (utcNow - published.ToUniversalTime()).TotalDays <= 7;
        }

        /// <summary>
        /// The content date as a short string in <paramref name="zone"/>, or null when the server
        /// did not send one. Never falls back to UpdatedAt: showing a date that a vote moved would
        /// be worse than showing none.
        /// </summary>
        public string ContentDateLabel(TimeZoneInfo zone)
        {
            if (string.IsNullOrEmpty(ContentUpdatedAt)) return null;
            DateTime parsed;
            if (!DateTime.TryParse(ContentUpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed))
            {
                return null;
            }

            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Utc), zone)
                .ToString("d MMM yyyy");
        }
    }

    public class TranslationCheckResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool HasUpdate { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        public int VoteCount { get; set; }
        public string UpdatedAt { get; set; }

        /// <summary>
        /// Who published it. The only way someone with no account can learn whose work they
        /// installed — every other source of that name is behind authentication.
        /// Null on a server too old to send it, which reads as "unknown", never as "nobody".
        /// </summary>
        public string Uploader { get; set; }

        /// <summary>
        /// The server answered "nothing changed". ⚠ Every other field is then EMPTY, not zero:
        /// a caller that writes them anyway blanks the very values it was trying to spare.
        /// </summary>
        public bool NotModified { get; set; }

        /// <summary>
        /// The validator to hand back on the next call. Opaque on purpose — it stopped being
        /// the file hash the day the answer started carrying the vote count and the uploader.
        /// </summary>
        public string ETag { get; set; }
    }

    public class TranslationDownloadResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool NotModified { get; set; }
        public string Content { get; set; }
        public string FileHash { get; set; }
    }

    public class GameSearchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
        public List<GameApiInfo> Games { get; set; }
    }

    public class GameApiInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string SteamId { get; set; }
        public string ImageUrl { get; set; }
        public int TranslationsCount { get; set; }
        public string Source { get; set; } // "local", "steam", "igdb", "rawg"
    }

    public class DeviceFlowInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string DeviceCode { get; set; }
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
    }

    public class VoteResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int VoteCount { get; set; }
        /// <summary>User's current vote: 1 (upvote), -1 (downvote), or null (no vote)</summary>
        public int? UserVote { get; set; }
    }

    public class UploadRequest
    {
        public string SteamId { get; set; }
        public string GameName { get; set; }

        /// <summary>
        /// The studio Unity records beside the product name, when the game states one.
        ///
        /// 🔴 **What it buys.** The site keeps the pair as `unity_name`/`unity_company` and
        /// resolves lookups with it, so a translation published from here stays findable from
        /// another install whatever its folder is called. A product name alone is often too weak
        /// to identify a game — "Game", "Prototype" — and the studio settles it.
        /// </summary>
        public string GameCompany { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        // Note: Type is now auto-calculated by server from HVASM tags
        public string Status { get; set; }
        public string Content { get; set; }
        public string Notes { get; set; }
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions. Null on a branch — the decision belongs to
        /// the Main, and a contributor sending it would answer for somebody else's translation.
        /// </summary>
        public bool? AcceptsBranches { get; set; }
    }

    public class UploadResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int TranslationId { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        /// <summary>Role assigned by the server (Main for public, Branch for contributor)</summary>
        public LineageRole Role { get; set; } = LineageRole.None;
        public string WebUrl { get; set; }

        /// <summary>
        /// The origin as the site RECORDED it: a pointer the caller could not have held is dropped
        /// there, and this is where the client learns that. Null when none.
        /// </summary>
        public Origin? Origin { get; set; }
    }

    public class UuidCheckResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool Exists { get; set; }
        public bool IsOwner { get; set; }
        /// <summary>Detected role: Main (owner), Branch (contributor), or None (new)</summary>
        public LineageRole Role { get; set; } = LineageRole.None;
        /// <summary>Username of the Main translation owner (if this is a Branch)</summary>
        public string MainUsername { get; set; }

        /// <summary>Branch whose Main is gone. Null on servers that do not report it.</summary>
        public bool? MainMissing { get; set; }

        /// <summary>
        /// The Main is still there and the account behind it is not.
        ///
        /// Same consequence as MainMissing — nobody will ever merge this — reached another way, and
        /// the difference matters to whoever reads it: the translation is still published and still
        /// safe to keep using. Null on servers that do not report it.
        /// </summary>
        public bool? MainAbandoned { get; set; }
        /// <summary>Number of branches contributing to this UUID (if this is Main)</summary>
        public int BranchesCount { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions at all — the Main's own decision.
        ///
        /// Null on a server that predates the field. Null is NOT "no": announcing that somebody
        /// works alone because a server said nothing would put words in their mouth, so an unknown
        /// answer behaves exactly as before and the refusal, if any, arrives from the upload.
        /// </summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// A branch whose Main has since closed: nothing can be done with it as a branch any more.
        /// The way on is to publish it as a translation of its own.
        /// </summary>
        public bool? BranchFrozen { get; set; }

        /// <summary>
        /// Of the branches above, how many are actually waiting: not been through in their current
        /// state, AND holding something. Null on a server too old to say — which is "unknown",
        /// never "none". See <see cref="ServerTranslationState.BranchesWithWork"/>.
        /// </summary>
        public int? BranchesWithWork { get; set; }

        /// <summary>How many lines those hold, counted once each. Null if unknown.</summary>
        public int? LinesAvailable { get; set; }

        /// <summary>
        /// How many rows need a decision — see <see cref="ServerTranslationState.LinesToReview"/>.
        /// Null on a server that predates it, which is "unknown", never zero.
        /// </summary>
        public int? LinesToReview { get; set; }

        /// <summary>Of those, the ones the Main does not hold, by the contribution's tag.</summary>
        public TagTally LinesNew { get; set; }

        /// <summary>Of those, the ones both sides hold differently, by the contribution's tag.</summary>
        public TagTally LinesDiffering { get; set; }

        /// <summary>On a branch: what this contribution still holds for its Main. Null if unknown.</summary>
        public int? LinesOffered { get; set; }

        public UuidCheckTranslationInfo ExistingTranslation { get; set; } // For UPDATE
        public UuidCheckTranslationInfo OriginalTranslation { get; set; } // For FORK

        /// <summary>
        /// Votes on the PUBLISHED translation of this lineage — the one being played, and the
        /// one the ranking ranks. Null when nothing of it is published, and on any server too
        /// old to report it: absence must read as "unknown", never as "no votes".
        /// </summary>
        public VoteState Vote { get; set; }
    }

    /// <summary>
    /// What the mod needs to show a vote without deciding anything itself. Whether the player
    /// MAY vote is a server rule (one owner, one translation, no self-votes) and stays there:
    /// the mod asks, it does not re-implement.
    /// </summary>
    public class VoteState
    {
        /// <summary>The translation a vote from here would land on.</summary>
        public int TargetId { get; set; }
        public int Count { get; set; }
        /// <summary>This player's own vote (+1 / -1), null when they have not voted.</summary>
        public int? UserVote { get; set; }
        public bool CanVote { get; set; }
    }

    public class UuidCheckTranslationInfo
    {
        public int Id { get; set; }
        public string Uploader { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public string Type { get; set; }

        /// <summary>
        /// "in_progress" or "complete" — the author's own declaration.
        ///
        /// ⚠ Read so it can be SHOWN and sent back unchanged. Without it the upload posted
        /// "in_progress" every time, quietly undoing a translation its author had marked complete
        /// on the website.
        /// </summary>
        public string Status { get; set; }
        public string Notes { get; set; }

        /// <summary>
        /// Where this row came from when it is a fork of somebody else's work — the same block
        /// the listing carries, so the card in the game credits the source the way the site's
        /// page does. Null when it started from nobody's, or on a site that predates the field.
        /// </summary>
        public Origin? Origin { get; set; }

        /// <summary>
        /// The link to show: this translation's own, or the Main's when a branch has none.
        /// </summary>
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// The link to EDIT: this row's own, never an inherited one.
        ///
        /// 🔴 Prefilling the edit field from <see cref="ResourcesUrl"/> and posting it back makes
        /// a branch adopt a copy of its Main's link and stop following it, over an edit its author
        /// never made. Null on servers older than the field, where the caller falls back.
        /// </summary>
        public string OwnResourcesUrl { get; set; }

        public int LineCount { get; set; }
        public string FileHash { get; set; }
        public string UpdatedAt { get; set; }
    }

    public class BranchListResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<BranchInfo> Branches { get; set; }
    }

    /// <summary>
    /// Information about a branch (contributor) to a translation
    /// </summary>
    public class BranchInfo
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public int LineCount { get; set; }
        /// <summary>Number of human-translated entries (tag H)</summary>
        public int HumanCount { get; set; }
        /// <summary>Number of AI-translated entries (tag A)</summary>
        public int AiCount { get; set; }
        /// <summary>Number of validated entries (tag V)</summary>
        public int ValidatedCount { get; set; }
        public string UpdatedAt { get; set; }
    }

    public class MergePreviewInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Token for the merge preview session</summary>
        public string Token { get; set; }
        /// <summary>URL to open in browser (may be relative)</summary>
        public string Url { get; set; }
        /// <summary>ISO8601 expiration timestamp</summary>
        public string ExpiresAt { get; set; }
    }

    public class EditSessionInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Mod-side key for content download and SSE stream (never shown to a browser)</summary>
        public string ModKey { get; set; }
        /// <summary>URL to open in browser (may be relative, contains the one-time browser token)</summary>
        public string Url { get; set; }
        /// <summary>ISO8601 expiration timestamp</summary>
        public string ExpiresAt { get; set; }
    }

    public class EditSessionContentResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Raw JSON of the session translations file</summary>
        public string Content { get; set; }
        /// <summary>
        /// True when the server no longer knows the session (404). Distinguishes
        /// "the session is over" — forget it — from a transient network failure,
        /// which must keep a resumable session on disk.
        /// </summary>
        public bool SessionGone { get; set; }
    }

    public class EditSessionUpdateResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>True when the server no longer knows the session (404)</summary>
        public bool SessionGone { get; set; }
        /// <summary>sha256 of the session file after this push</summary>
        public string ContentHash { get; set; }
        /// <summary>Seconds since the browser last signaled presence (null: never opened)</summary>
        public int? BrowserSeenSecondsAgo { get; set; }
        /// <summary>True when the pagehide beacon fired without a rejoin since</summary>
        public bool BrowserLeft { get; set; }
    }

    /// <summary>What the site says about a session we are asking about, not living in.</summary>
    public class EditSessionProbe
    {
        /// <summary>True alive, false gone, null could not ask.</summary>
        public bool? Exists { get; set; }

        /// <summary>
        /// Saves the browser made that nobody has fetched. ⚠ Until somebody does, the session
        /// is the only place that work exists.
        /// </summary>
        public int PendingChanges { get; set; }
    }

    /// <summary>What came of asking the site to change what a translation says about itself.</summary>
    public class DetailsResult
    {
        public bool Success;
        public string Error;
    }

    // ── What the relay's streams carry (spec/sse-events) ──────────────────────────────────

    /// <summary>The device-flow stream's one event: the access, delivered once.</summary>
    public class DeviceAuthorizedEvent
    {
        public string AccessToken { get; set; }
        public int? UserId { get; set; }
        public string UserName { get; set; }
    }

    /// <summary>
    /// A stream's `error` event, before or during. <see cref="Revoked"/> is the one a client reads
    /// as "sign out": the relay names it (<c>code: revoked</c>); every other error is a stream
    /// that ended, and the connection loop decides what that means.
    /// </summary>
    public class StreamErrorEvent
    {
        public string Error { get; set; }
        public string Code { get; set; }
        public bool Revoked => Code == "revoked";
    }

    /// <summary>The sync stream's `translation_updated`: the caller's own row moved.</summary>
    public class TranslationUpdatedEvent
    {
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        public int VoteCount { get; set; }
    }

    /// <summary>
    /// The merge-preview stream's `merge_completed`. <see cref="ToLocal"/> says the arbitrated
    /// file is collected from the token (nothing was published), otherwise it IS the published
    /// translation and is read back through the ordinary download.
    /// </summary>
    public class MergeCompletedEvent
    {
        public int? TranslationId { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        public bool ToLocal { get; set; }
    }

    /// <summary>The edit-session stream's `edit_saved`: the browser saved, fetch the file.</summary>
    public class EditSavedEvent
    {
        public string ContentHash { get; set; }
        public int LineCount { get; set; }
        public string SavedAt { get; set; }
    }

    /// <summary>The edit-session stream's `edit_retranslate`: the page asks for one line again.</summary>
    public class EditRetranslateEvent
    {
        public string Key { get; set; }
        public string RequestId { get; set; }
    }

    /// <summary>
    /// Server state for current translation (from check-uuid, not persisted to disk)
    /// </summary>
    public class ServerTranslationState
    {
        /// <summary>
        /// This answer as the socle's fact sheet — what <see cref="Standings.From"/> composes a
        /// standing from. Null state, nothing known: an empty sheet, never a claim.
        /// </summary>
        public static ServerFacts FactsOf(ServerTranslationState state)
        {
            if (state == null) return new ServerFacts();

            return new ServerFacts
            {
                Checked = state.Checked,
                Exists = state.Exists,
                IsOwner = state.IsOwner,
                Role = state.Role,
                SiteId = state.SiteId,
                Hash = state.Hash,
                Uploader = state.Uploader,
                MainUsername = state.MainUsername,
                BranchesCount = state.BranchesCount,
                BranchesWithWork = state.BranchesWithWork,
                LinesAvailable = state.LinesAvailable,
                LinesChanged = state.LinesChangedFor == state.Hash ? state.LinesChanged : null,
                LinesChangedHere = state.LinesChangedFor == state.Hash ? state.LinesChangedHere : null,
                AcceptsBranches = state.AcceptsBranches,
                MainMissing = state.MainMissing,
                MainAbandoned = state.MainAbandoned,
                BranchFrozen = state.BranchFrozen,
                MainIgnoring = state.MainIgnoring,
                Status = state.Status,
            };
        }

        /// <summary>True if we've checked with the server (even if translation doesn't exist)</summary>
        public bool Checked { get; set; } = false;

        /// <summary>
        /// Whether the server was asked AS THIS ACCOUNT — so <see cref="IsOwner"/> and
        /// <see cref="Role"/> are answers rather than defaults.
        ///
        /// 🔴 **"Checked" and "checked as us" are two different facts, and one screen took the
        /// first for the second.** The public endpoint answers about a translation, never about a
        /// person: it fills this state with `IsOwner = false, Role = None` because that is all an
        /// anonymous caller can be told. Read by somebody signed in, before their own check comes
        /// back, that reads as "this is not yours" — and the notification offered the OWNER of the
        /// translation the two buttons meant for a stranger, Branch and Fork, for as long as the
        /// account check took.
        ///
        /// ⚠ It cannot be told from the shape: `not owner, role none` is also the honest, final
        /// answer for somebody using another person's translation. Only who was asked separates
        /// them, so it is recorded rather than guessed.
        /// </summary>
        public bool AskedAsAccount { get; set; } = false;

        /// <summary>
        /// Where this account's row came from when it is a fork — what the card's "Forked from
        /// @x" chip is read from, the same block the community list already credits. Kept from
        /// the previous state when an answer does not carry the key (an older site): absent is
        /// unknown, never "started from nobody".
        /// </summary>
        public Origin? Origin { get; set; }

        /// <summary>
        /// How many lines the published copy changed since this machine last synced, and the
        /// server hash that count was made for — counted by the interface from the copy it
        /// fetched once (see TranslatorUIManager.EnsureRemoteChangesCounted). Read as unknown
        /// whenever the hash moved on.
        /// </summary>
        public int? LinesChanged { get; set; }
        public string LinesChangedFor { get; set; }

        /// <summary>
        /// From the same comparison: how many lines here differ from the published copy, and how
        /// many lines differ at all (a line changed on both sides counts once) — what the
        /// comparison page will list. ⚠ Not LocalChangesCount: a line added here that the site
        /// also added, identically, changed since the sync and differs from nothing.
        /// </summary>
        public int? LinesChangedHere { get; set; }
        public int? LinesDifferingFromCopy { get; set; }

        /// <summary>
        /// The comparison is about a copy this machine has not taken in. Once a sync lands on
        /// the hash it was counted for — a merge, a download, an upload — the file and the copy
        /// no longer differ by those lines, and the counts would go on saying they do: Compare
        /// read "(8)" over a file that had just merged the 3 lines it counted. Forgotten at the
        /// sync, counted again only if the site moves on.
        /// </summary>
        public void ForgetComparison()
        {
            LinesChanged = null;
            LinesChangedHere = null;
            LinesDifferingFromCopy = null;
            LinesChangedFor = null;
        }
        /// <summary>True if translation exists on server</summary>
        public bool Exists { get; set; } = false;
        /// <summary>True if current user owns the translation</summary>
        public bool IsOwner { get; set; } = false;
        /// <summary>Translation ID on server</summary>
        public int? SiteId { get; set; }
        /// <summary>Username of uploader</summary>
        public string Uploader { get; set; }
        /// <summary>File hash on server</summary>
        public string Hash { get; set; }
        /// <summary>Translation type (ai, human, etc.)</summary>
        public string Type { get; set; }
        /// <summary>Translation notes</summary>
        public string Notes { get; set; }
        /// <summary>URL to external resources (fonts, images)</summary>
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// "in_progress" or "complete", as published. Null when unknown — an older server, or a
        /// lineage we do not own — and a caller must then leave it alone rather than pick one.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions — the Main's own decision.
        ///
        /// Null when unknown: an older server, or a lineage nobody has asked about yet. Unknown
        /// is NOT "solo work", and every reader must leave it alone rather than pick one.
        /// </summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// This branch's Main has closed to contributions since: it can no longer be sent, nor
        /// have its details changed.
        ///
        /// 🔴 Nothing inside the game changes when it happens — the file opens, translates and
        /// saves exactly as before — so unless a screen says it, the discovery happens at the
        /// moment of publishing, after the work. Null is unknown, never "all is well".
        /// </summary>
        public bool? BranchFrozen { get; set; }

        /// <summary>
        /// THIS ACCOUNT's role in the lineage — the row published under its name.
        ///
        /// 🔴 <see cref="LineageRole.None"/> unless <see cref="IsOwner"/>, always. The mod's own
        /// enum used to carry two meanings: the server's "branch" (this account's row is a
        /// contribution) and, from one writer, "holding somebody else's lineage without having
        /// published" — which its own comment said was NOT a branch. Five screens read the role
        /// without IsOwner and confused the two; one becomes a Branch by uploading, and only then.
        /// Since 2026-09-07 the role is the socle's <see cref="LineageRole"/>, and every writer
        /// leaves it None for somebody who has no row.
        /// </summary>
        public LineageRole Role { get; set; } = LineageRole.None;

        /// <summary>If Branch, the username of the Main owner</summary>
        public string MainUsername { get; set; }

        /// <summary>
        /// Branch whose Main no longer exists — deleted, or its account closed.
        ///
        /// Nobody can ever merge this work: a branch needs a head to be merged into. The way
        /// forward is to publish it as a translation of its own, which the Fork action does.
        /// Null on servers too old to report it, and that absence must read as "unknown" rather
        /// than "the Main is fine".
        /// </summary>
        public bool? MainMissing { get; set; }

        /// <summary>
        /// The Main is still published and the account that owned it has been erased.
        ///
        /// Ends the same way as MainMissing — a branch needs somebody to be merged by, and there is
        /// nobody — but it is the harder of the two to notice: the Main is still listed, still
        /// downloadable, and still says it accepts contributions. Nothing ever fails; the work
        /// simply waits for a reader who does not exist.
        ///
        /// ⚠ Kept apart from MainMissing rather than folded into it, because what somebody has to
        /// understand is not the same: here the translation is still there and still safe to use.
        /// Null on servers too old to report it.
        /// </summary>
        public bool? MainAbandoned { get; set; }

        /// <summary>
        /// The Main was told about this branch, has edited their own file since, and has taken
        /// nothing in. Not the same as silence — that is dormancy — and said once only.
        /// Null on servers too old to report it.
        /// </summary>
        public bool? MainIgnoring { get; set; }

        /// <summary>Lines of this branch the Main has taken in, added up over every merge.</summary>
        public int MergedLinesTotal { get; set; }

        /// <summary>If Main, the number of branches</summary>
        public int BranchesCount { get; set; }

        /// <summary>
        /// Of those branches, how many are actually waiting on their Main: not been through in
        /// their current state, AND holding something a merge would offer.
        ///
        /// 🔴 **This is what a screen shows, not <see cref="BranchesCount"/>.** That one answers
        /// "how many people contribute" — true, and not the question somebody asks when deciding
        /// whether to open the merge screen. Counting a contributor who took the file months ago
        /// and never came back sends their Main to review emptiness.
        ///
        /// ⚠ Null on a server too old to say. Unknown is not zero: a screen falls back to the raw
        /// count rather than announcing that nothing is waiting.
        /// </summary>
        public int? BranchesWithWork { get; set; }

        /// <summary>How many lines those contributions hold, counted once each. Null if unknown.</summary>
        public int? LinesAvailable { get; set; }

        /// <summary>
        /// How many rows need a DECISION — lines the Main does not hold, plus lines both sides hold
        /// differently, the ones it will keep its own on included.
        ///
        /// 🔴 **Not <see cref="LinesAvailable"/>, and neither follows from the other.** That one is
        /// what would be taken; this is what has to be looked at. Measured on a real lineage: 56 and
        /// 38, the 18 in between being two machine translations that differ. One answers "how long
        /// will this take", the other "is there anything here for me".
        ///
        /// ⚠ Null on a server too old to say. Unknown is not zero.
        /// </summary>
        public int? LinesToReview { get; set; }

        /// <summary>
        /// Of those rows, the ones the Main does not hold at all, by the contribution's tag —
        /// because 21 new lines written by hand is not the proposition 21 machine lines are.
        /// </summary>
        public TagTally LinesNew { get; set; }

        /// <summary>Of those rows, the ones both sides hold differently, by the contribution's tag.</summary>
        public TagTally LinesDiffering { get; set; }

        /// <summary>
        /// On a branch: how many lines THIS contribution is still holding for its Main — what was
        /// sent and not taken in. Its author's own business, and nobody else's.
        /// </summary>
        public int? LinesOffered { get; set; }

        /// <summary>
        /// Votes on the PUBLISHED translation of this lineage — count, this player's own vote,
        /// and whether they may vote at all. The server decides that last one: no self-votes,
        /// public only. Null when nothing is published, and on any server too old to report it —
        /// absence reads as "unknown", never as "no votes".
        /// </summary>
        public VoteState Vote { get; set; }

        /// <summary>
        /// If Main, how many branches have never been reviewed or changed since.
        /// The plain count above cannot answer that: it does not move when a
        /// contributor pushes more work to a branch already counted.
        /// </summary>
        public int BranchesPendingReview { get; set; }

        /// <summary>
        /// If Branch, the Main this translation derives from — id and hash, so the
        /// mod can tell that upstream moved without downloading anything.
        /// Null for a Main, for a detached fork, and for any server that does not
        /// report it yet (older site: absence must read as "unknown", not "gone").
        /// </summary>
        public int? MainSiteId { get; set; }
        public string MainHash { get; set; }
        public int MainLineCount { get; set; }

        /// <summary>Source language of the translation (original game language)</summary>
        public string SourceLanguage { get; set; }

        /// <summary>Target language of the translation (translated to)</summary>
        public string TargetLanguage { get; set; }
    }
}
