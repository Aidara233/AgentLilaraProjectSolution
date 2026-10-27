// AgentCoreProcessor/Engine/Session/PortImpls.cs
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 基于 Channel&lt;T&gt; 的输入端口实现。
/// </summary>
internal class ChannelInputPort<T> : IInputPort
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

    public string Name { get; }
    public int Weight { get; set; }
    public Type DataType => typeof(T);

    public ChannelInputPort(string name, int weight = 5)
    {
        Name = name;
        Weight = weight;
    }

    public void Write(T data) => _channel.Writer.TryWrite(data);

    public async Task<object?> ReceiveAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    public bool TryPeek(out object? data)
    {
        if (_channel.Reader.TryPeek(out var item)) { data = item; return true; }
        data = null;
        return false;
    }
}

/// <summary>
/// 基于委托的输出端口实现。
/// </summary>
internal class DelegateOutputPort<T> : IOutputPort
{
    private readonly Func<T, Task> _handler;

    public string Name { get; }
    public Type DataType => typeof(T);

    public DelegateOutputPort(string name, Func<T, Task> handler)
    {
        Name = name;
        _handler = handler;
    }

    public async Task SendAsync(object data)
    {
        if (data is T typed)
            await _handler(typed);
    }
}
