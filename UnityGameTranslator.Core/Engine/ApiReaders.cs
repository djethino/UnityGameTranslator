using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What the site's answers mean on this side — one reader per route, a JSON object in and a
    /// record out, nothing else.
    ///
    /// 🔴 **Pure, and held to the contract's cases.** These bodies were written inline in
    /// ApiClient, between an HttpClient call and a log line, so nothing could ask "what does the
    /// mod derive from THIS answer" without a game and a server. They now live here, linked into
    /// Core.Checks, where <c>common/spec/api-v1/cases.json</c> — the same file the site replays
    /// with PHPUnit — is fed to each reader and what it derives is compared to what the case says
    /// (ApiContractChecks). A second Core reads this file to learn the same derivations.
    ///
    /// ⚠ **Absent means unknown, never no** (spec/api-v1, rule 1). A key the server did not send
    /// leaves a nullable at null and a counter at zero; <c>ToObject&lt;T?&gt;</c> rather than
    /// <c>Value&lt;T&gt;</c> wherever a JSON null must stay a C# null. And <c>as JObject</c>
    /// before indexing: a key sent as JSON null comes back as a JValue, which <c>?.</c> lets
    /// through to an indexer that throws.
    /// </summary>
    public static class ApiReaders
    {
        // ── Errors ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The sentence to show for a refused call, from what the site wrote — <c>error</c> first,
        /// then <c>message</c> — and the status code when it wrote nothing readable. A rate limit
        /// gets its own sentence: "Too Many Requests" reads as a breakage when the only thing to
        /// do is wait, and the wait is short and known.
        /// </summary>
        public static string DescribeError(int status, JObject body, int retryAfterSeconds)
        {
            if (status == 429)
            {
                return $"too many attempts in a row, wait {(retryAfterSeconds > 0 ? retryAfterSeconds : 60)}s and try again";
            }

            string message = body?["error"]?.Value<string>() ?? body?["message"]?.Value<string>();
            if (!string.IsNullOrEmpty(message)) return message;

            return $"HTTP {status} {StatusName(status)}";
        }

        private static string StatusName(int status)
        {
            switch (status)
            {
                case 400: return "BadRequest";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "NotFound";
                case 409: return "Conflict";
                case 413: return "RequestEntityTooLarge";
                case 415: return "UnsupportedMediaType";
                case 422: return "UnprocessableEntity";
                case 500: return "InternalServerError";
                case 503: return "ServiceUnavailable";
                default: return status.ToString();
            }
        }

        /// <summary>
        /// The sentence for an upload that failed: the site's, or Laravel's validation lines
        /// joined — those name the field, which is more than "Unprocessable" says.
        /// </summary>
        private static string UploadError(int status, JObject data)
        {
            string errorMsg = data?["error"]?.Value<string>()
                ?? data?["message"]?.Value<string>()
                ?? $"HTTP {status} {StatusName(status)}";

            var errors = data?["errors"] as JObject;
            if (errors != null)
            {
                var errorList = new List<string>();
                foreach (var prop in errors.Properties())
                {
                    foreach (var e in prop.Value)
                    {
                        errorList.Add(e.Value<string>());
                    }
                }
                if (errorList.Count > 0)
                {
                    errorMsg = string.Join(", ", errorList);
                }
            }

            return errorMsg;
        }

        // ── Listings ────────────────────────────────────────────────────────────────────────

        /// <summary><c>GET /translations?steam_id=…</c>: the flat rows, as every published mod reads them.</summary>
        public static TranslationSearchResult ReadSearch(JObject data)
        {
            var result = new TranslationSearchResult { Success = true };
            result.Count = data["count"]?.Value<int>() ?? 0;
            result.Translations = new List<TranslationInfo>();

            var translations = data["translations"] as JArray;
            if (translations != null)
            {
                foreach (var t in translations)
                {
                    result.Translations.Add(ReadListingRow(t));
                }
            }

            return result;
        }

        /// <summary>
        /// <c>GET /translations?q=…</c>: the answer can describe SEVERAL games, on purpose — a
        /// game folder is not always named as the site names it. Which group was asked about is
        /// <see cref="GameNames"/>' decision, the socle's, because the Manager asks the same
        /// question about fifty games and the two must not answer it differently.
        ///
        /// ⚠ The flat list is still read when <c>games</c> is absent: a self-hosted site older
        /// than this mod answers exactly as before, and losing its results would be a worse
        /// fault than the one being fixed.
        /// </summary>
        public static TranslationSearchResult ReadSearchByName(JObject data, string gameName)
        {
            var result = new TranslationSearchResult { Success = true };
            result.Translations = new List<TranslationInfo>();

            var grouped = data["games"] as JArray;

            if (grouped != null)
            {
                var names = new List<string>();
                foreach (var group in grouped)
                {
                    names.Add(group["game"]?["name"]?.Value<string>() ?? "");
                }

                var which = GameNames.Which(names, gameName);

                // ⚠ Counted from what was KEPT, never from the server's total: the total answers
                // about every game the name touched, and printing it beside a filtered list is
                // the same lie in a smaller place.
                result.Count = 0;

                foreach (var index in which.Chosen)
                {
                    var group = grouped[index];
                    result.Count += group["total"]?.Value<int>() ?? 0;

                    var rows = group["translations"] as JArray;
                    if (rows == null) continue;

                    foreach (var t in rows)
                    {
                        result.Translations.Add(ReadListingRow(t));
                    }
                }

                return result;
            }

            result.Count = data["count"]?.Value<int>() ?? 0;

            var translations = data["translations"] as JArray;
            if (translations != null)
            {
                foreach (var t in translations)
                {
                    result.Translations.Add(ReadListingRow(t));
                }
            }

            return result;
        }

        /// <summary>One translation as every listing describes it (<c>ListingRow</c> in the spec).</summary>
        public static TranslationInfo ReadListingRow(JToken t)
        {
            var game = t["game"] as JObject;
            return new TranslationInfo
            {
                Id = t["id"]?.Value<int>() ?? 0,
                GameName = game?["name"]?.Value<string>(),
                GameSlug = game?["slug"]?.Value<string>(),
                GameSteamId = game?["steam_id"]?.Value<string>(),
                GameImageUrl = game?["image_url"]?.Value<string>(),
                Uploader = t["uploader"]?.Value<string>(),
                SourceLanguage = t["source_language"]?.Value<string>(),
                TargetLanguage = t["target_language"]?.Value<string>(),
                LineCount = t["line_count"]?.Value<int>() ?? 0,
                Status = t["status"]?.Value<string>(),
                Type = t["type"]?.Value<string>(),
                Notes = t["notes"]?.Value<string>(),
                ResourcesUrl = t["resources_url"]?.Value<string>(),
                // Whether its Main takes contributions. Null on a server that predates the
                // field, and null shows nothing — silence is not "solo work".
                AcceptsBranches = t["accepts_branches"]?.ToObject<bool?>(),
                // Where a fork came from. Absent on a server that predates the field and on
                // anything nobody forked — both read as "started from nothing", which is what the
                // row then says by saying nothing.
                Origin = ReadOrigin(t["origin"]),
                VoteCount = t["vote_count"]?.Value<int>() ?? 0,
                // Null for anonymous callers and for servers older than this field.
                UserVote = t["user_vote"]?.Value<int?>(),
                DownloadCount = t["download_count"]?.Value<int>() ?? 0,
                HumanCount = t["human_count"]?.Value<int>() ?? 0,
                ValidatedCount = t["validated_count"]?.Value<int>() ?? 0,
                AiCount = t["ai_count"]?.Value<int>() ?? 0,
                CaptureCount = t["capture_count"]?.Value<int>() ?? 0,
                SkippedCount = t["skipped_count"]?.Value<int>() ?? 0,
                FileHash = t["file_hash"]?.Value<string>(),
                FileUuid = t["file_uuid"]?.Value<string>(),
                UpdatedAt = t["updated_at"]?.Value<string>(),
                // Null on servers older than this field: the list then shows no
                // date rather than one that a vote could have moved
                ContentUpdatedAt = t["content_updated_at"]?.Value<string>(),
                // Same rule: absent means unknown, and the list says nothing rather than 0 %
                GameCoverage = t["game_coverage"]?.Value<float?>(),
                CreatedAt = t["created_at"]?.Value<string>()
            };
        }

        /// <summary>
        /// Where a fork came from, or null when it came from nowhere.
        ///
        /// ⚠ An author of null inside a present block is NOT the same as an absent block: the
        /// first is a fork whose source account has gone, which is still a credit worth showing;
        /// the second is a translation somebody started themselves.
        /// </summary>
        public static Origin? ReadOrigin(JToken origin)
        {
            if (origin == null || origin.Type != JTokenType.Object) return null;

            return new Origin(origin["author"]?.Value<string>(),
                              origin["lines"]?.Value<int?>());
        }

        /// <summary>
        /// One side of a review — "new" or "differing" — read into the socle's own shape.
        ///
        /// ⚠ A missing letter is zero, unlike a missing figure elsewhere: the server sends only the
        /// tags it counted, so an absent "S" means no refusals rather than an unknown number. What
        /// stands for "we do not know" is the whole <c>lines_waiting</c> block being absent, which
        /// an older server does not send at all. ⚠ And an EMPTY tally arrives as <c>[]</c>, not
        /// <c>{}</c> (a PHP array with no key serialises as a list) — "not an object" is "nothing
        /// counted", which the cast below answers with the default.
        /// </summary>
        public static TagTally TallyOf(JToken waiting, string side)
        {
            var tags = (waiting as JObject)?[side];
            if (tags == null || tags.Type != JTokenType.Object) return default(TagTally);

            return new TagTally
            {
                Human = tags["H"]?.Value<int>() ?? 0,
                Validated = tags["V"]?.Value<int>() ?? 0,
                Machine = tags["A"]?.Value<int>() ?? 0,
                Skipped = tags["S"]?.Value<int>() ?? 0,
            };
        }

        // ── One translation ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>GET /translations/{id}/check</c>, on a 200. Whether there is an update is OUR
        /// comparison of the two hashes rather than the optional flag: the caller always knows
        /// the hash it is holding.
        /// </summary>
        public static TranslationCheckResult ReadCheck(JObject data, string localHash, string etag)
        {
            string serverHash = data["file_hash"]?.Value<string>();

            return new TranslationCheckResult
            {
                Success = true,
                FileHash = serverHash,
                LineCount = data["line_count"]?.Value<int>() ?? 0,
                VoteCount = data["vote_count"]?.Value<int>() ?? 0,
                // Absent on an older server: left null, which the caller reads as "unknown"
                // and never as "published by nobody"
                Uploader = data["uploader"]?.Value<string>(),
                UpdatedAt = data["updated_at"]?.Value<string>(),
                ETag = etag,
                HasUpdate = !string.IsNullOrEmpty(serverHash)
                            && !string.IsNullOrEmpty(localHash)
                            && serverHash != localHash,
            };
        }

        /// <summary><c>GET /translations/check-uuid</c>: where this account stands in a lineage.</summary>
        public static UuidCheckResult ReadUuidCheck(JObject data)
        {
            // Parse role first to derive IsOwner
            LineageRole role = RoleOf(data["role"]?.Value<string>());

            var result = new UuidCheckResult
            {
                Success = true,
                Exists = data["exists"]?.Value<bool>() ?? false,
                // IsOwner = user has a translation (role is main or branch)
                IsOwner = role == LineageRole.Main || role == LineageRole.Branch,
                Role = role,
                // MainUsername is in main.uploader when role is none and main exists
                MainUsername = (data["main"] as JObject)?["uploader"]?.Value<string>(),
                // Null on older servers: "unknown", never "the Main is fine"
                MainMissing = data["main_missing"]?.ToObject<bool?>(),
                MainAbandoned = data["main_abandoned"]?.ToObject<bool?>(),
                // Use ToObject<int?>() to handle explicit JSON null values
                BranchesCount = data["branches_count"]?.ToObject<int?>() ?? 0,

                // ⚠ ToObject<bool?> rather than Value<bool>: a missing field must stay null
                // and not become false. See the properties.
                AcceptsBranches = data["accepts_branches"]?.ToObject<bool?>(),
                BranchFrozen = data["branch_frozen"]?.ToObject<bool?>(),

                // ⚠ Null on an older site, and null is "unknown" — never "nothing is waiting".
                BranchesWithWork = data["branches_with_work"]?.ToObject<int?>(),
                LinesAvailable = data["lines_available"]?.ToObject<int?>(),

                // The other axis: how many rows need a decision, and what they are made of.
                // Absent on a server that predates it, and the card then shows the total
                // alone, as before.
                LinesToReview = (data["lines_waiting"] as JObject)?["review"]?.ToObject<int?>(),
                LinesNew = TallyOf(data["lines_waiting"], "new"),
                LinesDiffering = TallyOf(data["lines_waiting"], "differing"),
                LinesOffered = data["lines_offered"]?.ToObject<int?>()
            };

            // Votes on the published translation of this lineage. Absent on older servers,
            // and null when nothing of this lineage is published — both mean "no vote to
            // show here", never "zero votes".
            result.Vote = ReadVoteState(data["vote"]);

            // The caller's own row, when it holds one
            var t = data["translation"] as JObject;
            if (result.Exists && result.IsOwner && t != null)
            {
                result.ExistingTranslation = new UuidCheckTranslationInfo
                {
                    Id = t["id"]?.Value<int>() ?? 0,
                    SourceLanguage = t["source_language"]?.Value<string>(),
                    TargetLanguage = t["target_language"]?.Value<string>(),
                    Type = t["type"]?.Value<string>(),
                    // Null on a server that predates this field — the caller then leaves the
                    // status alone rather than guessing at one.
                    Status = t["status"]?.Value<string>(),
                    Notes = t["notes"]?.Value<string>(),
                    ResourcesUrl = t["resources_url"]?.Value<string>(),
                    // The row's own link, which is not the same question as the one above.
                    // Null on a server that predates the field — the edit field then falls
                    // back to the effective value, exactly as it behaved before.
                    OwnResourcesUrl = t["resources_url_own"]?.Value<string>(),
                    LineCount = t["line_count"]?.Value<int>() ?? 0,
                    FileHash = t["file_hash"]?.Value<string>(),
                    UpdatedAt = t["updated_at"]?.Value<string>(),
                    // Null on a site that predates the field, and null when the row started
                    // from nobody's work — the card shows no credit either way.
                    Origin = ReadOrigin(t["origin"]),
                    DownloadCount = t["download_count"]?.ToObject<int?>()
                };
            }

            // The Main, when the caller holds nothing in its lineage (an upload would become a branch)
            var m = data["main"] as JObject;
            if (result.Exists && !result.IsOwner && m != null)
            {
                result.OriginalTranslation = new UuidCheckTranslationInfo
                {
                    Id = m["id"]?.Value<int>() ?? 0,
                    Uploader = m["uploader"]?.Value<string>(),
                    SourceLanguage = m["source_language"]?.Value<string>(),
                    TargetLanguage = m["target_language"]?.Value<string>(),
                    Type = m["type"]?.Value<string>(),
                    LineCount = m["line_count"]?.Value<int>() ?? 0,
                    UpdatedAt = m["updated_at"]?.Value<string>()
                };
            }

            return result;
        }

        /// <summary>The closed word <c>role</c>: main, branch, none — never fork.</summary>
        public static LineageRole RoleOf(string role)
        {
            switch (role)
            {
                case "main": return LineageRole.Main;
                case "branch": return LineageRole.Branch;
                default: return LineageRole.None;
            }
        }

        /// <summary>The <c>vote</c> block, or null when it is absent or JSON null.</summary>
        public static VoteState ReadVoteState(JToken vote)
        {
            if (vote == null || vote.Type != JTokenType.Object) return null;

            return new VoteState
            {
                TargetId = vote["target_id"]?.Value<int>() ?? 0,
                Count = vote["count"]?.Value<int>() ?? 0,
                UserVote = vote["user_vote"]?.Value<int?>(),
                CanVote = vote["can_vote"]?.Value<bool>() ?? false,
            };
        }

        /// <summary><c>GET /translations/{uuid}/branches</c>.</summary>
        public static BranchListResult ReadBranches(JObject data)
        {
            var result = new BranchListResult
            {
                Success = true,
                Branches = new List<BranchInfo>()
            };

            var branches = data["branches"] as JArray;
            if (branches != null)
            {
                foreach (var b in branches)
                {
                    result.Branches.Add(new BranchInfo
                    {
                        Id = b["id"]?.Value<int>() ?? 0,
                        // API returns user.name (nested object)
                        Username = (b["user"] as JObject)?["name"]?.Value<string>(),
                        LineCount = b["line_count"]?.Value<int>() ?? 0,
                        HumanCount = b["human_count"]?.Value<int>() ?? 0,
                        AiCount = b["ai_count"]?.Value<int>() ?? 0,
                        ValidatedCount = b["validated_count"]?.Value<int>() ?? 0,
                        UpdatedAt = b["updated_at"]?.Value<string>()
                    });
                }
            }

            return result;
        }

        // ── Publishing ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>POST /translations</c>: what the row became on a 200/201, the site's sentence on
        /// anything else.
        /// </summary>
        public static UploadResult ReadUploadAnswer(int status, JObject data)
        {
            if (status < 200 || status >= 300)
            {
                return new UploadResult { Success = false, Error = UploadError(status, data) };
            }

            // 🔴 **Null here is an ordinary case**: read without the cast, `translation?["role"]`
            // reached an indexer on a JValue and threw.
            var translation = data?["translation"] as JObject;

            return new UploadResult
            {
                Success = true,
                TranslationId = translation?["id"]?.Value<int>() ?? 0,
                FileHash = translation?["file_hash"]?.Value<string>(),
                LineCount = translation?["line_count"]?.Value<int>() ?? 0,
                Role = RoleOf(translation?["role"]?.Value<string>()),
                WebUrl = translation?["web_url"]?.Value<string>(),
                Origin = ReadOrigin(translation?["origin"])
            };
        }

        /// <summary><c>PATCH /translations/{id}/details</c>: done, or the site's reason.</summary>
        public static DetailsResult ReadDetails(int status, JObject data)
        {
            if (status >= 200 && status < 300) return new DetailsResult { Success = true };

            // The site says why in words meant to be shown; falling back to the status code is
            // better than "something went wrong" and worse than what it wrote.
            return new DetailsResult
            {
                Success = false,
                Error = data?["message"]?.Value<string>()
                        ?? data?["error"]?.Value<string>()
                        ?? $"HTTP {status}",
            };
        }

        /// <summary><c>POST /translations/{id}/vote</c>.</summary>
        public static VoteResult ReadVote(int status, JObject data)
        {
            if (status < 200 || status >= 300)
            {
                var error = data?["error"]?.Value<string>() ?? data?["message"]?.Value<string>() ?? $"HTTP {status} {StatusName(status)}";
                return new VoteResult { Success = false, Error = error };
            }

            return new VoteResult
            {
                Success = true,
                VoteCount = data["vote_count"]?.Value<int>() ?? 0,
                UserVote = data["user_vote"]?.Value<int?>()
            };
        }

        // ── Games ───────────────────────────────────────────────────────────────────────────

        /// <summary><c>GET /games</c>: the catalogue's games.</summary>
        public static GameSearchResult ReadGames(JObject data)
        {
            var result = new GameSearchResult { Success = true };
            result.Count = data["count"]?.Value<int>() ?? 0;
            result.Games = new List<GameApiInfo>();

            var games = data["games"] as JArray;
            if (games != null)
            {
                foreach (var g in games)
                {
                    result.Games.Add(new GameApiInfo
                    {
                        Id = g["id"]?.Value<int>() ?? 0,
                        Name = g["name"]?.Value<string>(),
                        Slug = g["slug"]?.Value<string>(),
                        SteamId = g["steam_id"]?.Value<string>(),
                        ImageUrl = g["image_url"]?.Value<string>(),
                        TranslationsCount = g["translations_count"]?.Value<int>() ?? 0
                    });
                }
            }

            return result;
        }

        /// <summary><c>GET /games/search</c>: the catalogue, then the stores.</summary>
        public static GameSearchResult ReadExternalGames(JObject data)
        {
            var result = new GameSearchResult { Success = true };
            result.Count = data["count"]?.Value<int>() ?? 0;
            result.Games = new List<GameApiInfo>();

            var games = data["games"] as JArray;
            if (games != null)
            {
                foreach (var g in games)
                {
                    result.Games.Add(new GameApiInfo
                    {
                        Id = g["id"]?.Value<int>() ?? 0,
                        Name = g["name"]?.Value<string>(),
                        SteamId = g["steam_id"]?.Value<string>(),
                        ImageUrl = g["image_url"]?.Value<string>(),
                        Source = g["source"]?.Value<string>()
                    });
                }
            }

            return result;
        }

        // ── Account ─────────────────────────────────────────────────────────────────────────

        /// <summary><c>GET /me</c>: the short code the site names this access by.</summary>
        public static string ReadAccessCode(JObject data)
        {
            return data?["access_code"]?.Value<string>();
        }

        /// <summary><c>GET /me/translations</c>: every row the account holds, with its side of the lineage.</summary>
        public static MyTranslationsResult ReadMyTranslations(JObject data)
        {
            var result = new MyTranslationsResult { Success = true };
            if (data["translations"] is JArray rows)
            {
                foreach (var row in rows)
                {
                    result.Rows.Add(new LineagePosition
                    {
                        Id = row["id"]?.Value<int>() ?? 0,
                        FileUuid = row["file_uuid"]?.Value<string>(),
                        // The site's word for the side: `main` leads, anything else contributes.
                        IsMain = row["role"]?.Value<string>() == "main",
                    });
                }
            }
            return result;
        }

        /// <summary><c>GET /me/notifications</c>.</summary>
        public static ModNotificationsResult ReadNotifications(JObject data)
        {
            var result = new ModNotificationsResult
            {
                Success = true,
                Unread = data["unread"]?.Value<int>() ?? 0,
                Items = new List<ModNotificationItem>(),
            };

            if (data["items"] is JArray items)
            {
                foreach (var item in items)
                {
                    result.Items.Add(new ModNotificationItem
                    {
                        Id = item["id"]?.ToString(),
                        Type = item["type"]?.ToString(),
                        Text = item["text"]?.ToString(),
                        Url = item["url"]?.ToString(),
                        Uuid = item["uuid"]?.Type == JTokenType.String ? item["uuid"].ToString() : null,
                    });
                }
            }

            return result;
        }

        /// <summary><c>POST /auth/device</c>: the code to show, the page to type it on.</summary>
        public static DeviceFlowInitResult ReadDeviceFlow(JObject data)
        {
            return new DeviceFlowInitResult
            {
                Success = true,
                DeviceCode = data["device_code"]?.Value<string>(),
                UserCode = data["user_code"]?.Value<string>(),
                VerificationUri = data["verification_uri"]?.Value<string>(),
                ExpiresIn = data["expires_in"]?.Value<int>() ?? 900,
                Interval = data["interval"]?.Value<int>() ?? 5
            };
        }

        // ── Sessions ────────────────────────────────────────────────────────────────────────

        /// <summary><c>POST /merge-preview/init</c>, on a 200.</summary>
        public static MergePreviewInitResult ReadMergePreviewInit(JObject data)
        {
            return new MergePreviewInitResult
            {
                Success = true,
                Token = data["token"]?.Value<string>(),
                Url = data["url"]?.Value<string>(),
                ExpiresAt = data["expires_at"]?.Value<string>()
            };
        }

        /// <summary>
        /// <c>GET /merge-preview/{token}/result</c>: the arbitrated lines, serialised again for
        /// the reader that takes a file — or the site's reason.
        /// </summary>
        public static TranslationDownloadResult ReadMergePreviewResult(int status, JObject data, int retryAfterSeconds)
        {
            if (status < 200 || status >= 300)
            {
                return new TranslationDownloadResult { Success = false, Error = DescribeError(status, data, retryAfterSeconds) };
            }

            var content = data?["content"];
            if (content == null || content.Type == JTokenType.Null)
            {
                return new TranslationDownloadResult { Success = false, Error = "Merge result was empty" };
            }

            return new TranslationDownloadResult
            {
                Success = true,
                Content = content.ToString(Newtonsoft.Json.Formatting.None)
            };
        }

        /// <summary><c>POST /edit-session/init</c>, on a 200.</summary>
        public static EditSessionInitResult ReadEditSessionInit(JObject data)
        {
            return new EditSessionInitResult
            {
                Success = true,
                ModKey = data["mod_key"]?.Value<string>(),
                Url = data["url"]?.Value<string>(),
                ExpiresAt = data["expires_at"]?.Value<string>()
            };
        }

        /// <summary>
        /// <c>POST /edit-session/{key}/update</c>. A 404 is the session being GONE, which the
        /// caller must tell from a transient failure that keeps a resumable session on disk.
        /// </summary>
        public static EditSessionUpdateResult ReadEditSessionUpdate(int status, JObject data)
        {
            if (status < 200 || status >= 300)
            {
                return new EditSessionUpdateResult
                {
                    Success = false,
                    SessionGone = status == 404,
                    Error = data?["error"]?.Value<string>() ?? $"HTTP {status} {StatusName(status)}"
                };
            }

            return new EditSessionUpdateResult
            {
                Success = true,
                ContentHash = data["content_hash"]?.Value<string>(),
                BrowserSeenSecondsAgo = data["browser_seen_seconds_ago"]?.Value<int?>(),
                BrowserLeft = data["browser_left"]?.Value<bool>() ?? false
            };
        }

        /// <summary>
        /// <c>GET /edit-session/{key}/state</c>: alive, gone (404), or — any other status — could
        /// not be asked, which is null and never false.
        /// </summary>
        public static EditSessionProbe ReadEditSessionState(int status, JObject data)
        {
            if (status == 404) return new EditSessionProbe { Exists = false };
            if (status < 200 || status >= 300) return new EditSessionProbe { Exists = null };

            return new EditSessionProbe
            {
                Exists = true,
                PendingChanges = data["pending_changes"]?.Value<int>() ?? 0
            };
        }

        // ── The relay's streams (spec/sse-events) ───────────────────────────────────────────

        /// <summary>The device-flow stream's `authorized` payload.</summary>
        public static DeviceAuthorizedEvent ReadDeviceAuthorized(JObject data)
        {
            var user = data["user"] as JObject;
            return new DeviceAuthorizedEvent
            {
                AccessToken = data["access_token"]?.Value<string>(),
                UserId = user?["id"]?.Value<int?>(),
                UserName = user?["name"]?.Value<string>(),
            };
        }

        /// <summary>A stream's `error` payload: the sentence, and the code when the relay named one.</summary>
        public static StreamErrorEvent ReadStreamError(JObject data)
        {
            return new StreamErrorEvent
            {
                Error = data?["error"]?.Value<string>(),
                Code = data?["code"]?.Value<string>(),
            };
        }

        /// <summary>The sync stream's `translation_updated` payload.</summary>
        public static TranslationUpdatedEvent ReadTranslationUpdated(JObject data)
        {
            return new TranslationUpdatedEvent
            {
                FileHash = data["file_hash"]?.Value<string>(),
                LineCount = data["line_count"]?.Value<int>() ?? 0,
                VoteCount = data["vote_count"]?.Value<int>() ?? 0,
            };
        }

        /// <summary>The merge-preview stream's `merge_completed` payload.</summary>
        public static MergeCompletedEvent ReadMergeCompleted(JObject data)
        {
            return new MergeCompletedEvent
            {
                TranslationId = data["translation_id"]?.Value<int?>(),
                FileHash = data["file_hash"]?.Value<string>(),
                LineCount = data["line_count"]?.Value<int>() ?? 0,
                ToLocal = data["destination"]?.Value<string>() == "local",
            };
        }

        /// <summary>The edit-session stream's `edit_saved` payload.</summary>
        public static EditSavedEvent ReadEditSaved(JObject data)
        {
            return new EditSavedEvent
            {
                ContentHash = data["content_hash"]?.Value<string>(),
                LineCount = data["line_count"]?.Value<int>() ?? 0,
                SavedAt = data["saved_at"]?.Value<string>(),
            };
        }

        /// <summary>The edit-session stream's `edit_retranslate` payload.</summary>
        public static EditRetranslateEvent ReadEditRetranslate(JObject data)
        {
            return new EditRetranslateEvent
            {
                Key = data["key"]?.Value<string>(),
                RequestId = data["id"]?.Value<string>(),
            };
        }

        /// <summary>
        /// The sync stream's `state` payload — the same body as <c>GET /sync/state</c> — read into
        /// what the screens hold about the lineage.
        ///
        /// 🔴 **A partial answer must not erase what a full one established.** The payload is
        /// rebuilt from scratch on every event, which was safe while every caller asked for
        /// everything. Since the stream asks for one's own line alone (<c>lineage=0</c>), the
        /// lineage half arrives ABSENT — and absent read as zero wiped the contribution count, the
        /// overlay's notice, and the Main a branch derives from, one second after the startup call
        /// had filled them in correctly. So each lineage field is taken from THIS payload only when
        /// the payload carries it, and kept from <paramref name="previous"/> otherwise: absent means
        /// unknown, never none. That rule is a SEQUENCE, which is why it is held by cases here.
        ///
        /// ⚠ Both the stream and check-uuid arrive here, and both carry the account's token — so
        /// the role is an answer about US (<c>AskedAsAccount</c>), and one's own row is credited
        /// to <paramref name="accountName"/>.
        /// </summary>
        public static ServerTranslationState ReadSyncState(JObject data, ServerTranslationState previous, string accountName)
        {
            bool exists = data["exists"]?.Value<bool>() ?? false;
            LineageRole role = RoleOf(data["role"]?.Value<string>() ?? "none");

            int branchesCount = data["branches_count"] != null
                ? data["branches_count"].Value<int>()
                : (previous?.BranchesCount ?? 0);

            var translation = data["translation"];
            var main = data["main"];

            // ⚠ `as JObject` once, read three times below: `lines_waiting` comes back as JSON
            // null on a lineage with nothing waiting, and `?.` lets a JValue through to an
            // indexer that throws. Third time this trap has been paid for.
            var linesWaiting = data["lines_waiting"] as JObject;

            var serverState = new ServerTranslationState
            {
                Checked = true,
                AskedAsAccount = true,
                Exists = exists,
                IsOwner = role == LineageRole.Main || role == LineageRole.Branch,
                Role = role,
                BranchesCount = branchesCount,
                // Absent from an older site: stays null, which reads as "unknown" and never as
                // "the Main is fine". And kept from the previous state when this payload leaves
                // them out — they describe what became of the MAIN, so they belong to the lineage
                // and a stream does not carry them.
                MainMissing = data["main_missing"] != null
                    ? data["main_missing"].ToObject<bool?>()
                    : previous?.MainMissing,
                MainAbandoned = data["main_abandoned"] != null
                    ? data["main_abandoned"].ToObject<bool?>()
                    : previous?.MainAbandoned,
                MainIgnoring = data["main_ignoring"] != null
                    ? data["main_ignoring"].ToObject<bool?>()
                    : previous?.MainIgnoring,

                // Read at the TOP level, because it is a fact about the lineage — told to the
                // player running somebody else's translation too, who is precisely the person
                // deciding whether to send their corrections back.
                AcceptsBranches = data["accepts_branches"]?.ToObject<bool?>(),

                MergedLinesTotal = data["merged_lines_total"] != null
                    ? (data["merged_lines_total"].ToObject<int?>() ?? 0)
                    : (previous?.MergedLinesTotal ?? 0),

                // Null on an older site, and null means unknown — a zero would claim that
                // nothing is waiting. Kept from the previous state when this payload does not
                // carry them: a stream leaves them out by design.
                BranchesWithWork = data["branches_with_work"] != null
                    ? data["branches_with_work"].ToObject<int?>()
                    : previous?.BranchesWithWork,
                LinesAvailable = data["lines_available"] != null
                    ? data["lines_available"].ToObject<int?>()
                    : previous?.LinesAvailable,
                LinesToReview = linesWaiting != null
                    ? linesWaiting["review"]?.ToObject<int?>()
                    : previous?.LinesToReview,
                LinesNew = linesWaiting != null
                    ? TallyOf(linesWaiting, "new")
                    : (previous?.LinesNew ?? default(TagTally)),
                LinesDiffering = linesWaiting != null
                    ? TallyOf(linesWaiting, "differing")
                    : (previous?.LinesDiffering ?? default(TagTally)),
                LinesOffered = data["lines_offered"] != null
                    ? data["lines_offered"].ToObject<int?>()
                    : previous?.LinesOffered,
            };

            if (translation != null && translation.Type != JTokenType.Null)
            {
                serverState.SiteId = translation["id"]?.Value<int>();
                serverState.Uploader = accountName;
                serverState.Hash = translation["file_hash"]?.Value<string>();
                serverState.Type = translation["type"]?.Value<string>();
                // Read HERE as well as in the upload panel: this is the path the main screen
                // takes at startup, and a card that only learned the status once somebody opened
                // the upload screen would show nothing on the screen that matters.
                serverState.Status = translation["status"]?.Value<string>();
                serverState.Notes = translation["notes"]?.Value<string>();
                serverState.ResourcesUrl = translation["resources_url"]?.Value<string>();
                // The languages this lineage was published with — the payload has carried them
                // all along, and only an upload made from THIS machine used to write them back.
                serverState.SourceLanguage = translation["source_language"]?.Value<string>();
                serverState.TargetLanguage = translation["target_language"]?.Value<string>();

                // Only when the row carries the KEY: a JSON null says "started from nobody",
                // an absent key says an older site that cannot say — and then what the fuller
                // answer established is kept.
                serverState.Origin = translation["origin"] != null
                    ? ReadOrigin(translation["origin"])
                    : previous?.Origin;
                serverState.DownloadCount = translation["download_count"] != null
                    ? translation["download_count"].ToObject<int?>()
                    : previous?.DownloadCount;

                // Only when the row carries it: the lineage answer is already in from the top
                // level above, and an absent key here must not wipe it.
                if (translation["accepts_branches"] != null)
                    serverState.AcceptsBranches = translation["accepts_branches"].ToObject<bool?>();

                serverState.BranchFrozen = translation["branch_frozen"]?.ToObject<bool?>();

                // A branch also hears about the Main it derives from — and keeps it when this
                // payload does not carry it, or "Update from Main" and the owner's name would go
                // off the screen a second after the startup call had put them there.
                if (role == LineageRole.Branch)
                {
                    if (main != null && main.Type != JTokenType.Null)
                    {
                        serverState.MainSiteId = main["id"]?.Value<int>();
                        serverState.MainHash = main["file_hash"]?.Value<string>();
                        serverState.MainLineCount = main["line_count"]?.Value<int>() ?? 0;
                        serverState.MainUsername = main["uploader"]?.Value<string>();
                    }
                    else if (previous != null)
                    {
                        serverState.MainSiteId = previous.MainSiteId;
                        serverState.MainHash = previous.MainHash;
                        serverState.MainLineCount = previous.MainLineCount;
                        serverState.MainUsername = previous.MainUsername;
                    }
                }

                serverState.BranchesPendingReview = data["branches_pending_review"] != null
                    ? data["branches_pending_review"].Value<int>()
                    : (previous?.BranchesPendingReview ?? 0);
            }
            else if (main != null && main.Type != JTokenType.Null)
            {
                serverState.SiteId = main["id"]?.Value<int>();
                serverState.Uploader = main["uploader"]?.Value<string>();
                serverState.MainUsername = main["uploader"]?.Value<string>();
                serverState.Hash = main["file_hash"]?.Value<string>();
                serverState.ResourcesUrl = main["resources_url"]?.Value<string>();

                // Holding somebody's lineage without having published into it is a first-class
                // case, and the Main IS this row: without MainSiteId, MergeFromMain refused and
                // the only way to take in what the Main added was the download that REPLACES.
                serverState.MainSiteId = serverState.SiteId;
                serverState.MainHash = serverState.Hash;
                serverState.MainLineCount = main["line_count"]?.Value<int>() ?? 0;
                serverState.SourceLanguage = main["source_language"]?.Value<string>();
                serverState.TargetLanguage = main["target_language"]?.Value<string>();
            }

            // Votes on the published translation of this lineage. Left null on a server that
            // does not report it: the card then shows no vote at all, rather than "0".
            serverState.Vote = ReadVoteState(data["vote"]);

            return serverState;
        }
    }
}
