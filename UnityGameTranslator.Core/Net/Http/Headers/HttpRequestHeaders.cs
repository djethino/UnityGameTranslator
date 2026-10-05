//
// HttpRequestHeaders.cs
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
// UGT: reduced port (see Net/Http/VENDORED.md). Only the typed properties the mod or the
// transports read are kept; every other request header is still accepted by name through
// Add / TryAddWithoutValidation, stored as text.

using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Net.Http.Headers
{
	public sealed class HttpRequestHeaders : HttpHeaders
	{
		bool? expectContinue;

		internal HttpRequestHeaders ()
			: base (HttpHeaderKind.Request)
		{
		}

		public AuthenticationHeaderValue Authorization {
			get {
				return GetValue<AuthenticationHeaderValue> ("Authorization");
			}
			set {
				AddOrRemove ("Authorization", value);
			}
		}

		public HttpHeaderValueCollection<string> Connection {
			get {
				return GetValues<string> ("Connection");
			}
		}

		public bool? ConnectionClose {
			get {
				if (connectionclose == true || Connection.Find (l => string.Equals (l, "close", StringComparison.OrdinalIgnoreCase)) != null)
					return true;

				return connectionclose;
			}
			set {
				if (connectionclose == value)
					return;

				Connection.Remove ("close");
				if (value == true)
					Connection.Add ("close");

				connectionclose = value;
			}
		}

		internal bool ConnectionKeepAlive {
			get {
				return Connection.Find (l => string.Equals (l, "Keep-Alive", StringComparison.OrdinalIgnoreCase)) != null;
			}
		}

		public HttpHeaderValueCollection<NameValueWithParametersHeaderValue> Expect {
			get {
				return GetValues<NameValueWithParametersHeaderValue> ("Expect");
			}
		}

		public bool? ExpectContinue {
			get {
				if (expectContinue.HasValue)
					return expectContinue;

				var found = TransferEncoding.Find (l => string.Equals (l.Value, "100-continue", StringComparison.OrdinalIgnoreCase));
				return found != null ? true : (bool?) null;
			}
			set {
				if (expectContinue == value)
					return;

				Expect.Remove (l => l.Name == "100-continue");

				if (value == true)
					Expect.Add (new NameValueWithParametersHeaderValue ("100-continue"));

				expectContinue = value;
			}
		}

		public string Host {
			get {
				return GetValue<string> ("Host");
			}
			set {
				AddOrRemove ("Host", value);
			}
		}

		public HttpHeaderValueCollection<TransferCodingHeaderValue> TransferEncoding {
			get {
				return GetValues<TransferCodingHeaderValue> ("Transfer-Encoding");
			}
		}

		public bool? TransferEncodingChunked {
			get {
				if (transferEncodingChunked.HasValue)
					return transferEncodingChunked;

				var found = TransferEncoding.Find (l => string.Equals (l.Value, "chunked", StringComparison.OrdinalIgnoreCase));
				return found != null ? true : (bool?) null;
			}
			set {
				if (value == transferEncodingChunked)
					return;

				TransferEncoding.Remove (l => l.Value == "chunked");
				if (value == true)
					TransferEncoding.Add (new TransferCodingHeaderValue ("chunked"));

				transferEncodingChunked = value;
			}
		}

		internal void AddHeaders (HttpRequestHeaders headers)
		{
			foreach (var header in headers) {
				TryAddWithoutValidation (header.Key, header.Value);
			}
		}
	}
}
