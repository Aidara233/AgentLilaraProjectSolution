using MimeKit;
using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// H3：邮件 → 事件的字段映射。
/// <para>这里产出的就是模型最终看到的内容形状。映射错了不会报错，只会表现为
/// "订阅永远匹配不上"（from 取错）或"同一封信反复唤醒"（去重键拼错）——都很难查。</para>
/// </summary>
public class EmailEventMappingTests
{
    private static MimeMessage Mail(
        string address = "boss@corp",
        string? name = "老板",
        string subject = "发版通知",
        string body = "正文",
        DateTimeOffset? date = null)
    {
        var message = new MimeMessage();
        message.From.Add(name is null
            ? new MailboxAddress(string.Empty, address)
            : new MailboxAddress(name, address));
        message.Subject = subject;
        message.Date = date ?? default;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    [Fact]
    public void SenderIsTheAddressNotTheDisplayName()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 42, 7, "INBOX");

        // 订阅按发件人地址匹配，取显示名就永远匹配不上
        Assert.Equal("boss@corp", source.SenderId);
        Assert.Equal("mail", source.SourceId);
    }

    [Fact]
    public void ExtFromKeepsDisplayName()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 42, 7, "INBOX");

        // 显示名是给人看的，地址是给匹配用的，两者都要
        Assert.Equal("老板 <boss@corp>", source.Ext["from"]);
    }

    [Fact]
    public void ExtFromFallsBackToBareAddress()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(name: null), 1, 1, "INBOX");

        Assert.Equal("boss@corp", source.Ext["from"]);
    }

    [Fact]
    public void DedupeKeyCombinesUidValidityAndUid()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 42, 7, "INBOX");

        // UIDVALIDITY 必须进键：服务器重建邮箱后同号 UID 是完全不同的信
        Assert.Equal("mail:7:42", source.DedupeKey);
    }

    [Fact]
    public void ExtCarriesSubjectUidAndFolder()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 42, 7, "Archive");

        Assert.Equal("发版通知", source.Ext["subject"]);
        Assert.Equal("42", source.Ext["uid"]);
        Assert.Equal("Archive", source.Ext["folder"]);
    }

    [Fact]
    public void EmptyFolderFallsBackToInbox()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 1, 1, "  ");

        Assert.Equal("INBOX", source.Ext["folder"]);
    }

    [Fact]
    public void BodyComesFromExtractBody()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(body: "今晚发版"), 1, 1, "INBOX");

        Assert.Equal("今晚发版", source.Text);
    }

    [Fact]
    public void MissingDateFallsBackToNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var source = EmailEventSource.ToSourceEvent(Mail(date: null), 1, 1, "INBOX");

        Assert.True(source.ReceivedAt >= before);
    }

    [Fact]
    public void ProvidedDateIsKept()
    {
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var source = EmailEventSource.ToSourceEvent(Mail(date: date), 1, 1, "INBOX");

        Assert.Equal(date, source.ReceivedAt);
    }

    [Fact]
    public void ExtIsCaseInsensitive()
    {
        var source = EmailEventSource.ToSourceEvent(Mail(), 1, 1, "INBOX");

        // 图上的 event.ext 键名是约定，插件写 Subject 还是 subject 都得能查到
        Assert.Equal("发版通知", source.Ext["Subject"]);
    }
}
