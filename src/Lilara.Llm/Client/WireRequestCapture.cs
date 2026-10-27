using System.Threading;

namespace AgentCoreProcessor.Client;

/// <summary>
/// 只读的**线上请求体**捕获。⚠️ 只在显式启用（影子模式）时工作，且**绝不改变发送内容**。
///
/// <para>为什么需要它：`LegacyRequestMapper` 的快照来自客户端持有的历史与配置，
/// 而真正的请求体还会经过 SDK 序列化、ExtraBody 注入、图片编码等步骤。
/// 影子比较必须对着**实际发出去的字节**，否则比的是我们以为的请求。</para>
///
/// <para>⚠️ 作用域走 <see cref="AsyncLocal{T}"/>：并发会话各记各的，
/// 不会出现「A 会话读到 B 会话的请求体」。</para>
/// </summary>
internal static class WireRequestCapture
{
    internal sealed class Scope
    {
        public string? Body;
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>由启动接线在影子模式下打开；关闭时所有捕获点都是空操作。</summary>
    public static bool Enabled { get; set; }

    public static WireHandle Begin()
    {
        if (!Enabled) return default;

        var scope = new Scope();
        Current.Value = scope;
        return new WireHandle(scope);
    }

    public static void Capture(string? body)
    {
        if (!Enabled || string.IsNullOrEmpty(body)) return;

        var scope = Current.Value;
        if (scope is not null) scope.Body = body;
    }

    internal readonly struct WireHandle(Scope? scope)
    {
        public string? Body => scope?.Body;
    }
}
