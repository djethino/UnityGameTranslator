using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// SSE connection state for UI feedback.
    /// </summary>
    public enum SseConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    /// <summary>
    /// Lightweight SSE client for .NET Standard 2.0.
    /// Uses HttpClient with ResponseHeadersRead for streaming.
    /// Implements automatic reconnection with Last-Event-ID and exponential backoff.
    /// </summary>
    public class SseClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private CancellationTokenSource _cts;
        private string _lastEventId;
        private int _reconnectDelayMs = 3000;
        private bool _disposed;

        private const int MAX_RECONNECT_DELAY_MS = 30000;

        /// <summary>
        /// When the next attempt is due, in <see cref="DateTime.UtcNow"/> terms, while the state is
        /// <see cref="SseConnectionState.Disconnected"/> between two tries. Default otherwise.
        ///
        /// 🔴 **Because "Reconnecting…" for six minutes says nothing.** The state used to stay on
        /// Reconnecting through the WAIT as well as through the attempt, so a stream that could not
        /// come back showed one unchanging amber line for as long as the game ran — no way to tell
        /// a slow reconnection from a dead one, and nothing moving to suggest anything was still
        /// being tried. Observed by turning a firewall on.
        ///
        /// ⚠ It does not stop retrying, and that is deliberate: the game is running, the network
        /// comes back, and the periodic channel is the one that gives up (three tries, then the
        /// chosen rhythm). What this adds is honesty about which of the two moments we are in.
        /// </summary>
        public DateTime NextAttemptUtc { get; private set; }

        /// <summary>Seconds until the next attempt, floored at zero. Only meaningful while Disconnected.</summary>
        public int RetryInSeconds =>
            Math.Max(0, (int)Math.Ceiling((NextAttemptUtc - DateTime.UtcNow).TotalSeconds));
        private const int HEARTBEAT_TIMEOUT_MS = 60000;

        /// <summary>
        /// How often the reader wakes up on a silent stream, only to ask whether
        /// <see cref="HEARTBEAT_TIMEOUT_MS"/> has passed.
        ///
        /// ⚠ Deliberately well under the relay's own heartbeat rather than equal to it. It used to
        /// be fifteen seconds against a fifteen-second beat — see <see cref="SseStream"/> for what
        /// that cost. It is a tick, not a deadline: nothing is given up when it fires.
        /// </summary>
        private const int ReadTickMs = 5000;

        /// <summary>Current connection state.</summary>
        public SseConnectionState State { get; private set; } = SseConnectionState.Disconnected;

        /// <summary>
        /// This stream delivers ONE event and is then closed by the server on purpose. Set before
        /// <see cref="Connect"/>; the loop then stops on that event instead of treating the close
        /// as a connection to win back.
        ///
        /// 🔴 **Because a deliberate end and a lost connection look identical from here.** The
        /// device-flow endpoint emits `authorized` (or `expired`, or `error`) and calls res.end()
        /// in the same breath — see sse-server/server.js. The reader saw the stream finish, the
        /// loop announced Reconnecting, and the login panel wrote "Connection lost, reconnecting…"
        /// over the success it was about to show: the account WAS linked, and the mod said it had
        /// lost the connection. Cancelling from the event handler does not fix it — the handler is
        /// queued to the main thread and lands a frame later, by which time the loop has already
        /// spoken.
        ///
        /// ⚠ Off by default: the sync stream is long-lived, and an end there really is a loss.
        /// </summary>
        public bool StopAfterFirstEvent { get; set; }

        /// <summary>Set when <see cref="StopAfterFirstEvent"/> has been honoured, so the loop leaves quietly.</summary>
        private bool _finished;

        /// <summary>Fired when connection state changes. Handler runs on background thread — use RunOnMainThread.</summary>
        public event Action<SseConnectionState> OnStateChanged;

        /// <summary>Fired when an SSE event is received. Handler runs on background thread — use RunOnMainThread.</summary>
        public event Action<SseEvent> OnEvent;

        /// <summary>Fired on permanent (non-retryable) errors. Handler runs on background thread — use RunOnMainThread.</summary>
        public event Action<string> OnError;

        public SseClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// Connect to an SSE endpoint. Non-blocking, runs connection loop on background thread.
        /// </summary>
        /// <param name="url">SSE endpoint URL</param>
        /// <param name="headers">Optional headers (e.g., Authorization)</param>
        public void Connect(string url, Dictionary<string, string> headers = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SseClient));

            Disconnect();
            _finished = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => ConnectLoop(url, headers, token));
        }

        /// <summary>
        /// Disconnect and stop all reconnection attempts.
        /// </summary>
        public void Disconnect()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
            SetState(SseConnectionState.Disconnected);
            _reconnectDelayMs = 3000;
        }

        private async Task ConnectLoop(string url, Dictionary<string, string> headers, CancellationToken ct)
        {
            // ⚠ Whether this attempt is a RECONNECTION, which is not the same question as whether
            // an event has been seen. It was read from _lastEventId, so an attempt that had never
            // reached the server still announced itself as a reconnection — and a screen showing
            // that state writes "Connection lost", about a connection nobody ever had.
            bool everConnected = false;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetState(everConnected
                        ? SseConnectionState.Reconnecting
                        : SseConnectionState.Connecting);

                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Accept", "text/event-stream");

                    // Last-Event-ID for reconnection — server can replay missed events
                    if (!string.IsNullOrEmpty(_lastEventId))
                    {
                        request.Headers.Add("Last-Event-ID", _lastEventId);
                    }

                    // Custom headers (Authorization, etc.)
                    if (headers != null)
                    {
                        foreach (var h in headers)
                        {
                            if (request.Headers.Contains(h.Key))
                                request.Headers.Remove(h.Key);
                            request.Headers.Add(h.Key, h.Value);
                        }
                    }

                    // ResponseHeadersRead: start reading as soon as headers arrive (streaming)
                    using (var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            int statusCode = (int)response.StatusCode;

                            // Non-retryable errors — stop permanently
                            if (statusCode == 401 || statusCode == 403 || statusCode == 404)
                            {
                                var body = "";
                                try { body = await response.Content.ReadAsStringAsync(); } catch { }
                                OnError?.Invoke($"HTTP {statusCode}: {body}");
                                return;
                            }

                            // Retryable server error — will reconnect after backoff
                            throw new HttpRequestException($"HTTP {statusCode}");
                        }

                        // Connected successfully
                        SetState(SseConnectionState.Connected);
                        everConnected = true;
                        _reconnectDelayMs = 3000; // Reset backoff

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            // ⚠ The reading itself is SseStream's — see it for the defect that
                            // separated the two. What stays here is the socket: connecting,
                            // reconnecting, and what the last id and the retry hint are for.
                            var sse = new SseStream(reader)
                            {
                                TickMs = ReadTickMs,
                                HeartbeatTimeoutMs = HEARTBEAT_TIMEOUT_MS,
                                StopAfterFirstEvent = StopAfterFirstEvent,
                                OnEvent = evt => OnEvent?.Invoke(evt),
                                Warning = TranslatorCore.LogWarning,
                            };

                            var stopped = await sse.Run(ct);

                            if (sse.LastEventId != null) _lastEventId = sse.LastEventId;
                            if (sse.RetryDelayMs.HasValue) _reconnectDelayMs = sse.RetryDelayMs.Value;
                            _finished = stopped == SseStopReason.Delivered;
                        }

                        // The stream did what it was opened for. Leaving here rather than falling
                        // through to the backoff is what keeps a deliberate close from being
                        // announced as a loss.
                        if (_finished)
                        {
                            SetState(SseConnectionState.Disconnected);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // Intentional disconnect
                }
                catch (Exception ex)
                {
                    // ⚠ **A warning, and it used to be a debug line excused by its own comment**
                    // ("stream already in use is normal during reconnection, not a real error").
                    // It was not normal and it was not a reconnection artefact: it was this client
                    // dropping its own connection every fifteen seconds, and saying so where
                    // nobody looks — which is why somebody had to read a game log to find out why
                    // signing in never completed. A connection that keeps dying is worth a line
                    // anybody can see.
                    TranslatorCore.LogWarning($"[SSE] Connection lost, will retry: {ex.Message}");
                }

                if (ct.IsCancellationRequested) return;

                // ⚠ **Disconnected while WAITING, not Reconnecting.** The two are different
                // moments and only one of them is an attempt: saying "Reconnecting…" through a
                // thirty-second wait made a dead link and a slow one look identical, for hours.
                // The attempt itself sets Reconnecting at the top of the loop.
                NextAttemptUtc = DateTime.UtcNow.AddMilliseconds(_reconnectDelayMs);
                SetState(SseConnectionState.Disconnected);
                try
                {
                    await Task.Delay(_reconnectDelayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, MAX_RECONNECT_DELAY_MS);
            }
        }

        private void SetState(SseConnectionState state)
        {
            if (State == state) return;
            State = state;

            try
            {
                OnStateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[SSE] StateChanged handler error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
