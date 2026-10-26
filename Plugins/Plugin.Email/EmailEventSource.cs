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
    private Task? _loop;
    private EventSourceState _state = EventSourceState.Stopped;
    private DateTimeOffset? _startedAt;
    private long _received;
    private DateTimeOffset? _lastEventAt;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

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

    public event Func<SourceEvent, Task>? OnEvent;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _state = EventSourceState.Starting;
        _startedAt = DateTimeOffset.UtcNow;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
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

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
        _state = EventSourceState.Stopped;
    }

    public EventSourceStatus GetStatus() => new()
    {
        Id = Id,
        Kind = Kind,
        State = _state,
        StartedAt = _startedAt,
        Received = _received,
        LastEventAt = _lastEventAt,
        LastError = _lastError,
        LastErrorAt = _lastErrorAt,
    };

    // ── 轮询循环 ──

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // 首次进入：若还没有游标，只记基线，不投递历史
        try
        {
            await PrimeCursorIfMissingAsync(ct);
            _state = EventSourceState.Running;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
                _state = EventSourceState.Running;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _config.PollSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RecordError(Exception ex)
    {
        _state = EventSourceState.Error;
        _lastError = ex.Message;
        _lastErrorAt = DateTimeOffset.UtcNow;
    }

    /// <summary>没有游标时，把当前最大 UID 记成基线（不投递）。</summary>
    private async Task PrimeCursorIfMissingAsync(CancellationToken ct)
    {
        if (File.Exists(_cursorPath))
        {
            return;
        }

        await _connMgr.ExecuteImapAsync(async imap =>
        {
            var folder = await OpenFolderAsync(imap, ct);
            var cursor = new MailCursor
            {
                UidValidity = folder.UidValidity,
                LastUid = await GetMaxUidAsync(folder, ct),
            };

            SaveCursor(cursor);
            return true;
        }, ct);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var cursor = LoadCursor();
        if (cursor is null)
        {
            await PrimeCursorIfMissingAsync(ct);
            return;
        }

        var events = await _connMgr.ExecuteImapAsync(async imap =>
        {
            var folder = await OpenFolderAsync(imap, ct);

            // UIDVALIDITY 变了 → 旧 UID 全部失效，重新取基线（丢掉这一轮，不投递）
            if (folder.UidValidity != cursor.UidValidity)
            {
                var reset = new MailCursor
                {
                    UidValidity = folder.UidValidity,
                    LastUid = await GetMaxUidAsync(folder, ct),
                };
                SaveCursor(reset);
                return new List<SourceEvent>();
            }

            // 只取 UID 大于游标的：用 FetchAsync + UniqueIdRange，避免搜全表再过滤
            var range = new UniqueIdRange(new UniqueId((uint)(cursor.LastUid + 1)), UniqueId.MaxValue);
            var summaries = await folder.FetchAsync(range, MessageSummaryItems.UniqueId, ct);
            if (summaries.Count == 0)
            {
                return new List<SourceEvent>();
            }

            var produced = new List<SourceEvent>(summaries.Count);
            var maxUid = cursor.LastUid;

            foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
            {
                ct.ThrowIfCancellationRequested();
                var uid = summary.UniqueId;

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
                    // 单封解析失败不阻断整批：跳过，游标照常前进（否则会永久卡在这一封）
                }

                maxUid = Math.Max(maxUid, uid.Id);
            }

            SaveCursor(new MailCursor { UidValidity = folder.UidValidity, LastUid = maxUid });
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

    private static async Task<long> GetMaxUidAsync(IMailFolder folder, CancellationToken ct)
    {
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

    private sealed class MailCursor
    {
        public uint UidValidity { get; set; }

        public long LastUid { get; set; }
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
