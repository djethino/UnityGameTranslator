using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Net.Http
{
    /// <summary>
    /// What actually carries a request: <see cref="HttpClientHandler"/> holds the settings, a
    /// transport opens a connection with them and sends.
    ///
    /// 🔴 **Why the mod has its own HTTP client at all** (issue #31, user decision 2026-10-05). The
    /// mod used to borrow the game's `System.Net.Http.dll`. A game can ship one that does not work
    /// on its own runtime — a Microsoft .NET Framework copy calling `System.Net.Logging.get_On`,
    /// which Mono's `System.dll` never had — and every request died. Replacing the game's library
    /// would change what the GAME loads, which is not ours to change ("imagine it is a crack meant
    /// to keep the game offline, and we open it again"). So the mod carries its own client and
    /// touches nothing of the game's.
    ///
    /// ⚠ **One transport per runtime, chosen by the adapter** (<c>IModLoaderAdapter.HttpTransport</c>):
    /// under Mono, <see cref="WebRequestTransport"/> over the runtime's public `HttpWebRequest`;
    /// under IL2CPP the mod runs on the loader's own .NET (never the game's libraries), whose
    /// `HttpClient` keeps connections open — `HttpWebRequest` there opens a new connection per
    /// request (dotnet/runtime, `HttpWebRequest.GetCachedOrCreateHttpClient`).
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>
        /// A connection for one handler, its settings fixed from now on
        /// (<see cref="HttpClientHandler"/> refuses changes once a request has left).
        /// </summary>
        IHttpConnection Open(HttpClientHandler settings);
    }

    /// <summary>What a transport opened for one handler; disposed with it.</summary>
    public interface IHttpConnection : IDisposable
    {
        /// <summary>
        /// Send one request. A server's error status is an answer, never an exception; a request
        /// that never got one throws <see cref="HttpRequestException"/> with the cause inside, and
        /// a cancelled one ends cancelled.
        /// </summary>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }

    /// <summary>Which transport this process uses — set once, by the core, from the adapter.</summary>
    public static class HttpTransport
    {
        private static IHttpTransport _current;

        /// <summary>
        /// The transport of this runtime.
        ///
        /// ⚠ No default: a request sent before the adapter named one is a startup-order defect, and
        /// it is said rather than carried by a transport that might be the wrong one.
        /// </summary>
        public static IHttpTransport Current
        {
            get => _current ?? throw new InvalidOperationException(
                "No HTTP transport yet: the mod loader adapter names one when the mod starts.");
            set => _current = value ?? throw new ArgumentNullException(nameof(value));
        }
    }
}
