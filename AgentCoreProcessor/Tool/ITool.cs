using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具接口。所有工具实现此接口并注册到 ToolRegistry。
    /// </summary>
    internal interface ITool
    {
        /// <summary>工具名称，必须与 ToolCall.Tool 匹配。</summary>
        string Name { get; }

        /// <summary>工具描述，供动态注入 prompt 使用。</summary>
        string Description { get; }

        /// <summary>参数声明列表，供 ToolRegistry 自动生成工具描述。</summary>
        IReadOnlyList<ToolParameter> Parameters { get; }

        /// <summary>最大执行时间，超时后强制中止。</summary>
        TimeSpan Timeout { get; }

        /// <summary>
        /// 执行工具。
        /// </summary>
        /// <param name="resolvedInputs">已解析的输入列表（ref 已替换为实际数据），按 inputs 数组顺序排列。</param>
        /// <param name="ct">取消令牌（含超时控制）。</param>
        /// <returns>执行结果。ToolId 由调用方填充，工具本身无需设置。</returns>
        Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

        /// <summary>是否允许子 agent 使用。默认 true。信号类/说话/委派等工具覆盖为 false。</summary>
        bool AllowSubAgent => true;

        /// <summary>使用此工具所需的最低权限。Default = 自由工具，高于 Default 需要运行时授权。</summary>
        PermissionLevel RequiredPermission => PermissionLevel.Default;

        /// <summary>执行后结果需要返回模型，触发下一轮循环。</summary>
        bool ContinueLoop => false;

        /// <summary>结果跨轮保留（摘要形式注入 prompt，模型可按需查看详情或清理）。</summary>
        bool RetainResult => false;

        /// <summary>能力摘要（一句话），注入 Express prompt 让模型知道此能力存在。null 表示不暴露给 Express。</summary>
        string? CapabilitySummary => null;

        /// <summary>所属工具组名。null 表示默认组（始终可见）。</summary>
        string? ToolGroup => null;

        /// <summary>同组内是否默认展开（而非折叠为摘要）。</summary>
        bool DefaultExpanded => true;

        /// <summary>
        /// 原生工具调用的 JSON Schema。默认从 Parameters 推导（全部 type: string）。
        /// 需要非 string 类型的工具可覆盖此方法。
        /// </summary>
        JsonNode GetInputSchema()
        {
            if (Parameters.Count == 0)
                return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in Parameters)
            {
                props[p.Name] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = p.Description
                };
                required.Add(p.Name);
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = required
            };
        }
    }
}

