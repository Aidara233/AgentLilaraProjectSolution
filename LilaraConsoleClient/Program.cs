namespace LilaraConsoleClient;

/// <summary>
/// Lilara 迷你客户端 — 连接 HttpBridge 适配器的 ws 协议，交互式对话。
/// 用法：LilaraConsoleClient [--host localhost] [--port 9876] [--channel http_debug]
///        [--user http-user] [--display-name <名>] [--token <token>] [--history-limit 20]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var (host, port, channel, user, displayName, token, historyLimit) = ParseArgs(args);

        var wsUri = new UriBuilder("ws", host, port, "/ws")
        {
            Query = BuildQuery(("channel", channel), ("token", token))
        }.Uri;

        await using var ws = new WsClient();
        var http = new HttpApi(host, port, token);

        try
        {
            await ws.ConnectAsync(wsUri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ 无法连接 {wsUri}：{ex.Message}");
            return 1;
        }
        ws.StartReceiveLoop();

        var session = new ChatSession(ws, http, channel, user, displayName);
        session.WriteLine($"Lilara 迷你客户端 — 已连接 {wsUri}（频道 {channel}）");
        session.WriteLine("输入消息直接发送；/help 查看命令；Ctrl+C 退出。");

        if (historyLimit > 0)
            await session.LoadHistoryAsync(historyLimit);

        // Ctrl+C 优雅退出（e.Cancel=true 阻止进程直接死亡，走正常断开）
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            session.RequestQuit();
        };

        // REPL：读一行输入，同时监听"连接关闭 / 退出请求"以打断阻塞读
        while (true)
        {
            Console.Write("> ");
            var readTask = Console.In.ReadLineAsync();
            var done = await Task.WhenAny(readTask, session.Closed, session.QuitSignal);
            if (done != readTask) break; // 连接关闭或请求退出
            var line = await readTask;
            if (line == null) break;     // stdin EOF
            await session.DispatchAsync(line);
        }

        session.WriteLine("（正在断开…）");
        return 0;
    }

    private static (string host, int port, string channel, string user, string? displayName, string? token, int historyLimit)
        ParseArgs(string[] args)
    {
        string host = "localhost";
        int port = 9876;
        string channel = "http_debug";
        string user = "http-user";
        string? displayName = null;
        string? token = null;
        int historyLimit = 20;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": if (i + 1 < args.Length) host = args[++i]; break;
                case "--port": if (i + 1 < args.Length && int.TryParse(args[++i], out var p)) port = p; break;
                case "--channel": if (i + 1 < args.Length) channel = args[++i]; break;
                case "--user": if (i + 1 < args.Length) user = args[++i]; break;
                case "--display-name": if (i + 1 < args.Length) displayName = args[++i]; break;
                case "--token": if (i + 1 < args.Length) token = args[++i]; break;
                case "--history-limit": if (i + 1 < args.Length && int.TryParse(args[++i], out var h)) historyLimit = h; break;
                default: break; // 忽略未知参数
            }
        }
        return (host, port, channel, user, displayName, token, historyLimit);
    }

    private static string BuildQuery(params (string key, string? value)[] pairs)
    {
        var parts = pairs
            .Where(p => !string.IsNullOrEmpty(p.value))
            .Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value!)}");
        return string.Join("&", parts);
    }
}
