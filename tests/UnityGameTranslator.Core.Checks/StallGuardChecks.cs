using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A transfer is judged on whether it moves, never on how long it takes: a stream that
    /// trickles for longer than the limit completes, a stream that stalls once for longer than
    /// the limit fails. This is the rule that lets a big file cross a slow link.
    /// </summary>
    internal static class StallGuardChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var limit = TimeSpan.FromMilliseconds(150);

            // Trickles: 12 reads, 40 ms apart — 480 ms in all, three times the limit, no stall.
            var trickle = new StallGuardStream(new PacedStream(pieces: 12, pause: TimeSpan.FromMilliseconds(40)), limit);
            string trickled = null;
            try { trickled = Drain(trickle).GetAwaiter().GetResult(); } catch (Exception e) { trickled = "threw " + e.GetType().Name; }
            check(trickled == "............",
                  "a transfer slower than the limit completes as long as it keeps moving",
                  $"🔴 the old single timeout cut it — got: {trickled}");

            // Stalls: one read that never comes back within the limit.
            var stalled = new StallGuardStream(new PacedStream(pieces: 3, pause: TimeSpan.FromMilliseconds(40), stallAt: 2, stall: TimeSpan.FromMilliseconds(600)), limit);
            Exception failure = null;
            try { Drain(stalled).GetAwaiter().GetResult(); } catch (Exception e) { failure = e; }
            check(failure is TimeoutException,
                  "a transfer that stops for longer than the limit fails",
                  $"a server that stopped answering shows as one read that never completes — got: {failure?.GetType().Name ?? "no failure"}");

            // Sending: pieces, each bounded; a target that stalls fails the same way.
            var sink = new MemoryStream();
            StallGuard.WriteInPieces(sink, new byte[200_000], limit, pieceSize: 64 * 1024).GetAwaiter().GetResult();
            check(sink.Length == 200_000, "a body is sent whole, in pieces", "every piece is one bounded step");

            Exception sendFailure = null;
            try { StallGuard.WriteInPieces(new StallingSink(TimeSpan.FromMilliseconds(600)), new byte[10], limit).GetAwaiter().GetResult(); }
            catch (Exception e) { sendFailure = e; }
            check(sendFailure is TimeoutException, "a send that stops for longer than the limit fails",
                  $"got: {sendFailure?.GetType().Name ?? "no failure"}");
        }

        private static async Task<string> Drain(Stream s)
        {
            var buffer = new byte[16];
            var text = new System.Text.StringBuilder();
            int read;
            while ((read = await s.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0)
                text.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
            return text.ToString();
        }

        /// <summary>One byte per read, a pause before each; one read may stall much longer.</summary>
        private sealed class PacedStream : Stream
        {
            private readonly int _pieces;
            private readonly TimeSpan _pause, _stall;
            private readonly int _stallAt;
            private int _given;

            public PacedStream(int pieces, TimeSpan pause, int stallAt = -1, TimeSpan stall = default)
            {
                _pieces = pieces; _pause = pause; _stallAt = stallAt; _stall = stall;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_given >= _pieces) return 0;
                await Task.Delay(_given == _stallAt ? _stall : _pause, cancellationToken);
                buffer[offset] = (byte)'.';
                _given++;
                return 1;
            }

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class StallingSink : Stream
        {
            private readonly TimeSpan _stall;
            public StallingSink(TimeSpan stall) { _stall = stall; }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.Delay(_stall, cancellationToken);
            public override void Write(byte[] buffer, int offset, int count) => Thread.Sleep(_stall);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
