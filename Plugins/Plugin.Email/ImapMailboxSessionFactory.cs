using MailKit;
using MailKit.Search;
using MimeKit;

namespace Plugin.Email;

/// <summary>
/// <see cref="IMailboxSessionFactory"/> 的真实实现：用 <see cref="EmailConnectionManager"/>
/// 建连、认证、开文件夹，然后在同一条连接内执行会话工作。
/// <para>MailKit 的全部细节都收在这个文件里。上面那层（<see cref="EmailEventSource"/>）只认
/// <see cref="IMailboxSession"/>，所以它的编排逻辑可以脱网测试。</para>
/// </summary>
public sealed class ImapMailboxSessionFactory : IMailboxSessionFactory
{
    private readonly EmailConnectionManager _connMgr;
    private readonly string _folder;

    public ImapMailboxSessionFactory(EmailConnectionManager connMgr, string? folder)
    {
        _connMgr = connMgr;
        _folder = string.IsNullOrWhiteSpace(folder) ? "INBOX" : folder;
    }

    public Task<T> WithSessionAsync<T>(Func<IMailboxSession, Task<T>> work, CancellationToken ct) =>
        _connMgr.ExecuteImapAsync(async imap =>
        {
            var folder = await imap.GetFolderAsync(_folder, ct);
            return await work(new ImapMailboxSession(folder));
        }, ct);
}

/// <summary>一条已认证连接上的文件夹视图。</summary>
internal sealed class ImapMailboxSession : IMailboxSession
{
    private readonly IMailFolder _folder;

    public ImapMailboxSession(IMailFolder folder) => _folder = folder;

    public async Task<MailboxHead> GetHeadAsync(CancellationToken ct)
    {
        await EnsureOpenAsync(ct);

        // UidNext 是服务器直接给的；比拉全表取最大值便宜得多（几千封的邮箱就是几千个 UID）
        var next = _folder.UidNext;
        if (next is not null)
        {
            return new MailboxHead(_folder.UidValidity, Math.Max(0, (long)next.Value.Id - 1));
        }

        // 服务器没给 UidNext：退回全表（少见，但别因此卡住）
        var all = await _folder.SearchAsync(SearchQuery.All, ct);
        return new MailboxHead(
            _folder.UidValidity,
            all.Count == 0 ? 0 : all.Max(uid => (long)uid.Id));
    }

    public async Task<IReadOnlyList<long>> ListUidsAfterAsync(long afterUid, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);

        var range = new UniqueIdRange(new UniqueId((uint)(afterUid + 1)), UniqueId.MaxValue);
        var summaries = await _folder.FetchAsync(range, MessageSummaryItems.UniqueId, ct);

        return summaries.Select(summary => (long)summary.UniqueId.Id).ToList();
    }

    public async Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        return await _folder.GetMessageAsync(new UniqueId((uint)uid), ct);
    }

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (!_folder.IsOpen)
        {
            await _folder.OpenAsync(FolderAccess.ReadOnly, ct);
        }
    }
}
