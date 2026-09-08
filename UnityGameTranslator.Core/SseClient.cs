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
    /// Represents a parsed SSE event.
    /// </summary>
    public class SseEvent
    {
        public string Id { get; set; }
        public string EventType { get; set; }
        public string Data { get; set; }
    }

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
        private DateTime _lastDataReceived = DateTime.UtcNow;
        private bool _disposed;

        private const int MAX_RECONNECT_DELAY_MS = 30000;
        private const int HEARTBEAT_TIMEOUT_MS = 60000;

        /// <summary>
        /// How often the reader wakes up on a silent stream, only to ask whether
        /// <see cref="HEARTBEAT_TIMEOUT_MS"/> has passed.
        ///
        /// ⚠ Deliberately well under the relay's own heartbeat rather than equal to it. It used to
        /// be fifteen seconds against a fifteen-second beat — see ParseEventStream for what that
        /// cost. It is a tick, not a deadline: nothing is given up when it fires.
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
                        _lastDataReceived = DateTime.UtcNow;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            await ParseEventStream(reader, ct);
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

                // Exponential backoff before trying again. Called a reconnection only when there
                // was a connection to lose — see everConnected above.
                SetState(everConnected
                    ? SseConnectionState.Reconnecting
                    : SseConnectionState.Connecting);
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

        /// <summary>
        /// Parse the SSE event stream line by line, per the SSE specification.
        /// https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation
        /// </summary>
        private async Task ParseEventStream(StreamReader reader, CancellationToken ct)
        {
            string eventType = null;
            var dataLines = new List<string>();
            string eventId = null;

            // 🔴 **ONE read at a time, and that is the whole reason this lives outside the loop.**
            // ReadLineAsync must not be called again while a previous call is still pending on the
            // same stream: .NET answers "The stream is currently in use by a previous operation on
            // the stream", the connection dies, and every reconnection meets the same wall.
            //
            // It WAS called again — every time the tick below won the race, the loop abandoned a
            // pending read and started another. And the tick was fifteen seconds while the relay's
            // heartbeat is also fifteen (sse-server/server.js, HEARTBEAT_INTERVAL_MS), two numbers
            // chosen independently and landing on the same one, so the race was a dead heat and the
            // loser was the connection. What it cost: device-flow authorisation never arrived. The
            // site said the game was linked and the game sat on "Waiting for authorization" for
            // ever, with the reason logged as debug and excused in a comment as normal.
            //
            // ⚠ The tick is now well under the heartbeat, so waking up finds data waiting rather
            // than racing it — but that is comfort, not the fix. The fix is that a tick which wins
            // KEEPS the pending read instead of replacing it.
            Task<string> readTask = null;

            while (!ct.IsCancellationRequested)
            {
                // Check heartbeat timeout
                if ((DateTime.UtcNow - _lastDataReceived).TotalMilliseconds > HEARTBEAT_TIMEOUT_MS)
                {
                    TranslatorCore.LogWarning("[SSE] Heartbeat timeout, reconnecting...");
                    return; // Exit to trigger reconnection
                }

                string line;
                try
                {
                    // Started only when there is no read in flight. ReadLineAsync waits for data,
                    // for the end of the stream, or for the connection to drop.
                    if (readTask == null) readTask = reader.ReadLineAsync();

                    // A tick, so the heartbeat timeout above is reached even on a silent stream.
                    var completedTask = await Task.WhenAny(readTask, Task.Delay(ReadTickMs, ct));
                    if (completedTask != readTask)
                    {
                        // Nothing yet. The read stays pending and is picked up again next time
                        // round — replacing it is what broke this.
                        continue;
                    }

                    // Awaited rather than read through .Result: a faulted read hands back its own
                    // exception here, where the catches below can recognise it, instead of an
                    // AggregateException that matches none of them.
                    line = await readTask;
                    readTask = null;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    return; // Stream closed, will trigger reconnection
                }

                if (line == null)
                {
                    return; // End of stream, will trigger reconnection
                }

                _lastDataReceived = DateTime.UtcNow;

                // Empty line = dispatch event
                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        var evt = new SseEvent
                        {
                            Id = eventId,
                            EventType = eventType ?? "message",
                            Data = string.Join("\n", dataLines)
                        };

                        if (!string.IsNullOrEmpty(eventId))
                        {
                            _lastEventId = eventId;
                        }

                        try
                        {
                            OnEvent?.Invoke(evt);
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogError($"[SSE] Event handler error: {ex.Message}");
                        }

                        // Handed over, and this stream was only ever going to carry the one.
                        if (StopAfterFirstEvent)
                        {
                            _finished = true;
                            return;
                        }
                    }

                    // Reset for next event
                    eventType = null;
                    dataLines.Clear();
                    eventId = null;
                    continue;
                }

                // Comment line (heartbeat)
                if (line[0] == ':')
                {
                    continue;
                }

                // Parse field:value
                int colonIndex = line.IndexOf(':');
                string field, value;
                if (colonIndex >= 0)
                {
                    field = line.Substring(0, colonIndex);
                    value = colonIndex + 1 < line.Length ? line.Substring(colonIndex + 1) : "";
                    // Remove single leading space after colon (per spec)
                    if (value.Length > 0 && value[0] == ' ')
                    {
                        value = value.Substring(1);
                    }
                }
                else
                {
                    field = line;
                    value = "";
                }

                switch (field)
                {
                    case "event":
                        eventType = value;
                        break;
                    case "data":
                        dataLines.Add(value);
                        break;
                    case "id":
                        // Ignore IDs containing null (per spec)
                        if (!value.Contains("\0"))
                        {
                            eventId = value;
                        }
                        break;
                    case "retry":
                        if (int.TryParse(value, out int retryMs) && retryMs >= 0)
                        {
                            _reconnectDelayMs = retryMs;
                        }
                        break;
                }
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
