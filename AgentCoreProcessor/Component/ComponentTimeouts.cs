// AgentCoreProcessor/Component/ComponentTimeouts.cs
namespace AgentCoreProcessor.Component;

/// <summary>
/// 组件生命周期超时（防御性兜底，防单组件挂起拖住启动/会话创建）。
/// 从旧 ComponentConfig.json 收敛为代码常量——仅 3 个防御值，不再外部可配。
/// </summary>
internal static class ComponentTimeouts
{
    /// <summary>组件关闭总超时（毫秒）。</summary>
    public const int ShutdownTimeoutMs = 30000;

    /// <summary>全局组件（Global 作用域）初始化超时（秒）。</summary>
    public const int GlobalInitTimeoutSeconds = 10;

    /// <summary>会话 Loop 组件初始化超时（秒）。</summary>
    public const int LoopInitTimeoutSeconds = 5;
}
