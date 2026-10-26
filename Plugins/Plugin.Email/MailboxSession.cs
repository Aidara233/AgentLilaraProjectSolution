using MimeKit;

namespace Plugin.Email;

/// <summary>一次会话看到的邮箱头部信息。</summary>
/// <param name="UidValidity">服务器给这个文件夹的 UIDVALIDITY。变了意味着旧 UID 全部失效。</param>
/// <param name="MaxUid">当前已存在的最大 UID（`UidNext - 1`，空邮箱为 0）。</param>
public sealed record MailboxHead(uint UidValidity, long MaxUid);

/// <summary>
/// 一次 IMAP 连接内的邮箱会话。
/// <para>存在的意义：<c>EmailConnectionManager.ExecuteImapAsync</c> 把 MailKit 的 <c>ImapClient</c>
/// 直接交给回调，而那是具体类 + 真实 socket，伪造不了——于是轮询编排一行都测不了。
/// 换成这个接口之后，<see cref="EmailEventSource"/> 不再认识 MailKit，测试也就能精确制造
/// "UIDVALIDITY 变了""第 2 封解析失败""整个连接失败"这些真实会发生的情况。</para>
/// </summary>
public interface IMailboxSession
{
    /// <summary>取 UIDVALIDITY 与当前最大 UID。基线由它得出。</summary>
    Task<MailboxHead> GetHeadAsync(CancellationToken ct);

    /// <summary>列出 UID 大于 <paramref name="afterUid"/> 的那些消息（只列 UID，不取正文）。</summary>
    Task<IReadOnlyList<long>> ListUidsAfterAsync(long afterUid, CancellationToken ct);

    /// <summary>按 UID 取一封信。取不到返回 null；读失败应当抛。</summary>
    Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken ct);
}

/// <summary>
/// 邮箱会话工厂：**一次连接一个会话**，与原先 <c>ExecuteImapAsync</c> 的生命周期一致
/// （建连 → 认证 → 开文件夹 → 干活 → 断开）。
/// <para>抽成工厂而不是直接给会话，是因为"连接"本身也需要被伪造——
/// 整个会话抛异常（连不上、认证失败）是最常见的失败模式，必须能测。</para>
/// </summary>
public interface IMailboxSessionFactory
{
    /// <summary>在一条连接内执行 <paramref name="work"/>。</summary>
    Task<T> WithSessionAsync<T>(Func<IMailboxSession, Task<T>> work, CancellationToken ct);
}
