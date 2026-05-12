using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 工具接口。所有工具（内置和插件）实现此接口。
    /// 行为元数据通过 ToolMetaAttribute 声明，不再作为接口默认实现。
    /// </summary>
    public interface ITool
    {
        string Name { get; }
        string Description { get; }
        IReadOnlyList<ToolParameter> Parameters { get; }
        TimeSpan Timeout { get; }

        Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

        /// <summary>
        /// 原生工具调用的 JSON Schema。默认从 Parameters 推导（全部 type: string）。
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
