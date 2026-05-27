using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true, OutputOnly = true)]
    public class AdapterActionTool : ITool
    {
        private readonly IAdapterAccess? _adapterAccess;
        private readonly string _adapterId = "";
        private readonly List<AdapterActionInfo> _availableActions = new();

        public AdapterActionTool() { }

        public AdapterActionTool(IAdapterAccess adapterAccess, string adapterId)
        {
            _adapterAccess = adapterAccess;
            _adapterId = adapterId;
            _availableActions = adapterAccess.GetAvailableActions(adapterId);
        }

        public string Name => "adapter_action";

        public string Description
        {
            get
            {
                if (_availableActions.Count == 0)
                    return "执行QQ平台特殊操作（当前适配器无可用操作）。";

                var sb = new StringBuilder();
                sb.AppendLine("执行QQ平台特殊操作。可用操作：");
                foreach (var a in _availableActions)
                {
                    var paramDescs = a.Params.Select(p =>
                    {
                        var req = p.Required ? "必填" : "可选";
                        return $"{p.Name}({req},{p.Label})";
                    });
                    var paramStr = paramDescs.Any() ? $" 参数: {string.Join(", ", paramDescs)}" : " 无参数";
                    sb.AppendLine($"- {a.Name}: {a.Description}.{paramStr}");
                }
                sb.Append("action 填操作名称，params_json 填JSON参数字符串（如 {\\\"user_id\\\":\\\"123456\\\"}），无参数时传 {}");
                return sb.ToString();
            }
        }

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作名称，如 poke / recall / get_group_list", 0),
            new("params_json", "JSON参数字符串，如 {\"user_id\":\"123456\"}，无参数时传 {}", 1)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_adapterAccess == null)
                return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return new ToolResult { Status = "failed", Error = "action 不能为空" };

            var action = resolvedInputs[0].Trim();
            var paramsJson = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : "{}";
            if (string.IsNullOrEmpty(paramsJson)) paramsJson = "{}";

            var result = await _adapterAccess.ExecuteActionAsync(_adapterId, action, paramsJson);
            return result != null
                ? new ToolResult { Status = "success", Data = result }
                : new ToolResult { Status = "failed", Error = $"操作 {action} 执行失败" };
        }
    }
}
