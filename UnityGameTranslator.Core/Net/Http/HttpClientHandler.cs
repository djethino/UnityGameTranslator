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
// UGT: split from upstream (see Net/Http/VENDORED.md). Upstream's handler both held the settings
// and drove HttpWebRequest; here it holds the settings only — the ones the mod sets, with
// upstream's defaults and checks — and sends through the transport of this runtime
// (HttpTransport.Current). Upstream's sending code is WebRequestTransport.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Net.Http
{
	public class HttpClientHandler : HttpMessageHandler
	{
		bool allowAutoRedirect;
		DecompressionMethods automaticDecompression;
		CookieContainer cookieContainer;
		int maxAutomaticRedirections;
		long maxRequestContentBufferSize;
		IWebProxy proxy;
		bool useCookies;
		bool useProxy;
		bool sentRequest;
		bool disposed;

		IHttpConnection connection;
		readonly object connectionLock = new object ();

		public HttpClientHandler ()
		{
			allowAutoRedirect = true;
			maxAutomaticRedirections = 50;
			maxRequestContentBufferSize = int.MaxValue;
			useCookies = true;
			useProxy = true;
		}

		internal void EnsureModifiability ()
		{
			if (sentRequest)
				throw new InvalidOperationException (
					"This instance has already started one or more requests. " +
					"Properties can only be modified before sending the first request.");
		}

		public bool AllowAutoRedirect {
			get {
				return allowAutoRedirect;
			}
			set {
				EnsureModifiability ();
				allowAutoRedirect = value;
			}
		}

		public DecompressionMethods AutomaticDecompression {
			get {
				return automaticDecompression;
			}
			set {
				EnsureModifiability ();
				automaticDecompression = value;
			}
		}

		public CookieContainer CookieContainer {
			get {
				return cookieContainer ?? (cookieContainer = new CookieContainer ());
			}
			set {
				EnsureModifiability ();
				cookieContainer = value;
			}
		}

		public int MaxAutomaticRedirections {
			get {
				return maxAutomaticRedirections;
			}
			set {
				EnsureModifiability ();
				if (value <= 0)
					throw new ArgumentOutOfRangeException ();

				maxAutomaticRedirections = value;
			}
		}

		public long MaxRequestContentBufferSize {
			get {
				return maxRequestContentBufferSize;
			}
			set {
				EnsureModifiability ();
				if (value < 0)
					throw new ArgumentOutOfRangeException ();

				maxRequestContentBufferSize = value;
			}
		}

		public IWebProxy Proxy {
			get {
				return proxy;
			}
			set {
				EnsureModifiability ();
				if (!UseProxy)
					throw new InvalidOperationException ();

				proxy = value;
			}
		}

		public bool UseCookies {
			get {
				return useCookies;
			}
			set {
				EnsureModifiability ();
				useCookies = value;
			}
		}

		public bool UseProxy {
			get {
				return useProxy;
			}
			set {
				EnsureModifiability ();
				useProxy = value;
			}
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				Volatile.Write (ref disposed, true);
				lock (connectionLock) {
					connection?.Dispose ();
					connection = null;
				}
			}

			base.Dispose (disposing);
		}

		protected internal override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (disposed)
				throw new ObjectDisposedException (GetType ().ToString ());

			Volatile.Write (ref sentRequest, true);

			IHttpConnection open;
			lock (connectionLock) {
				if (connection == null)
					connection = HttpTransport.Current.Open (this);
				open = connection;
			}

			return open.SendAsync (request, cancellationToken);
		}
	}
}
