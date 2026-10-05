//
// HttpClientHandler.cs
//
// Authors:
//	Marek Safar  <marek.safar@gmail.com>
//
// Copyright (C) 2011 Xamarin Inc (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// UGT: upstream HttpClientHandler's sending code (CreateWebRequest, SendAsync,
// CreateResponseMessage), moved behind IHttpTransport. Upstream reached six members INTERNAL to
// System.dll, granted to it as System.dll's friend assembly "System.Net.Http"; merged into the
// mod it is not that friend any more. Each is replaced by public API — never by the runtime's
// access-check bypass the merged DLL happens to carry (Shared/IgnoresAccessChecks*.cs), and never
// by trusting members nobody promised to keep. See Net/Http/VENDORED.md for the list.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityGameTranslator.Net.Http.Headers;

namespace UnityGameTranslator.Net.Http
{
	/// <summary>Requests carried by the runtime's own public <see cref="HttpWebRequest"/> — the Mono transport.</summary>
	public sealed class WebRequestTransport : IHttpTransport
	{
		static long groupCounter;

		/// <summary>
		/// The headers HttpWebRequest refuses in its Headers on .NET Framework and Mono
		/// (WebHeaderCollection.IsRestricted). Written out rather than asked of the runtime: a game's
		/// trimmed System.dll may not carry IsRestricted, and this list has not changed since .NET 2.0.
		/// OwnHttpClientChecks holds it against the runtime's own answer.
		/// </summary>
		internal static readonly HashSet<string> ReservedHeaders = new HashSet<string> (StringComparer.OrdinalIgnoreCase) {
			"Accept", "Connection", "Content-Length", "Content-Type", "Date", "Expect", "Host",
			"If-Modified-Since", "Proxy-Connection", "Range", "Referer", "Transfer-Encoding", "User-Agent",
		};

		public IHttpConnection Open (HttpClientHandler settings)
		{
			return new Connection (settings, "HttpClientHandler" + Interlocked.Increment (ref groupCounter));
		}

		sealed class Connection : IHttpConnection
		{
			readonly HttpClientHandler settings;
			readonly string connectionGroupName;

			// UGT: upstream closed its connection group through the internal
			// ServicePointManager.CloseConnectionGroup(name); the public way is per ServicePoint, so
			// the ones this connection used are remembered.
			readonly HashSet<ServicePoint> servicePoints = new HashSet<ServicePoint> ();
			bool disposed;

			public Connection (HttpClientHandler settings, string connectionGroupName)
			{
				this.settings = settings;
				this.connectionGroupName = connectionGroupName;
			}

			public void Dispose ()
			{
				ServicePoint[] used;
				lock (servicePoints) {
					if (disposed)
						return;
					disposed = true;
					used = servicePoints.ToArray ();
					servicePoints.Clear ();
				}

				foreach (var sp in used)
					sp.CloseConnectionGroup (connectionGroupName);
			}

			HttpWebRequest CreateWebRequest (HttpRequestMessage request)
			{
				// UGT: upstream called HttpWebRequest's internal constructor.
				var wr = WebRequest.CreateHttp (request.RequestUri);

				// UGT: upstream set the internal ThrowOnError = false; SendAsync takes the response out
				// of the WebException instead.

				// UGT: upstream always streamed the body (false) and gave HttpWebRequest the internal
				// ResendContentFactory to send it again after a redirect. Without it a body can only be
				// sent again if HttpWebRequest kept a copy, so it does exactly when redirects are
				// followed. Where they are not — the site's client — the body still streams, which is
				// what lets a large upload be bounded piece by piece (ApiClient.StallGuardHandler).
				wr.AllowWriteStreamBuffering = settings.AllowAutoRedirect;

				wr.ConnectionGroupName = connectionGroupName;
				wr.Method = request.Method.Method;
				wr.ProtocolVersion = request.Version;

				if (wr.ProtocolVersion == HttpVersion.Version10) {
					wr.KeepAlive = request.Headers.ConnectionKeepAlive;
				} else {
					wr.KeepAlive = request.Headers.ConnectionClose != true;
				}

				if (settings.AllowAutoRedirect) {
					wr.AllowAutoRedirect = true;
					wr.MaximumAutomaticRedirections = settings.MaxAutomaticRedirections;
				} else {
					wr.AllowAutoRedirect = false;
				}

				wr.AutomaticDecompression = settings.AutomaticDecompression;

				if (settings.UseCookies) {
					// It cannot be null or allowAutoRedirect won't work
					wr.CookieContainer = settings.CookieContainer;
				}

				if (settings.UseProxy) {
					wr.Proxy = settings.Proxy;
				} else {
					// Disables default WebRequest.DefaultWebProxy value
					wr.Proxy = null;
				}

				var servicePoint = wr.ServicePoint;
				servicePoint.Expect100Continue = request.Headers.ExpectContinue == true;
				lock (servicePoints) {
					servicePoints.Add (servicePoint);
				}

				// Add request headers
				foreach (var header in request.Headers) {
					var values = header.Value;
					if (header.Key == "Host") {
						//
						// Host must be explicitly set for HttpWebRequest
						//
						wr.Host = request.Headers.Host;
						continue;
					}

					if (header.Key == "Transfer-Encoding") {
						//
						// Chunked Transfer-Encoding is set for HttpWebRequest later when Content length is checked
						//
						values = values.Where (l => l != "chunked");
					}

					var values_formated = HttpHeaders.GetSingleHeaderString (header.Key, values);
					if (values_formated == null)
						continue;

					SetHeader (wr, header.Key, values_formated);
				}

				return wr;
			}

			/// <summary>
			/// UGT: upstream wrote every header through the internal WebHeaderCollection.AddInternal,
			/// which skips the check that refuses the headers HttpWebRequest sets itself. Through the
			/// public API those go to their properties; everything else to Headers.Add, which still
			/// refuses a name or value that would break the request line (CR, LF).
			///
			/// ⚠ The headers the mod never sends are set in methods of their own. Mono compiles a
			/// method whole, and a game's trimmed System.dll can lack a member only they name
			/// (HttpWebRequest.Date, AddRange…): inside this method it would fail EVERY header of
			/// every request; in its own, only the request that sends that header.
			/// </summary>
			static void SetHeader (HttpWebRequest wr, string name, string value)
			{
				switch (name.ToLowerInvariant ()) {
				case "accept":
					wr.Accept = value;
					return;
				case "connection":
					SetConnection (wr, value);
					return;
				case "content-length":
					// Set from the content itself, in SendAsync.
					return;
				case "content-type":
					wr.ContentType = value;
					return;
				case "date":
					SetDate (wr, value);
					return;
				case "expect":
					SetExpect (wr, value);
					return;
				case "if-modified-since":
					SetIfModifiedSince (wr, value);
					return;
				case "range":
					SetRange (wr, value);
					return;
				case "referer":
					SetReferer (wr, value);
					return;
				case "transfer-encoding":
					SetTransferEncoding (wr, value);
					return;
				case "user-agent":
					wr.UserAgent = value;
					return;
				default:
					// A reserved header with no case above would be refused by Mono's HttpWebRequest
					// on every request — and accepted by .NET Core's, so nothing outside a game would
					// ever show it. Said here, on every runtime.
					if (ReservedHeaders.Contains (name))
						throw new NotSupportedException ("HttpWebRequest sets the " + name + " header itself, and this transport has no property for it.");

					wr.Headers.Add (name, value);
					return;
				}
			}

			// Keep-Alive and Close are KeepAlive, set from the same header; only other connection
			// options go to the property, which refuses those two.
			static void SetConnection (HttpWebRequest wr, string value)
			{
				var options = Tokens (value).Where (t => !t.Equals ("close", StringComparison.OrdinalIgnoreCase)
					&& !t.Equals ("keep-alive", StringComparison.OrdinalIgnoreCase)).ToList ();
				if (options.Count > 0)
					wr.Connection = string.Join (", ", options);
			}

			static void SetDate (HttpWebRequest wr, string value)
			{
				wr.Date = ParseDate ("Date", value);
			}

			// 100-continue is ServicePoint.Expect100Continue, set from the same header; the property refuses it.
			static void SetExpect (HttpWebRequest wr, string value)
			{
				var expectations = Tokens (value).Where (t => !t.Equals ("100-continue", StringComparison.OrdinalIgnoreCase)).ToList ();
				if (expectations.Count > 0)
					wr.Expect = string.Join (", ", expectations);
			}

			static void SetIfModifiedSince (HttpWebRequest wr, string value)
			{
				wr.IfModifiedSince = ParseDate ("If-Modified-Since", value);
			}

			static void SetReferer (HttpWebRequest wr, string value)
			{
				wr.Referer = value;
			}

			// chunked was taken out by the caller and is SendChunked; another coding needs it as well.
			static void SetTransferEncoding (HttpWebRequest wr, string value)
			{
				wr.SendChunked = true;
				wr.TransferEncoding = value;
			}

			static IEnumerable<string> Tokens (string value)
			{
				return value.Split (',').Select (t => t.Trim ()).Where (t => t.Length > 0);
			}

			static DateTime ParseDate (string name, string value)
			{
				DateTime date;
				if (!DateTime.TryParse (value, CultureInfo.InvariantCulture,
						DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date))
					throw new FormatException ("The " + name + " header is not a date: " + value);

				return date;
			}

			/// <summary>
			/// The one Range form HttpWebRequest can express: a single byte range. Anything else is
			/// refused by name rather than sent as something different.
			/// </summary>
			static void SetRange (HttpWebRequest wr, string value)
			{
				const string unit = "bytes=";
				if (value.StartsWith (unit, StringComparison.OrdinalIgnoreCase)) {
					var range = value.Substring (unit.Length).Trim ();
					int dash = range.IndexOf ('-');
					long from, to;
					if (dash > 0 && range.IndexOf (',') < 0
						&& long.TryParse (range.Substring (0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out from)) {
						var end = range.Substring (dash + 1);
						if (end.Length == 0) {
							wr.AddRange (from);
							return;
						}
						if (long.TryParse (end, NumberStyles.None, CultureInfo.InvariantCulture, out to)) {
							wr.AddRange (from, to);
							return;
						}
					}
				}

				throw new NotSupportedException ("This transport sends a single byte range only, not: " + value);
			}

			HttpResponseMessage CreateResponseMessage (HttpWebResponse wr, HttpRequestMessage requestMessage, CancellationToken cancellationToken)
			{
				var response = new HttpResponseMessage (wr.StatusCode);
				response.RequestMessage = requestMessage;
				response.ReasonPhrase = wr.StatusDescription;
				response.Content = new StreamContent (wr.GetResponseStream (), cancellationToken);

				var headers = wr.Headers;
				for (int i = 0; i < headers.Count; ++i) {
					var key = headers.GetKey(i);
					var value = headers.GetValues (i);

					HttpHeaders item_headers;
					if (HttpHeaders.GetKnownHeaderKind (key) == Headers.HttpHeaderKind.Content)
						item_headers = response.Content.Headers;
					else
						item_headers = response.Headers;

					item_headers.TryAddWithoutValidation (key, value);
				}

				requestMessage.RequestUri = wr.ResponseUri;

				return response;
			}

			static bool MethodHasBody (HttpMethod method)
			{
				switch (method.Method) {
				case "HEAD":
				case "GET":
				case "MKCOL":
				case "CONNECT":
				case "TRACE":
					return false;
				default:
					return true;
				}
			}

			public async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
			{
				if (disposed)
					throw new ObjectDisposedException (GetType ().ToString ());

				var wrequest = CreateWebRequest (request);
				HttpWebResponse wresponse = null;

				try {
					using (cancellationToken.Register (l => ((HttpWebRequest)l).Abort (), wrequest)) {
						var content = request.Content;
						if (content != null) {
							foreach (var header in content.Headers) {
								foreach (var value in header.Value) {
									SetHeader (wrequest, header.Key, value);
								}
							}

							if (request.Headers.TransferEncodingChunked == true) {
								wrequest.SendChunked = true;
							} else {
								//
								// Content length has to be set because HttpWebRequest is running without buffering
								//
								var contentLength = content.Headers.ContentLength;
								if (contentLength != null) {
									wrequest.ContentLength = contentLength.Value;
								} else {
									if (settings.MaxRequestContentBufferSize == 0)
										throw new InvalidOperationException ("The content length of the request content can't be determined. Either set TransferEncodingChunked to true, load content into buffer, or set MaxRequestContentBufferSize.");

									await content.LoadIntoBufferAsync (settings.MaxRequestContentBufferSize).ConfigureAwait (false);
									wrequest.ContentLength = content.Headers.ContentLength.Value;
								}
							}

							using (var stream = await wrequest.GetRequestStreamAsync ().ConfigureAwait (false)) {
								await request.Content.CopyToAsync (stream).ConfigureAwait (false);
							}
						} else if (MethodHasBody (request.Method)) {
							// Explicitly set this to make sure we're sending a "Content-Length: 0" header.
							// This fixes the issue that's been reported on the forums:
							// http://forums.xamarin.com/discussion/17770/length-required-error-in-http-post-since-latest-release
							wrequest.ContentLength = 0;
						}

						try {
							wresponse = (HttpWebResponse)await wrequest.GetResponseAsync ().ConfigureAwait (false);
						} catch (WebException we) when (we.Response is HttpWebResponse) {
							// UGT: what upstream's ThrowOnError = false gave as a plain response — a server
							// that answered with an error status has answered.
							wresponse = (HttpWebResponse) we.Response;
						}
					}
				} catch (WebException) when (cancellationToken.IsCancellationRequested) {
					// UGT: our own abort (the token, registered above) — told below as a cancelled
					// task. Upstream tested the status, RequestCanceled; the token says whose abort.
				} catch (WebException we) {
					throw new HttpRequestException ("An error occurred while sending the request", we);
				} catch (System.IO.IOException ex) {
					throw new HttpRequestException ("An error occurred while sending the request", ex);
				}

				if (cancellationToken.IsCancellationRequested) {
					var cancelled = new TaskCompletionSource<HttpResponseMessage> ();
					cancelled.SetCanceled ();
					return await cancelled.Task;
				}

				return CreateResponseMessage (wresponse, request, cancellationToken);
			}
		}
	}
}
