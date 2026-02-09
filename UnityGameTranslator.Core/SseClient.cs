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

        /// <summary>Current connection state.</summary>
        public SseConnectionState State { get; private set; } = SseConnectionState.Disconnected;

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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetState(_lastEventId == null
                        ? SseConnectionState.Connecting
                        : SseConnectionState.Reconnecting);

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
                        _reconnectDelayMs = 3000; // Reset backoff
                        _lastDataReceived = DateTime.UtcNow;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            await ParseEventStream(reader, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // Intentional disconnect
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[SSE] Connection error: {ex.Message}");
                }

                if (ct.IsCancellationRequested) return;

                // Exponential backoff before reconnection
                SetState(SseConnectionState.Reconnecting);
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
                    // ReadLineAsync will block until data arrives or stream closes
                    var readTask = reader.ReadLineAsync();

                    // Use a timeout so we can check heartbeat periodically
                    var completedTask = await Task.WhenAny(readTask, Task.Delay(15000, ct));
                    if (completedTask != readTask)
                    {
                        // Timeout — no data in 15s, loop to check heartbeat timeout
                        continue;
                    }

                    line = readTask.Result;
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
