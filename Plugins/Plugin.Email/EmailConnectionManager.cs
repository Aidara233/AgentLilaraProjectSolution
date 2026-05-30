using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Plugin.Email;

public class EmailConnectionManager
{
    private readonly EmailConfig _config;
    private ImapClient? _imapClient;
    private readonly SemaphoreSlim _imapLock = new(1, 1);
    private readonly SemaphoreSlim _smtpLock = new(1, 1);
    private CancellationTokenSource? _idleCts;
    private Task? _idleTask;

    public EmailConnectionManager(EmailConfig config)
    {
        _config = config;
    }

    public bool ImapConnected => _imapClient?.IsConnected == true && _imapClient.IsAuthenticated;
    public bool Configured => !string.IsNullOrEmpty(_config.Imap.Username) && !string.IsNullOrEmpty(_config.Smtp.Username);
    public string SmtpUsername => _config.Smtp.Username;

    // === IMAP Keep-Alive ===

    public void StartImapKeepAlive(CancellationToken shutdownCt)
    {
        if (!Configured) return;

        _imapClient = new ImapClient();
        _imapClient.ServerCertificateValidationCallback = (s, c, h, e) => true;
        _idleCts = new CancellationTokenSource();

        _idleTask = Task.Run(() => IdleLoop(shutdownCt), CancellationToken.None);
    }

    private async Task IdleLoop(CancellationToken shutdownCt)
    {
        while (!shutdownCt.IsCancellationRequested)
        {
            try
            {
                if (_imapClient != null && !_imapClient.IsConnected)
                {
                    await _imapClient.ConnectAsync(
                        _config.Imap.Host, _config.Imap.Port,
                        _config.Imap.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
                        shutdownCt);
                    await _imapClient.AuthenticateAsync(
                        _config.Imap.Username, _config.Imap.Password, shutdownCt);
                }

                if (_imapClient != null && _imapClient.IsConnected)
                {
                    var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCt, _idleCts!.Token);
                    linkedCts.CancelAfter(TimeSpan.FromMinutes(9));
                    try
                    {
                        await _imapClient.IdleAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (!shutdownCt.IsCancellationRequested)
                    {
                        // Idle timeout or interrupted — loop continues
                    }
                    finally
                    {
                        linkedCts.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                try { _imapClient?.Disconnect(true); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(30), shutdownCt); } catch { break; }
            }
        }

        if (_imapClient?.IsConnected == true)
        {
            try { await _imapClient.DisconnectAsync(true, shutdownCt); } catch { }
        }
        _imapClient?.Dispose();
    }

    /// <summary>执行 IMAP 操作，自动重连 + 锁保护。</summary>
    public async Task<T> ExecuteImapAsync<T>(Func<ImapClient, Task<T>> operation, CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("邮件服务未配置，请先在 Email.json 中设置账户信息。");

        await _imapLock.WaitAsync(ct);
        try
        {
            if (_imapClient == null || !_imapClient.IsAuthenticated)
            {
                if (_imapClient == null)
                {
                    _imapClient = new ImapClient();
                    _imapClient.ServerCertificateValidationCallback = (s, c, h, e) => true;
                }
                try
                {
                    if (!_imapClient.IsConnected)
                        await _imapClient.ConnectAsync(_config.Imap.Host, _config.Imap.Port,
                            _config.Imap.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, ct);
                    if (!_imapClient.IsAuthenticated)
                        await _imapClient.AuthenticateAsync(_config.Imap.Username, _config.Imap.Password, ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"IMAP 连接失败: {ex.Message}", ex);
                }
            }

            return await operation(_imapClient);
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

    /// <summary>执行 SMTP 发送，按需连接。</summary>
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

    /// <summary>停止 IMAP 保活并断开连接。</summary>
    public async Task StopAsync()
    {
        _idleCts?.Cancel();
        if (_idleTask != null)
        {
            try { await _idleTask; } catch { }
        }
        if (_imapClient?.IsConnected == true)
        {
            try { await _imapClient.DisconnectAsync(true); } catch { }
        }
        _imapClient?.Dispose();
        _imapClient = null;
    }
}
