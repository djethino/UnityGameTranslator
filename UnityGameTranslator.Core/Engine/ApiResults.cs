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
    }

    public class TranslationSearchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
        public List<TranslationInfo> Translations { get; set; }
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
}
