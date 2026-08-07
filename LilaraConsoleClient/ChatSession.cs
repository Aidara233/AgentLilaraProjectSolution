using System.Text.Json;

namespace LilaraConsoleClient;

/// <summary>
/// 会话状态 + 命令分发 + 渲染。连接 HttpBridge ws，维护活动频道/发言人/历史对账去重，
/// 把实时 out 帧、历史拉取、本地回显统一成一致的时间线。
/// </summary>
internal sealed class ChatSession
{
    private readonly WsClient ws;
    private readonly HttpApi http;

    private readonly object outputLock = new();
    private readonly object stateLock = new();

    // 已见 bot 出向 seq（== /api/messages 的 platformMessageId），实时流与历史对账去重
    private readonly HashSet<string> knownBotSeq = new(StringComparer.Ordinal);

    // 最近本地发送 key（channel|content），避免历史拉取回显刚发过的消息
    private readonly LinkedList<string> recentSends = new();
    private const int RecentSendsMax = 20;

    // 每频道已拉取到的最大 DB id（增量游标）
    private readonly Dictionary<string, int> lastDbId = new();

    private readonly TaskCompletionSource closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource quitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ActiveChannel { get; private set; }
    public string User { get; private set; }
    public string DisplayName { get; private set; }

    public bool QuitRequested => quitTcs.Task.IsCompleted;
    public Task Closed => closedTcs.Task;
    public Task QuitSignal => quitTcs.Task;

    public ChatSession(WsClient ws, HttpApi http, string channel, string user, string? displayName)
    {
        this.ws = ws;
        this.http = http;
        ActiveChannel = channel;
        User = user;
        DisplayName = displayName ?? user;
        ws.FrameReceived += HandleFrame;
        ws.ConnectionClosed += HandleConnectionClosed;
    }

    public void RequestQuit() => quitTcs.TrySetResult();

    // ── 输出（锁保护，避免收帧线程与主线程写串行） ──

    public void WriteLine(string line)
    {
        lock (outputLock)
        {
            Console.WriteLine(line);
        }
    }

    // ── ws 帧处理 ──

    private void HandleConnectionClosed(Exception? ex)
    {
        closedTcs.TrySetResult();
        WriteLine(ex == null ? "（连接已关闭）" : $"（连接断开：{ex.Message}）");
    }

    private void HandleFrame(JsonElement frame)
    {
        var type = frame.TryGetProperty("type", out var t) ? t.GetString() : "";
        switch (type)
        {
            case "welcome":
                var clientId = frame.TryGetProperty("clientId", out var c) ? c.GetString() : "";
                WriteLine($"✓ 已连接（clientId={clientId}）");
                break;
            case "out":
                HandleOutFrame(frame);
                break;
            case "ack":
                HandleAckFrame(frame);
                break;
            case "pong":
                WriteLine("✓ pong");
                break;
            case "error":
                WriteLine($"✗ 服务器错误：{(frame.TryGetProperty("message", out var m) ? m.GetString() : "")}");
                break;
        }
    }

    private void HandleOutFrame(JsonElement frame)
    {
        var channel = frame.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "";
        var content = frame.TryGetProperty("content", out var ct) ? ct.GetString() ?? "" : "";
        var seq = frame.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
        var time = frame.TryGetProperty("time", out var tm) ? tm.GetString() : null;

        // seq == /api/messages 的 platformMessageId，凭此对账去重（历史拉取不回显重复）
        var key = $"http_{seq:D8}";
        lock (stateLock)
        {
            if (seq == 0 || !knownBotSeq.Add(key)) return;
        }
        var stamp = FormatTime(time);
        WriteLine($"[{stamp}] Lilara · {channel}: {content}");
    }

    private void HandleAckFrame(JsonElement frame)
    {
        var op = frame.TryGetProperty("op", out var o) ? o.GetString() : "";
        var channel = frame.TryGetProperty("channel", out var c) ? c.GetString() : "";
        WriteLine($"✓ ack: {op} {channel}");
    }

    // ── 命令分发 ──

    public async Task DispatchAsync(string rawLine)
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line)) return;

        if (!line.StartsWith('/'))
        {
            await SendMessageAsync(line);
            return;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        var rest = line[cmd.Length..].Trim();

        switch (cmd)
        {
            case "/help": PrintHelp(); break;
            case "/quit": case "/exit": case "/bye": RequestQuit(); break;
            case "/ping": await ws.SendAsync(WsFrame.Ping()); break;
            case "/channels": await ListChannelsAsync(); break;
            case "/join": await JoinAsync(arg); break;
            case "/leave": await LeaveAsync(arg); break;
            case "/history": await ShowHistoryAsync(rest); break;
            case "/status": await ShowStatusAsync(); break;
            case "/user":
                if (string.IsNullOrEmpty(arg)) { WriteLine("用法：/user <名称>"); break; }
                User = arg; DisplayName = arg;
                WriteLine($"✓ 发言人改为 {arg}");
                break;
            case "/display":
                if (string.IsNullOrEmpty(arg)) { WriteLine("用法：/display <名称>"); break; }
                DisplayName = arg;
                WriteLine($"✓ 显示名改为 {arg}");
                break;
            default:
                WriteLine($"未知命令：{cmd}（/help 查看）");
                break;
        }
    }

    // ── 业务动作 ──

    private async Task SendMessageAsync(string content)
    {
        await ws.SendAsync(WsFrame.Send(ActiveChannel, content, User, DisplayName));
        MarkRecentSend(ActiveChannel, content);
        WriteLine($"[{DateTime.Now:HH:mm:ss}] 我 · {ActiveChannel}: {content}");
    }

    private async Task JoinAsync(string channel)
    {
        if (string.IsNullOrEmpty(channel)) { WriteLine("用法：/join <频道>"); return; }
        ActiveChannel = channel;
        await ws.SendAsync(WsFrame.Subscribe(channel));
        WriteLine($"✓ 已切到 {channel}，拉取历史…");
        await LoadHistoryAsync(20, channel);
    }

    private async Task LeaveAsync(string channel)
    {
        if (string.IsNullOrEmpty(channel)) { WriteLine("用法：/leave <频道>"); return; }
        await ws.SendAsync(WsFrame.Unsubscribe(channel));
        WriteLine($"✓ 已退订 {channel}");
    }

    private async Task ShowHistoryAsync(string arg)
    {
        // 支持 "50"（条数）或 "50 123"（条数 + afterId 增量）
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limit = parts.Length > 0 && int.TryParse(parts[0], out var l) ? l : 20;
        var afterId = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;
        await LoadHistoryAsync(limit, ActiveChannel, afterId);
    }

    /// <summary>拉取频道历史并渲染。afterId &gt; 0 时增量查未读。</summary>
    public async Task LoadHistoryAsync(int limit, string? channel = null, int afterId = 0)
    {
        channel ??= ActiveChannel;
        try
        {
            var msgs = await http.GetMessagesAsync(channel, limit, afterId);
            if (msgs.Count == 0)
            {
                WriteLine(afterId > 0 ? $"（{channel} 无新增）" : $"（{channel} 暂无消息）");
                return;
            }
            foreach (var m in msgs)
            {
                // bot 消息凭 platformMessageId 与实时流对账去重
                if (m.IsFromBot && m.PlatformMessageId != null)
                {
                    lock (stateLock)
                    {
                        if (knownBotSeq.Contains(m.PlatformMessageId)) continue;
                    }
                }
                // 本地刚发过的消息不回显
                if (IsRecentSend(channel, m.Content)) continue;

                var stamp = FormatTime(m.Time);
                var sender = m.IsFromBot ? "Lilara" : m.SenderName;
                WriteLine($"[{stamp}] {sender} · {channel}: {m.Content}");
            }
            lastDbId[channel] = msgs[^1].Id;
        }
        catch (Exception ex)
        {
            WriteLine($"✗ 拉取历史失败：{ex.Message}");
        }
    }

    private async Task ListChannelsAsync()
    {
        try
        {
            var channels = await http.GetChannelsAsync();
            if (channels.Count == 0) { WriteLine("（暂无频道）"); return; }
            foreach (var c in channels)
                WriteLine($"  {(c.Name == ActiveChannel ? "●" : "○")} {c.Name}（id={c.Id}）");
        }
        catch (Exception ex) { WriteLine($"✗ 拉取频道失败：{ex.Message}"); }
    }

    private async Task ShowStatusAsync()
    {
        try
        {
            var st = await http.GetStatusAsync();
            if (st?.Adapter == null) { WriteLine("（状态不可用）"); return; }
            WriteLine($"适配器 {st.Adapter.Id}：{st.Adapter.State} 端口={st.Adapter.Port} 默认频道={st.Adapter.DefaultChannel}");
            if (st.Engine != null)
                WriteLine($"引擎：isIdle={st.Engine.IsIdle} busyWorker={st.Engine.BusyWorker}");
        }
        catch (Exception ex) { WriteLine($"✗ 查询状态失败：{ex.Message}"); }
    }

    private void PrintHelp()
    {
        WriteLine("命令：");
        WriteLine("  /help                   本帮助");
        WriteLine("  /quit                   退出（或 Ctrl+C）");
        WriteLine("  /ping                   ws 心跳测试");
        WriteLine("  /channels               列出频道");
        WriteLine("  /join <频道>             订阅并切换到该频道（拉历史）");
        WriteLine("  /leave <频道>            退订频道");
        WriteLine("  /history [条数] [afterId]  拉取当前频道历史（默认最新 20 条；afterId>0 增量查未读）");
        WriteLine("  /status                 适配器 + 引擎状态");
        WriteLine("  /user <名称>             切换发言人（user + 显示名）");
        WriteLine("  /display <名称>          仅改显示名");
        WriteLine("  其他输入 → 直接发送消息（isPrivate=true，立即激活会话）");
    }

    // ── 辅助 ──

    private void MarkRecentSend(string channel, string content)
    {
        var key = $"{channel}|{content}";
        recentSends.Remove(key);
        recentSends.AddFirst(key);
        while (recentSends.Count > RecentSendsMax) recentSends.RemoveLast();
    }

    private bool IsRecentSend(string channel, string content)
        => recentSends.Contains($"{channel}|{content}");

    private static string FormatTime(string? iso)
    {
        if (!string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, out var dto))
            return dto.ToLocalTime().ToString("HH:mm:ss");
        return "";
    }
}
