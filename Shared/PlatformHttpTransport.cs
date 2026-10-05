// The HTTP transport of the IL2CPP adapters: the mod's requests carried by the HttpClient of the
// .NET the loader runs on (BepInEx 6 ships it beside the game; MelonLoader uses the one installed
// on the machine). Linked into both IL2CPP adapters, never into a Mono one.
//
// Why not the mod's own client, as under Mono (UnityGameTranslator.Core/Net/Http): that client
// sends through HttpWebRequest, and on this .NET HttpWebRequest opens a new connection for nearly
// every request — it shares one cached HttpClient for the whole process, and only for requests
// with no cookies, the default proxy and the very same settings as the first one
// (dotnet/runtime, HttpWebRequest.GetCachedOrCreateHttpClient). Every translation would pay a
// connection and a TLS handshake. This runtime is the loader's, not one of the game's libraries:
// what issue #31 protects against does not apply here (analyse/issue-31-bibliotheque-etrangere.md).
//
// The Core speaks only its own types (UnityGameTranslator.Net.Http); this file converts at the
// edge, both ways, and nothing else.

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Platform = System.Net.Http;
using Ugt = UnityGameTranslator.Net.Http;

namespace UnityGameTranslator.Shared
{
    internal sealed class PlatformHttpTransport : Ugt.IHttpTransport
    {
        public Ugt.IHttpConnection Open(Ugt.HttpClientHandler settings) => new Connection(settings);

        private sealed class Connection : Ugt.IHttpConnection
        {
            private readonly Platform.HttpMessageInvoker _invoker;

            public Connection(Ugt.HttpClientHandler settings)
            {
                var handler = new Platform.HttpClientHandler
                {
                    AllowAutoRedirect = settings.AllowAutoRedirect,
                    AutomaticDecompression = settings.AutomaticDecompression,
                    UseCookies = settings.UseCookies,
                    UseProxy = settings.UseProxy,
                };

                if (settings.AllowAutoRedirect)
                    handler.MaxAutomaticRedirections = settings.MaxAutomaticRedirections;

                if (settings.UseCookies)
                    handler.CookieContainer = settings.CookieContainer;

                // Null keeps this runtime's default proxy, as the mod's own handler does.
                if (settings.UseProxy && settings.Proxy != null)
                    handler.Proxy = settings.Proxy;

                _invoker = new Platform.HttpMessageInvoker(handler, disposeHandler: true);
            }

            public void Dispose() => _invoker.Dispose();

            public async Task<Ugt.HttpResponseMessage> SendAsync(Ugt.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var outgoing = new Platform.HttpRequestMessage(new Platform.HttpMethod(request.Method.Method), request.RequestUri)
                {
                    Version = request.Version,
                };

                foreach (var header in request.Headers)
                    outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);

                if (request.Content != null)
                {
                    var body = new BridgedContent(request.Content);
                    foreach (var header in request.Content.Headers)
                        body.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    outgoing.Content = body;
                }

                Platform.HttpResponseMessage answer;
                try
                {
                    answer = await _invoker.SendAsync(outgoing, cancellationToken).ConfigureAwait(false);
                }
                catch (Platform.HttpRequestException e)
                {
                    // The Core catches its own type. The cause stays inside, where Connectivity reads it.
                    throw new Ugt.HttpRequestException("An error occurred while sending the request", e);
                }

                var response = new Ugt.HttpResponseMessage(answer.StatusCode)
                {
                    ReasonPhrase = answer.ReasonPhrase,
                    Version = answer.Version,
                    RequestMessage = request,
                };

                var stream = await answer.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                response.Content = new Ugt.StreamContent(stream, cancellationToken);

                // Each header filed where the Core's own collections put it: a content header is
                // refused by the response's collection and goes to the content's.
                foreach (var header in answer.Headers)
                    File(response, header.Key, header.Value);
                foreach (var header in answer.Content.Headers)
                    File(response, header.Key, header.Value);

                if (answer.RequestMessage?.RequestUri != null)
                    request.RequestUri = answer.RequestMessage.RequestUri;

                return response;
            }

            private static void File(Ugt.HttpResponseMessage response, string name, System.Collections.Generic.IEnumerable<string> values)
            {
                if (!response.Headers.TryAddWithoutValidation(name, values))
                    response.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }

        /// <summary>
        /// The Core's request body, written by the Core's own content: a body it sends in bounded
        /// pieces (ApiClient's stall guard) is still sent that way.
        /// </summary>
        private sealed class BridgedContent : Platform.HttpContent
        {
            private readonly Ugt.HttpContent _inner;

            public BridgedContent(Ugt.HttpContent inner) => _inner = inner;

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => _inner.CopyToAsync(stream);

            protected override bool TryComputeLength(out long length)
            {
                long? known = _inner.Headers.ContentLength;
                length = known ?? 0;
                return known.HasValue;
            }
        }
    }
}
