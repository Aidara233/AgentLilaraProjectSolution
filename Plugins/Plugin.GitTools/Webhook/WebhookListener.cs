using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.Logging;

namespace Plugin.GitTools.Webhook;

public class WebhookListener : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _secret;
    private readonly ISignalLogger? _log;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public Action<string, string>? OnEventReceived { get; set; }

    public WebhookListener(int port, string secret, ISignalLogger? log)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/webhooks/github");
        _secret = secret;
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        _log?.Event("git-webhook", "listener-started", new { prefixes = string.Join(", ", _listener.Prefixes) });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
        _log?.Event("git-webhook", "listener-stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _listener.Close();
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Error("git-webhook", "listener-error", new { error = ex.Message });
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.HttpMethod != "POST")
            {
                await SendResponse(response, 405, "Method Not Allowed");
                return;
            }

            var eventType = request.Headers["X-GitHub-Event"] ?? "unknown";
            var signature = request.Headers["X-Hub-Signature-256"] ?? "";

            using var ms = new MemoryStream();
            await request.InputStream.CopyToAsync(ms);
            var body = ms.ToArray();

            if (!string.IsNullOrEmpty(_secret) && !VerifySignature(signature, body))
            {
                _log?.Warn("git-webhook", "signature-verification-failed", new { eventType });
                await SendResponse(response, 401, "Signature verification failed");
                return;
            }

            var bodyStr = Encoding.UTF8.GetString(body);
            _log?.Event("git-webhook", "event-received", new { eventType, bodyLength = body.Length });

            OnEventReceived?.Invoke(eventType, bodyStr);

            await SendResponse(response, 200, "OK");
        }
        catch (Exception ex)
        {
            _log?.Error("git-webhook", "handle-request-error", new { error = ex.Message });
            try { await SendResponse(response, 500, "Internal Server Error"); } catch { }
        }
        finally
        {
            response.Close();
        }
    }

    private bool VerifySignature(string signatureHeader, byte[] body)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;

        var parts = signatureHeader.Split('=');
        if (parts.Length != 2 || parts[0] != "sha256") return false;

        var key = Encoding.UTF8.GetBytes(_secret);
        var hash = new HMACSHA256(key).ComputeHash(body);
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        return string.Equals(parts[1], expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendResponse(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        var bytes = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
