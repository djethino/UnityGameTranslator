using System;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Anonymizes values before logging so that users can paste the log into a public
    /// issue tracker without leaking their Windows username, custom AI endpoint, or
    /// account name on our service.
    ///
    /// Defaults preserve enough information for diagnosing problems (path structure,
    /// port, route, hostname class), they just remove the identifiable part. Anything
    /// that goes through these helpers should still be useful in a bug report.
    /// </summary>
    public static class Sanitize
    {
        // Matches both:
        //   C:\Users\Administrator\Desktop\...
        //   /home/jean/.config/...
        // and replaces the username segment with <user>. The trailing slash is captured
        // so we don't have to re-emit it.
        private static readonly Regex WindowsUserPath = new Regex(
            @"(?ix) ( ^|\b ) (?<head> [A-Za-z]:\\Users\\ ) (?<user> [^\\]+ ) (?<tail> \\ )",
            RegexOptions.Compiled);
        private static readonly Regex UnixUserPath = new Regex(
            @"(?ix) ( ^|\b ) (?<head> /home/ ) (?<user> [^/]+ ) (?<tail> / )",
            RegexOptions.Compiled);
        private static readonly Regex MacUserPath = new Regex(
            @"(?ix) ( ^|\b ) (?<head> /Users/ ) (?<user> [^/]+ ) (?<tail> / )",
            RegexOptions.Compiled);

        /// <summary>
        /// Strip the username segment from any user-profile path. Keeps the rest of the
        /// path so the reader can still see what file/directory the mod was looking at.
        ///
        /// Example: "C:\Users\Administrator\Desktop\Forsaken\plugins\fonts\LXG.gen.png"
        ///       → "C:\Users\&lt;user&gt;\Desktop\Forsaken\plugins\fonts\LXG.gen.png"
        /// </summary>
        public static string Path(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string s = path;
            s = WindowsUserPath.Replace(s, "${head}<user>${tail}");
            s = UnixUserPath.Replace(s, "${head}<user>${tail}");
            s = MacUserPath.Replace(s, "${head}<user>${tail}");
            return s;
        }

        /// <summary>
        /// Hide the host of a URL when it isn't a well-known default. Keeps the scheme,
        /// port, and route so we still know "Ollama on port 11434, path /api/chat" — just
        /// not which exact machine.
        ///
        /// Localhost (127.0.0.1, ::1, "localhost") is always shown unredacted: it doesn't
        /// identify anyone and tells us a lot about the user's setup. Our own default
        /// production hosts are also kept in clear so we can tell "stock config" apart
        /// from "user changed it".
        ///
        /// Example:
        ///   http://localhost:11434/api/chat           → unchanged
        ///   http://my-private-llm.acme.tld:8080/api  → http://&lt;host&gt;:8080/api
        ///   https://my-self-hosted-api.example.com/  → https://&lt;host&gt;/
        /// </summary>
        public static string Url(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url; // not parseable, return as-is (already not identifying)

            // Whitelist: clearly non-personal hosts stay readable.
            string h = uri.Host.ToLowerInvariant();
            if (h == "localhost" || h == "127.0.0.1" || h == "::1") return url;
            if (h.EndsWith(".asymptomatik.com")) return url;
            if (h == "asymptomatik.com") return url;

            // Otherwise, hide the host but preserve scheme/port/route.
            string portPart = uri.IsDefaultPort ? "" : $":{uri.Port}";
            string pathAndQuery = uri.PathAndQuery;
            if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery == "/") pathAndQuery = "/";
            return $"{uri.Scheme}://<host>{portPart}{pathAndQuery}";
        }

        /// <summary>
        /// Display a user-account identifier with the first and last character kept and
        /// the middle replaced by a length-preserving placeholder. Enough to tell two
        /// accounts apart in the same log, not enough to identify the human.
        ///
        /// Example: "jsebagh" → "j*****h"   |   "ab" → "a*"   |   "x" → "*"
        /// </summary>
        public static string UserName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length == 1) return "*";
            if (name.Length == 2) return name[0] + "*";
            return name[0] + new string('*', name.Length - 2) + name[name.Length - 1];
        }
    }
}
