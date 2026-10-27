// AgentCoreProcessor/Engine/Session/MailboxInputPort.cs
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 子 session 邮箱端口。用于父 session 向子 session 注入指令文本。
/// 基于 System.Threading.Channels.Channel&lt;string&gt; 的有界队列实现 IInputPort。
/// WaitForActivationAsync 通过 `port is not MessageInputPort` 分支感知此端口有数据即立即激活。
/// DrainInputPortsRaw 中 string 类型数据作为 channelId=0 的系统消息注入上下文。
/// </summary>
internal sealed class MailboxInputPort : IInputPort
{
    private readonly Channel<string> _channel;

    public string Name => "mailbox";
    public Type DataType => typeof(string);
    /// <summary>100 与 @提及同级，保证父指令到达即激活。</summary>
    public int Weight => 100;

    public MailboxInputPort(Channel<string>? channel = null)
    {
        _channel = channel ?? Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    /// <summary>写入一条父会话指令。永不阻塞（无界队列）。</summary>
    public bool TryWrite(string message) => _channel.Writer.TryWrite(message);

    public bool TryPeek(out object? data)
    {
        if (_channel.Reader.TryPeek(out var msg))
        {
            data = msg;
            return true;
        }
        data = null;
        return false;
    }

    public async Task<object?> ReceiveAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    /// <summary>关闭写入端。子 session 终止后由持有者调用，避免泄漏挂起的 writer。</summary>
    public void Complete() => _channel.Writer.TryComplete();
}