using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Plugin.Email;

public class EmailConnectionManager
{
    private readonly EmailConfig _config;
    private readonly SemaphoreSlim _imapLock = new(1, 1);
    private readonly SemaphoreSlim _smtpLock = new(1, 1);

    public EmailConnectionManager(EmailConfig config)
    {
        _config = config;
    }

    public bool Configured => !string.IsNullOrEmpty(_config.Imap.Username) && !string.IsNullOrEmpty(_config.Smtp.Username);
    public string SmtpUsername => _config.Smtp.Username;

    /// <summary>执行 IMAP 操作，按需建连，用完即释放。</summary>
    public async Task<T> ExecuteImapAsync<T>(Func<ImapClient, Task<T>> operation, CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("邮件服务未配置，请先在 Email.json 中设置账户信息。");

        await _imapLock.WaitAsync(ct);
        try
        {
            using var imap = new ImapClient();
            imap.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await imap.ConnectAsync(
                _config.Imap.Host, _config.Imap.Port,
                _config.Imap.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
                ct);
            await imap.AuthenticateAsync(_config.Imap.Username, _config.Imap.Password, ct);

            var result = await operation(imap);

            if (imap.IsConnected)
                await imap.DisconnectAsync(true, ct);
            return result;
        }
        catch (ImapCommandException ex)
        {
            throw new InvalidOperationException($"IMAP 命令失败: {ex.Message}", ex);
        }
        catch (ImapProtocolException ex)
        {
            throw new InvalidOperationException($"IMAP 协议错误: {ex.Message}", ex);
        }
        finally
        {
            _imapLock.Release();
        }
    }

    /// <summary>执行 SMTP 发送，按需建连，用完即释放。</summary>
    public async Task ExecuteSmtpAsync(Func<SmtpClient, Task> operation, CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("邮件服务未配置，请先在 Email.json 中设置账户信息。");

        await _smtpLock.WaitAsync(ct);
        try
        {
            using var smtp = new SmtpClient();
            smtp.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await smtp.ConnectAsync(
                _config.Smtp.Host, _config.Smtp.Port,
                _config.Smtp.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, ct);
            await smtp.AuthenticateAsync(_config.Smtp.Username, _config.Smtp.Password, ct);
            await operation(smtp);
            await smtp.DisconnectAsync(true, ct);
        }
        finally
        {
            _smtpLock.Release();
        }
    }
}
