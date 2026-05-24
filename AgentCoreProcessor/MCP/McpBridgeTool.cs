using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Tool;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.MCP
{
    internal class McpBridgeTool : ITool
    {
        private readonly McpClientTool _mcpTool;
        private readonly McpServerEntry _serverEntry;
        private readonly McpToolOverride? _override;
        private readonly List<string> _parameterNames;
        private readonly List<ToolParameter> _parameters;

        public McpBridgeTool(McpClientTool mcpTool, McpServerEntry serverEntry)
        {
            _mcpTool = mcpTool;
            _serverEntry = serverEntry;
            serverEntry.ToolOverrides.TryGetValue(mcpTool.Name, out _override);

            var prefix = string.IsNullOrEmpty(serverEntry.ToolPrefix) ? "" : $"{serverEntry.ToolPrefix}_";
            Name = $"{prefix}{mcpTool.Name}";
            Description = mcpTool.Description ?? mcpTool.Name;

            (_parameterNames, _parameters) = BuildParameters(mcpTool.JsonSchema);
        }

        public string Name { get; }
        public string Description { get; }
        public IReadOnlyList<ToolParameter> Parameters => _parameters;
        public TimeSpan Timeout => TimeSpan.FromSeconds(_serverEntry.Timeout);
        public bool ContinueLoop => _override?.ContinueLoop ?? true;
        public bool RetainResult => _override?.RetainResult ?? true;
        public PermissionLevel RequiredPermission
        {
            get
            {
                var str = _override?.Permission ?? _serverEntry.Permission;
                return str switch
                {
                    "Elevated" => PermissionLevel.Elevated,
                    "Admin" => PermissionLevel.Admin,
                    _ => PermissionLevel.Default
                };
            }
        }

        public string? CapabilitySummary
        {
            get
            {
                var desc = Description.Length > 40 ? Description[..40] + "..." : Description;
                return $"[MCP] {desc}";
            }
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var arguments = new Dictionary<string, object?>();
            for (int i = 0; i < _parameterNames.Count && i < resolvedInputs.Count; i++)
            {
                var value = resolvedInputs[i];
                if (string.IsNullOrEmpty(value)) continue;

                object? parsed;
                try { parsed = JToken.Parse(value).ToObject<object>(); }
                catch { parsed = value; }

                arguments[_parameterNames[i]] = parsed;
            }

            try
            {
                var result = await _mcpTool.CallAsync(arguments, cancellationToken: ct);

                var texts = new List<string>();
                if (result.Content != null)
                {
                    foreach (var block in result.Content)
                    {
                        if (block is TextContentBlock text)
                            texts.Add(text.Text);
                    }
                }

                var data = string.Join("\n", texts);
                if (result.IsError == true)
                    return new ToolResult { Status = "failed", Error = data };

                return new ToolResult { Status = "success", Data = data };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"MCP [{_serverEntry.Id}] 调用失败: {ex.Message}"
                };
            }
        }

        private static (List<string> names, List<ToolParameter> parameters) BuildParameters(JsonElement schema)
        {
            var names = new List<string>();
            var parameters = new List<ToolParameter>();

            if (schema.ValueKind != JsonValueKind.Object) return (names, parameters);
            if (!schema.TryGetProperty("properties", out var props)) return (names, parameters);

            var required = new List<string>();
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                required = req.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

            var allProps = props.EnumerateObject().ToList();
            var ordered = required
                .Where(r => allProps.Any(p => p.Name == r))
                .Concat(allProps.Select(p => p.Name).Where(n => !required.Contains(n)).OrderBy(n => n))
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var name = ordered[i];
                var desc = name;
                if (props.TryGetProperty(name, out var propSchema) &&
                    propSchema.TryGetProperty("description", out var d))
                    desc = d.GetString() ?? name;

                if (!required.Contains(name))
                    desc += "（可选）";

                names.Add(name);
                parameters.Add(new ToolParameter(name, desc, i));
            }

            return (names, parameters);
        }
    }
}
