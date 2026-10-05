//
// HttpContentHeaders.cs
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
// UGT: reduced port (see Net/Http/VENDORED.md). Content-Encoding, Content-Length and
// Content-Type are the ones the mod and the transports use typed; the others are still
// content headers by name (HttpHeaders.KindOnly), stored as text.

using System.Collections.Generic;

namespace UnityGameTranslator.Net.Http.Headers
{
	public sealed class HttpContentHeaders : HttpHeaders
	{
		readonly HttpContent content;

		internal HttpContentHeaders (HttpContent content)
			: base (HttpHeaderKind.Content)
		{
			this.content = content;
		}

		public ICollection<string> ContentEncoding {
			get {
				return GetValues<string> ("Content-Encoding");
			}
		}

		public long? ContentLength {
			get {
				long? v = GetValue<long?> ("Content-Length");
				if (v != null)
					return v;

				v = content.LoadedBufferLength;
				if (v != null)
					return v;

				long l;
				if (content.TryComputeLength (out l)) {
					// .net compatibility reading value actually set header property value
					SetValue ("Content-Length", l);
					return l;
				}

				return null;
			}
			set {
				AddOrRemove ("Content-Length", value);
			}
		}

		public MediaTypeHeaderValue ContentType {
			get {
				return GetValue<MediaTypeHeaderValue> ("Content-Type");
			}
			set {
				AddOrRemove ("Content-Type", value);
			}
		}
	}
}
