using MimeKit;
using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// H1：正文抽取。
/// <para>正文抽取错了，模型看到的整封信都是错的——而这里没有任何网络依赖，没理由不测。</para>
/// </summary>
public class EmailBodyExtractionTests
{
    [Fact]
    public void PlainTextWinsOverHtml()
    {
        var message = new MimeMessage
        {
            Body = new Multipart("alternative")
            {
                new TextPart("plain") { Text = "纯文本正文" },
                new TextPart("html") { Text = "<p>HTML 正文</p>" },
            },
        };

        Assert.Equal("纯文本正文", EmailEventSource.ExtractBody(message));
    }

    [Fact]
    public void HtmlIsDegradedToText()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("html") { Text = "<html><body><p>你好</p><p>再见</p></body></html>" },
        };

        var body = EmailEventSource.ExtractBody(message);

        Assert.DoesNotContain("<", body);
        Assert.Contains("你好", body);
        Assert.Contains("再见", body);
    }

    [Fact]
    public void StyleBlockIsNotTreatedAsContent()
    {
        // 营销邮件几乎都带几百行 CSS，只去标签会把它们原样当正文塞进上下文
        var message = new MimeMessage
        {
            Body = new TextPart("html")
            {
                Text = "<html><head><style>.a{color:red;font-size:12px}</style></head>" +
                       "<body><p>真正的内容</p></body></html>",
            },
        };

        var body = EmailEventSource.ExtractBody(message);

        Assert.Contains("真正的内容", body);
        Assert.DoesNotContain("color:red", body);
        Assert.DoesNotContain("font-size", body);
    }

    [Fact]
    public void ScriptBlockIsNotTreatedAsContent()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("html")
            {
                Text = "<body><script>var tracking = 'abc123';</script><p>正文</p></body>",
            },
        };

        var body = EmailEventSource.ExtractBody(message);

        Assert.Contains("正文", body);
        Assert.DoesNotContain("tracking", body);
    }

    [Fact]
    public void TagWithLongerNameIsNotMistakenForScript()
    {
        // <scripture> 不是 script，内容不该被吃掉
        var text = EmailEventSource.HtmlToText("<body><scripture>经文</scripture></body>");

        Assert.Contains("经文", text);
    }

    [Fact]
    public void EntitiesAreDecoded()
    {
        var text = EmailEventSource.HtmlToText("<p>a &amp; b &lt;tag&gt; &quot;q&quot;&nbsp;end</p>");

        Assert.Contains("a & b", text);
        Assert.Contains("<tag>", text);
        Assert.Contains("\"q\"", text);
    }

    [Fact]
    public void EmptyMessageYieldsEmptyBody()
    {
        Assert.Equal(string.Empty, EmailEventSource.ExtractBody(new MimeMessage()));
    }

    [Fact]
    public void WhitespaceOnlyPlainFallsBackToHtml()
    {
        var multipart = new MultipartAlternative();
        multipart.Add(new TextPart("plain") { Text = "   " });
        multipart.Add(new TextPart("html") { Text = "<p>HTML 兜底</p>" });

        var message = new MimeMessage { Body = multipart };

        // 空白纯文本不算正文，否则模型只会收到一串空格
        Assert.Contains("HTML 兜底", EmailEventSource.ExtractBody(message));
    }

    [Fact]
    public void UnclosedTagDoesNotThrow()
    {
        var text = EmailEventSource.HtmlToText("正文<p");

        Assert.Equal("正文", text);
    }
}
