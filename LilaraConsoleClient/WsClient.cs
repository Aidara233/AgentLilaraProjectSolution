using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LilaraConsoleClient;

/// <summary>
/// HttpBridge 适配器 ws 协议层：封装 ClientWebSocket，负责连接、发帧、后台收帧循环。
/// 协议帧见 docs/http-bridge-api.md §3，服务端实现对照 AgentCoreProcessor/Adapter/Http/HttpBridgeAdapter.cs。
/// </summary>
internal sealed class WsClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private CancellationTokenSource? loopCts;
    private Task? receiveLoop;

    /// <summary>收到一帧 JSON（welcome/out/ack/pong/error）。调用方勿在其中阻塞太久。</summary>
    public event Action<JsonElement>? FrameReceived;

    /// <summary>连接意外关闭（对端断开/网络错误）。参数为异常或 null（正常 close 帧）。</summary>
    public event Action<Exception?>? ConnectionClosed;

    public WebSocketState State => socket.State;

    public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(uri, ct);
    }

    /// <summary>启动后台收帧循环。调用前必须已 ConnectAsync 成功。</summary>
    public void StartReceiveLoop()
    {
        loopCts = new CancellationTokenSource();
        receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        var token = loopCts!.Token;
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                // 拼接分片消息（与服务端 HandleWebSocketAsync 同款 MemoryStream 循环）
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        ConnectionClosed?.Invoke(null);
                        return;
                    }
                    if (result.Count > 0) ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrWhiteSpace(text)) continue;
                var frame = JsonDocument.Parse(text).RootElement.Clone();
                FrameReceived?.Invoke(frame);
            }
            ConnectionClosed?.Invoke(null);
        }
        catch (OperationCanceledException)
        {
            // 主动关闭路径：CloseAsync 先取消本循环，不触发断开通知
        }
        catch (Exception ex)
        {
            ConnectionClosed?.Invoke(ex);
        }
    }

    /// <summary>发一帧。frame 为 {type, ...} 字典，由 WsFrame 静态工厂构造。</summary>
    public async Task SendAsync(Dictionary<string, object?> frame, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new InvalidOperationException("连接已关闭，无法发送");
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally { sendLock.Release(); }
    }

    public async Task CloseAsync()
    {
        loopCts?.Cancel(); // 先取消收帧循环，避免关闭 socket 时 ReceiveAsync 抛"invalid state"假警报
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        socket.Dispose();
        if (receiveLoop != null)
        {
            try { await receiveLoop; } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        sendLock.Dispose();
        loopCts?.Dispose();
    }
}

/// <summary>协议帧静态工厂：统一构造 C→S 帧字典。</summary>
internal static class WsFrame
{
    public static Dictionary<string, object?> Ping() => new() { ["type"] = "ping" };

    public static Dictionary<string, object?> Subscribe(string channel) =>
        new() { ["type"] = "subscribe", ["channel"] = channel };

    public static Dictionary<string, object?> Unsubscribe(string channel) =>
        new() { ["type"] = "unsubscribe", ["channel"] = channel };

    /// <summary>发消息。isPrivate=true 缺省视同提及 → 立即激活会话（对齐服务端 defaultIsPrivate）。</summary>
    public static Dictionary<string, object?> Send(
        string channel, string content, string user, string? displayName, bool isMentioned = false, bool isPrivate = true) =>
        new()
        {
            ["type"] = "send",
            ["channel"] = channel,
            ["content"] = content,
            ["user"] = user,
            ["displayName"] = displayName ?? user,
            ["isMentioned"] = isMentioned,
            ["isPrivate"] = isPrivate
        };
}
