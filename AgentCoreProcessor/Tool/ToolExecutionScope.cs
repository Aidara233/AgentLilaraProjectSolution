namespace AgentCoreProcessor.Tool;

/// <summary>一次工具调用发生时所处的位置。</summary>
internal sealed record ToolExecutionContext
{
    /// <summary>发起调用的会话。为空表示不是会话发起的（命令、初始化动作等）。</summary>
    public string? SessionId { get; init; }

    /// <summary>会话类型（express / channel / task / review）。</summary>
    public string? SessionType { get; init; }

    /// <summary>该会话当前占用的频道；0 表示没有频道（WebUI 会话、子 agent 等）。</summary>
    public int ChannelId { get; init; }
}

/// <summary>
/// 工具执行上下文（环境传递）。
/// <para>为什么需要：<see cref="AgentLilara.PluginSDK.ITool.ExecuteAsync"/> 的签名里只有参数，
/// 没有任何"谁在调用"的信息。而像"给当前频道挂个订阅"这种工具必须知道频道——
/// 让模型自己填 channelId 是不行的，它不知道这个内部标识。</para>
/// <para>⚠️ 用 AsyncLocal 而不是改接口：<c>ITool</c> 是插件契约，加参数会破坏所有既有工具与第三方插件；
/// 而这层信息对绝大多数工具没用，只有少数需要，环境传递正合适。</para>
/// <para>⚠️ 作用域由 <see cref="ToolExecutor"/> 在执行前后成对设置与恢复，
/// 不依赖调用方记得清理（异步并发下没清理会串到别的会话）。</para>
/// </summary>
internal static class ToolExecutionScope
{
    private static readonly AsyncLocal<ToolExecutionContext?> Ambient = new();

    /// <summary>当前工具调用的位置；不在任何工具调用内时为 null。</summary>
    public static ToolExecutionContext? Current => Ambient.Value;

    public static IDisposable Begin(ToolExecutionContext context)
    {
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly ToolExecutionContext? _previous;

        public Restore(ToolExecutionContext? previous) => _previous = previous;

        public void Dispose() => Ambient.Value = _previous;
    }
}
