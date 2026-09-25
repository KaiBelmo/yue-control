using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Spotikey;

/// <summary>
/// Tiny WebSocket server on 127.0.0.1 that the Chrome extension connects to. Built on a raw
/// TcpListener (no http.sys / URL ACLs needed). Only accepts connections whose Origin is a
/// Chrome extension, so ordinary web pages cannot drive the player.
/// Messages are JSON text frames; see extension/background.js for the protocol.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    readonly int _port;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    TcpListener? _listener;
    WebSocket? _client;
    int _nextId;

    public int Port => _port;
    public bool IsConnected => _client is { State: WebSocketState.Open };
    public string? LastError { get; private set; }

    /// <summary>Raised on a background thread when the extension connects (true) or drops (false).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Raised on a background thread when the extension pushes a track change.</summary>
    public event Action<JsonObject>? StateReceived;

    public BridgeServer(int port) => _port = port;

    /// <summary>Starts listening. Throws SocketException if the port is taken.</summary>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(PingLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { LastError = ex.Message; await Task.Delay(500); continue; }

            _ = Task.Run(() => HandleClientAsync(tcp));
        }
    }

    async Task HandleClientAsync(TcpClient tcp)
    {
        WebSocket? ws = null;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            var headers = await ReadHttpRequestAsync(stream);
            if (headers == null) return;

            if (!TryBuildHandshake(headers, out string response, out string reason))
            {
                LastError = reason;
                await WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            await WriteAsync(stream, response);
            ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            var previous = Interlocked.Exchange(ref _client, ws);
            previous?.Abort();
            ConnectionChanged?.Invoke(true);

            await ReceiveLoopAsync(ws);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            if (ws != null && Interlocked.CompareExchange(ref _client, null, ws) == ws)
                ConnectionChanged?.Invoke(false);
            ws?.Dispose();
            tcp.Dispose();
        }
    }

    static async Task<Dictionary<string, string>?> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        int length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), timeout.Token);
            if (read == 0) return null;
            length += read;

            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                headers[":request"] = lines.Length > 0 ? lines[0] : "";
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                return headers;
            }

            if (length == buffer.Length) return null; // headers too large
        }
    }

    static bool TryBuildHandshake(Dictionary<string, string> h, out string response, out string reason)
    {
        response = "";
        reason = "";

        if (!h.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Not a WebSocket request";
            return false;
        }
        if (!h.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            reason = "Missing Sec-WebSocket-Key";
            return false;
        }
        h.TryGetValue("Origin", out var origin);
        if (origin == null || !origin.StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            reason = $"Rejected connection from origin '{origin ?? "(none)"}'";
            return false;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
        response = "HTTP/1.1 101 Switching Protocols\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        return true;
    }

    static Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    async Task ReceiveLoopAsync(WebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                break;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                if (message.Length > 4_000_000) break; // absurd
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
                HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    void HandleMessage(string json)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(json) as JsonObject; }
        catch { return; }
        if (obj == null) return;

        string? type = obj["type"]?.GetValue<string>();
        switch (type)
        {
            case "result":
                if (obj["id"] is JsonValue idValue && idValue.TryGetValue<int>(out int id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(obj);
                break;
            case "state":
                StateReceived?.Invoke(obj);
                break;
            // hello / pong: nothing to do
        }
    }

    async Task PingLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); }
            catch (OperationCanceledException) { break; }

            var ws = _client;
            if (ws is { State: WebSocketState.Open })
            {
                try { await SendTextAsync(ws, """{"type":"ping"}"""); }
                catch { /* receive loop will notice */ }
            }
        }
    }

    async Task SendTextAsync(WebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(_cts.Token);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Sends a command to the extension and waits for its reply.</summary>
    public async Task<JsonObject> SendCommandAsync(string action, TimeSpan timeout, JsonObject? args = null)
    {
        var ws = _client;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("Chrome extension is not connected");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject { ["type"] = "command", ["id"] = id, ["action"] = action };
        if (args != null)
            foreach (var kv in args) msg[kv.Key] = kv.Value?.DeepClone();

        try
        {
            await SendTextAsync(ws, msg.ToJsonString());
            using var cts = new CancellationTokenSource(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetException(new TimeoutException("No reply from the Chrome extension")));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _client?.Abort(); } catch { }
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
    }
}
