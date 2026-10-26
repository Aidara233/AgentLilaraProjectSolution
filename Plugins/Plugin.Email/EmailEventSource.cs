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
    private readonly EmailConnectionManager _connMgr;
    private readonly EmailEventSourceConfig _config;
    private readonly string _cursorPath;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SourcePollingLoop? _pollLoop;
    private DateTimeOffset? _startedAt;
    private long _received;
    private DateTimeOffset? _lastEventAt;

    public EmailEventSource(
        EmailConnectionManager connMgr,
        EmailEventSourceConfig config,
        string storageDirectory)
    {
        _connMgr = connMgr;
        _config = config;
        _cursorPath = Path.Combine(storageDirectory, "cursor.json");
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

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var stored = LoadCursor();

        var events = await _connMgr.ExecuteImapAsync(async imap =>
        {
            var folder = await OpenFolderAsync(imap, ct);
            var baseline = await GetMaxUidAsync(folder, ct);

            // 游标缺失或 UIDVALIDITY 变了：这一轮只取基线、不投递。
            // 两种情况的处理完全一样，所以交给决策层统一裁决，不再各写一份。
            if (stored is null || stored.UidValidity != folder.UidValidity)
            {
                var reset = MailCursorPolicy.Decide(
                    stored, folder.UidValidity, baseline, Array.Empty<long>());

                SaveCursor(new MailCursor
                {
                    UidValidity = folder.UidValidity,
                    LastUid = reset.NewLastUid,
                });

                return new List<SourceEvent>();
            }

            // 只取 UID 大于游标的：FetchAsync + UniqueIdRange，避免搜全表再过滤
            var range = new UniqueIdRange(new UniqueId((uint)(stored.LastUid + 1)), UniqueId.MaxValue);
            var summaries = await folder.FetchAsync(range, MessageSummaryItems.UniqueId, ct);
            var candidates = summaries.Select(summary => (long)summary.UniqueId.Id).ToList();

            var decision = MailCursorPolicy.Decide(stored, folder.UidValidity, baseline, candidates);

            var produced = new List<SourceEvent>(decision.Uids.Count);

            foreach (var value in decision.Uids)
            {
                ct.ThrowIfCancellationRequested();
                var uid = new UniqueId((uint)value);

                try
                {
                    var message = await folder.GetMessageAsync(uid, ct);
                    if (message is not null)
                    {
                        produced.Add(ToSourceEvent(message, uid, folder.UidValidity));
                    }
                }
                catch (Exception)
                {
                    // 单封解析失败不阻断整批：跳过。游标已由决策层越过它，
                    // 否则每轮都会重试同一封、后面的信永远收不到。
                }
            }

            SaveCursor(new MailCursor
            {
                UidValidity = folder.UidValidity,
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
            await handler(@event);
        }
    }

    private Task<IMailFolder> OpenFolderAsync(ImapClient imap, CancellationToken ct)
    {
        var folderPath = string.IsNullOrWhiteSpace(_config.Folder) ? "INBOX" : _config.Folder;
        return imap.GetFolderAsync(folderPath, ct);
    }

    /// <summary>
    /// 当前邮箱的最大 UID（基线）。
    /// <para>用 <c>UidNext - 1</c> 而不是 <c>SearchQuery.All</c>：拉全表在几千封的邮箱上就是几千个 UID，
    /// 只是为了取一个最大值。UidNext 是服务器直接给的。</para>
    /// </summary>
    private static async Task<long> GetMaxUidAsync(IMailFolder folder, CancellationToken ct)
    {
        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        }

        var next = folder.UidNext;
        if (next is not null)
        {
            return Math.Max(0, (long)next.Value.Id - 1);
        }

        // 服务器没给 UidNext：退回全表（少见，但别因此卡住）
        var all = await folder.SearchAsync(SearchQuery.All, ct);
        return all.Count == 0 ? 0 : all.Max(u => (long)u.Id);
    }

    private SourceEvent ToSourceEvent(MimeMessage message, UniqueId uid, uint uidValidity)
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
            DedupeKey = $"mail:{uidValidity}:{uid.Id}",
            Ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["from"] = fromText,
                ["subject"] = message.Subject ?? string.Empty,
                ["uid"] = uid.Id.ToString(),
                ["folder"] = string.IsNullOrWhiteSpace(_config.Folder) ? "INBOX" : _config.Folder,
            },
        };
    }

    /// <summary>只取纯文本正文；HTML 退化为去标签文本。附件一律不读（那是工具的事）。</summary>
    private static string ExtractBody(MimeMessage message)
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

        var builder = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    // ── 游标持久化 ──

    private MailCursor? LoadCursor()
    {
        try
        {
            if (!File.Exists(_cursorPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MailCursor>(File.ReadAllText(_cursorPath));
        }
        catch (Exception)
        {
            // 游标损坏：当作没有，下一次会重新取基线（宁可丢一轮，也不重放历史）
            return null;
        }
    }

    private void SaveCursor(MailCursor cursor)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cursorPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _cursorPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cursor));
            File.Move(temp, _cursorPath, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败不致命：下一轮会重新取（同一次进程内仍以内存中的 maxUid 为准）
        }
    }

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
