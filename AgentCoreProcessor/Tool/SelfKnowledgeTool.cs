using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Tool
{
    internal class SelfKnowledgeTool : ITool
    {
        public string Name => "view_architecture";
        public string Description => "查看自身技术架构文档（按区块）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("区块", "要查看的区块：overview/memory/tools/channels/dream（留空列出所有区块）", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public bool RetainResult => true;

        private static readonly Dictionary<string, string> Sections = new()
        {
            ["overview"] = "整体架构概述",
            ["memory"] = "记忆系统",
            ["tools"] = "工具系统",
            ["channels"] = "频道系统",
            ["dream"] = "做梦系统"
        };

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var section = resolvedInputs.ElementAtOrDefault(0)?.Trim().ToLowerInvariant() ?? "";

            if (string.IsNullOrEmpty(section))
            {
                var list = string.Join("\n", Sections.Select(kv => $"- {kv.Key}: {kv.Value}"));
                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = $"可用区块：\n{list}\n\n使用方法：查看架构(区块名)"
                });
            }

            if (!Sections.ContainsKey(section))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"未知区块 \"{section}\"，可用：{string.Join(", ", Sections.Keys)}"
                });
            }

            var path = Path.Combine(PathConfig.CoreConfigPath, "SelfKnowledge", $"{section}.txt");
            if (!File.Exists(path))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"文档文件不存在: {section}.txt"
                });
            }

            var content = File.ReadAllText(path);
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = content
            });
        }
    }
}
