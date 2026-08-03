// AgentCoreProcessor/Tool/Core/SwitchCoreTool.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// Session 运行时切换 Core 工具：模型自主决定升级/降级模型配置。
    /// 可用核列表由会话类型白名单（Storage/Session/{type}.json 的 AllowedCores）决定，
    /// 切换只换模型配置执行器，不碰历史/端口/组件（session 定上下文、core 定模型）。
    /// </summary>
    [ToolMeta(ContinueLoop = true)]
    internal class SwitchCoreTool : ITool
    {
        private readonly RuntimeSession _session;

        public string Name => "switch_core";
        public string Description =>
            "切换当前会话使用的核心配置（换模型/参数/提示词）。" +
            "可选核心由会话类型白名单决定，调用后下一轮起用新核心继续工作。";
        public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
        {
            new("name",
                "要切换到的核心配置名（必须在本会话可用列表内，如 ChannelCore / ExpressCore）。",
                0, isRequired: true)
        };
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public SwitchCoreTool(RuntimeSession session)
        {
            _session = session;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var name = resolvedInputs != null && resolvedInputs.Count > 0
                ? resolvedInputs[0]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"switch_core 需要提供核心名。本会话可用核心：{string.Join(", ", _session.AllowedCoreConfigs)}"
                });
            }

            if (_session.SwitchCore(name))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = $"已切换到核心 {_session.ActiveCoreConfig}。可用核心：{string.Join(", ", _session.AllowedCoreConfigs)}"
                });
            }

            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = $"核心 {name} 不在本会话可用列表内。可用核心：{string.Join(", ", _session.AllowedCoreConfigs)}"
            });
        }
    }
}
