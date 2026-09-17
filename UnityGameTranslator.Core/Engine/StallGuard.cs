using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A transfer is judged on whether it is MOVING, never on how long it takes as a whole.
    ///
    /// 🔴 One limit on the whole request was the silent failure of every big file: 30 s covered
    /// the headers, the upload of the body and the download of the answer together, so a
    /// translation of a few megabytes gzipped could not be published from a slow uplink, and the
    /// error read as a network fault. What a limit is for is a server that stopped answering —
    /// and that shows as a single read or write that never completes. So the limit applies to
    /// each step: a step that stalls longer than it fails, however many steps there are.
    ///
    /// Pure: no Unity, no HTTP — the Core.Checks replay a stalling stream and a trickling one.
    /// </summary>
    public static class StallGuard
    {
        /// <summary>
        /// The step, or a TimeoutException once <paramref name="limit"/> passes with the step
        /// still pending. The abandoned step keeps running; whoever owns the connection closes it
        /// on the exception, which is what ends it.
        /// </summary>
        public static async Task<T> Bounded<T>(Task<T> step, TimeSpan limit, string what)
        {
            var done = await Task.WhenAny(step, Task.Delay(limit)).ConfigureAwait(false);
            if (done != step) throw new TimeoutException($"{what}: nothing moved for {limit.TotalSeconds:0} s");
            return await step.ConfigureAwait(false);
        }

        /// <inheritdoc cref="Bounded{T}"/>
        public static async Task Bounded(Task step, TimeSpan limit, string what)
        {
            var done = await Task.WhenAny(step, Task.Delay(limit)).ConfigureAwait(false);
            if (done != step) throw new TimeoutException($"{what}: nothing moved for {limit.TotalSeconds:0} s");
            await step.ConfigureAwait(false);
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> in pieces, each piece bounded: a body sent as one write
        /// is one step, and one step is exactly what the limit cannot see inside.
        /// </summary>
        public static async Task WriteInPieces(Stream target, byte[] bytes, TimeSpan limit, int pieceSize = 64 * 1024)
        {
            for (int at = 0; at < bytes.Length; at += pieceSize)
            {
                int count = Math.Min(pieceSize, bytes.Length - at);
                await Bounded(target.WriteAsync(bytes, at, count), limit, "sending").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A read-only stream whose every asynchronous read is bounded (see <see cref="StallGuard"/>).
    /// Synchronous reads pass through unguarded: the client buffers and the download loop both
    /// read asynchronously, and a blocking read has no way to be abandoned.
    /// </summary>
    public sealed class StallGuardStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _limit;

        public StallGuardStream(Stream inner, TimeSpan limit)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _limit = limit;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => StallGuard.Bounded(_inner.ReadAsync(buffer, offset, count, cancellationToken), _limit, "receiving");

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
