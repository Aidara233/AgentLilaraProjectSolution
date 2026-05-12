using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    internal class AdapterActionTool : ITool
    {
        private readonly ISystemContext ctx;

        public string Name => "adapter_action";
        public string Description => "对指定适配器执行操作（如获取群列表、戳一戳、撤回消息等）。参数1=适配器ID，参数2=操作名，参数3=JSON参数对象（可选）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("适配器ID", "目标适配器的 ID（如 qq-main）", 0),
            new("操作名", "要执行的操作名称（如 get_group_list、poke、recall）", 1),
            new("参数", "JSON 格式的参数对象，如 {\"group_id\":\"123456\"}。无参数时留空", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(15);
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "可以对 QQ 适配器执行操作：获取群/好友列表、戳一戳、撤回消息、设置群名片";

        public AdapterActionTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var adapterId = resolvedInputs.ElementAtOrDefault(0)?.Trim();
            var actionName = resolvedInputs.ElementAtOrDefault(1)?.Trim();
            var paramsJson = resolvedInputs.ElementAtOrDefault(2)?.Trim();

            if (string.IsNullOrEmpty(adapterId))
                return new ToolResult { Status = "failed", Error = "缺少适配器ID" };
            if (string.IsNullOrEmpty(actionName))
                return new ToolResult { Status = "failed", Error = "缺少操作名" };

            if (actionName.StartsWith("send_", StringComparison.OrdinalIgnoreCase))
                return new ToolResult { Status = "failed", Error = "不允许通过适配器操作直接发送消息，请通过频道循环发送" };

            var adapter = ctx.Adapters.GetAdapterById(adapterId);
            if (adapter == null)
                return new ToolResult { Status = "failed", Error = $"适配器 '{adapterId}' 不存在" };

            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(paramsJson) && paramsJson != "{}")
            {
                try
                {
                    parameters = JsonConvert.DeserializeObject<Dictionary<string, string>>(paramsJson)
                        ?? new Dictionary<string, string>();
                }
                catch
                {
                    return new ToolResult { Status = "failed", Error = "参数 JSON 格式错误" };
                }
            }

            var result = await adapter.ExecuteActionAsync(actionName, parameters);

            if (result.Success)
            {
                return new ToolResult
                {
                    Status = "success",
                    Data = result.Result ?? "操作成功"
                };
            }
            else
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = result.Error ?? "操作失败"
                };
            }
        }
    }
}
