using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK.Sources;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace Plugin.Email;

/// <summary>
/// 邮件子信源：定时轮询 INBOX，把**新到**的邮件变成事件交给宿主汇集器。
/// <para>与插件的其余部分分工：那些工具是"模型主动去查"（拉），这里是"框架替它盯着"（推）。
/// 同一个连接管理器、同一份配置，但一个消耗 token、一个不消耗。</para>
/// <para>⚠️ 游标必须落盘：只放内存的话，进程重启会把整个收件箱当作"新邮件"重放一遍，
/// 把所有一次性订阅一次性引爆。游标记 UIDVALIDITY + 最后 UID，UIDVALIDITY 变了就整个重置
/// （IMAP 明确允许服务器这么做，此时旧 UID 全部失效）。</para>
/// <para>⚠️ 首轮不投递历史：第一次启动只把当前最大 UID 记成游标，否则装好插件那一刻
/// 会把积压的几百封未读全部当成"刚到的"。</para>
/// </summary>
public sealed class EmailEventSource : IEventSource
{
    private readonly IMailboxSessionFactory _sessions;
    private readonly EmailEventSourceConfig _config;
    private readonly MailCursorStore _cursorStore;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SourcePollingLoop? _pollLoop;
    private DateTimeOffset? _startedAt;
    private long _received;
    private DateTimeOffset? _lastEventAt;

    public EmailEventSource(
        IMailboxSessionFactory sessions,
        EmailEventSourceConfig config,
        string storageDirectory)
    {
        _sessions = sessions;
        _config = config;
        _cursorStore = new MailCursorStore(Path.Combine(storageDirectory, "cursor.json"));
    }

    public string Id => "mail";

    public string Kind => "mail";

    /// <summary>邮件信源提供的载荷键。`preset.watch` 读的就是这里的 `from`。</summary>
    public IReadOnlyList<string> Fields { get; } = new[] { "from", "subject", "uid", "folder" };

    public event Func<SourceEvent, Task>? OnEvent;

    public Task StartAsync(CancellationToken ct = default)
    {
        // 幂等只针对"还活着的循环"。循环已经因为错误死掉时必须允许重建——
        // 否则这个信源永远拉不起来，而外面看起来"已经启动了"。
        if (_runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _startedAt ??= DateTimeOffset.UtcNow;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 首轮 / 游标丢失 / UIDVALIDITY 变化，统一由 MailCursorPolicy 在 PollOnceAsync 里裁决，
        // 所以不再需要"先 prime 再轮询"的两段式——首轮失败也走同一套重试与退避。
        var loop = new SourcePollingLoop(PollOnceAsync, _config.PollSeconds);

        _pollLoop = loop;
        _runTask = Task.Run(() => loop.RunAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            _runTask = null;
            _pollLoop = null;
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：忽略
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception)
            {
                // 循环内部已记录；这里不能让停止流程抛
            }
        }

        _cts.Dispose();
        _cts = null;
        _runTask = null;
        _pollLoop = null;
    }

    public EventSourceStatus GetStatus() => new()
    {
        Id = Id,
        Kind = Kind,
        State = _pollLoop?.State ?? EventSourceState.Stopped,
        StartedAt = _startedAt,
        Received = _received,
        LastEventAt = _lastEventAt,
        LastError = _pollLoop?.LastError,
        LastErrorAt = _pollLoop?.LastErrorAt,
    };

    // ── 轮询 ──

    /// <summary>
    /// 执行一轮检查：取数以获取邮箱头部 → 决策 → 取信 → 落盘 → 投递。
    /// <para>公开出来是因为它本身就是个正当操作（调试、WebUI 手动触发一次），
    /// 同时这样编排逻辑才能被测试——在此之前它埋在 IMAP 调用里，一行都测不了。</para>
    /// <para>失败会向上抛：轮询循环负责捕获并记入 <see cref="GetStatus"/>，
    /// 手动调用者自己处理。</para>
    /// </summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var stored = _cursorStore.Load();

        var events = await _sessions.WithSessionAsync(async session =>
        {
            var head = await session.GetHeadAsync(ct);

            // 游标缺失或 UIDVALIDITY 变了：这一轮只取基线、不投递。
            // 两种情况的处理完全一样，交给决策层统一裁决，不再各写一份。
            if (stored is null || stored.UidValidity != head.UidValidity)
            {
                var reset = MailCursorPolicy.Decide(
                    stored, head.UidValidity, head.MaxUid, Array.Empty<long>());

                _cursorStore.Save(new MailCursor
                {
                    UidValidity = head.UidValidity,
                    LastUid = reset.NewLastUid,
                });

                return new List<SourceEvent>();
            }

            var candidates = await session.ListUidsAfterAsync(stored.LastUid, ct);
            var decision = MailCursorPolicy.Decide(
                stored, head.UidValidity, head.MaxUid, candidates);

            var produced = new List<SourceEvent>(decision.Uids.Count);

            foreach (var uid in decision.Uids)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var message = await session.GetMessageAsync(uid, ct);
                    if (message is not null)
                    {
                        produced.Add(ToSourceEvent(message, uid, head.UidValidity, _config.Folder));
                    }
                }
                catch (Exception)
                {
                    // 单封解析失败不阻断整批：跳过。游标已由决策层越过它，
                    // 否则每轮都会重试同一封、后面的信永远收不到。
                }
            }

            _cursorStore.Save(new MailCursor
            {
                UidValidity = head.UidValidity,
                LastUid = decision.NewLastUid,
            });

            return produced;
        }, ct);

        foreach (var @event in events)
        {
            var handler = OnEvent;
            if (handler is null)
            {
                continue;
            }

            _received++;
            _lastEventAt = DateTimeOffset.UtcNow;

            try
            {
                await handler(@event);
            }
            catch (Exception)
            {
                // 一个订阅抛异常不能打断整批：后面的信还得投，否则一次投递故障会吃掉整轮。
                // 错误由订阅侧自己记录；这里只保证隔离。
            }
        }
    }

    /// <summary>
    /// 邮件 → 事件。
    /// <para>公开为 static 是为了可测：这里产出的就是模型最终看到的内容形状，
    /// 字段映射错了（发件人取了显示名、去重键拼错）不会报错，只会表现为"匹配不上"或"重复唤醒"。</para>
    /// </summary>
    public static SourceEvent ToSourceEvent(MimeMessage message, long uid, uint uidValidity, string? folder)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        var fromText = from is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>";

        return new SourceEvent
        {
            SourceId = "mail",
            SenderId = from?.Address ?? string.Empty,
            Text = ExtractBody(message),
            ReceivedAt = message.Date == default ? DateTimeOffset.UtcNow : message.Date,
            DedupeKey = $"mail:{uidValidity}:{uid}",
            Ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["from"] = fromText,
                ["subject"] = message.Subject ?? string.Empty,
                ["uid"] = uid.ToString(),
                ["folder"] = string.IsNullOrWhiteSpace(folder) ? "INBOX" : folder,
            },
        };
    }

    /// <summary>
    /// 只取纯文本正文；没有纯文本时把 HTML 退化为文本。附件一律不读（那是工具的事）。
    /// <para>公开为 static 是为了可测：正文抽取错了，模型看到的整封信都是错的，而这里没有任何
    /// 网络依赖，没理由不测。</para>
    /// </summary>
    public static string ExtractBody(MimeMessage message)
    {
        var plain = message.TextBody;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            return plain;
        }

        var html = message.HtmlBody;
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return HtmlToText(html);
    }

    /// <summary>
    /// 朴素 HTML → 文本。
    /// <para>⚠️ 必须跳过 <c>script</c> / <c>style</c> 整段：营销邮件几乎都带几百行 CSS，
    /// 只去标签会把它们原样当正文塞进上下文——既白白烧 token，也把真正的内容挤掉。</para>
    /// </summary>
    public static string HtmlToText(string html)
    {
        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            if (html[index] == '<')
            {
                var skipped = TrySkipRawTextElement(html, index);
                if (skipped > index)
                {
                    index = skipped;
                    continue;
                }

                var close = html.IndexOf('>', index);
                if (close < 0)
                {
                    break; // 未闭合标签：丢掉剩余部分，不猜
                }

                index = close + 1;
                continue;
            }

            builder.Append(html[index]);
            index++;
        }

        return DecodeEntities(builder.ToString()).Trim();
    }

    /// <summary>若 <paramref name="start"/> 处是 script/style 开标签，返回其闭合标签之后的位置；否则原样返回。</summary>
    private static int TrySkipRawTextElement(string html, int start)
    {
        foreach (var tag in new[] { "script", "style" })
        {
            if (!StartsWithTag(html, start, tag))
            {
                continue;
            }

            var end = html.IndexOf($"</{tag}", start + 1, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return html.Length; // 没闭合：整段当作代码丢掉
            }

            var close = html.IndexOf('>', end);
            return close < 0 ? html.Length : close + 1;
        }

        return start;
    }

    /// <summary>判断 `&lt;tag` 是否在此处开始，且标签名后面是分隔符（避免 `&lt;scripture` 被当成 script）。</summary>
    private static bool StartsWithTag(string html, int start, string tag)
    {
        if (start + 1 + tag.Length > html.Length)
        {
            return false;
        }

        if (html[start] != '<')
        {
            return false;
        }

        if (string.Compare(html, start + 1, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var after = start + 1 + tag.Length;
        if (after >= html.Length)
        {
            return true;
        }

        return html[after] is '>' or ' ' or '\t' or '\r' or '\n';
    }

    /// <summary>解常见实体。`&amp;` 放最后，避免把 `&amp;lt;` 变成 `&lt;` 之后再解一次。</summary>
    private static string DecodeEntities(string text) => text
        .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
        .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
        .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
        .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
        .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
        .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    // 游标读写见 MailCursorStore（抽出去才能测）

}

/// <summary>邮件信源的轮询配置。</summary>
public sealed class EmailEventSourceConfig
{
    /// <summary>是否把新邮件作为事件推送（关掉则只保留模型主动查询的工具路径）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔秒数（下限 10）。</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>监听的文件夹，默认 INBOX。</summary>
    public string Folder { get; set; } = "INBOX";
}
