using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 检索记忆工具。按关键词/向量搜索主库记忆。
    /// </summary>
    internal class ReviewSearchMemoryTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewSearchMemoryTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "检索记忆";
        public string Description => "按关键词搜索主记忆库，可选按频道/人物过滤";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("查询内容", "搜索关键词或语义描述", 0),
            new("频道ID", "可选，限定搜索的频道ID", 1),
            new("人物ID", "可选，限定搜索的人物ID", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var query = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResult { Status = "failed", Error = "查询内容不能为空" };

            int channelId = 0, personId = 0;
            int.TryParse(resolvedInputs.ElementAtOrDefault(1) ?? "", out channelId);
            int.TryParse(resolvedInputs.ElementAtOrDefault(2) ?? "", out personId);

            var results = await ctx.MemorySvc.RecallAsync(
                personId > 0 ? personId : 0,
                channelId > 0 ? channelId : 0,
                query, topK: 15, includeLinks: true);

            if (results.Count == 0)
                return new ToolResult { Status = "success", Data = "未找到相关记忆" };

            var sb = new StringBuilder();
            foreach (var m in results)
                sb.AppendLine($"[ID:{m.Id}] {m.Content}");
            return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
        }
    }

    /// <summary>
    /// 查看关联工具。查询某条记忆的关联网络。
    /// </summary>
    internal class ReviewViewLinksTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewViewLinksTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "查看关联";
        public string Description => "查询指定记忆ID的关联网络（关联记忆、强度、类型）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("记忆ID", "要查询关联的记忆ID", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (!int.TryParse(resolvedInputs.ElementAtOrDefault(0), out var memoryId))
                return new ToolResult { Status = "failed", Error = "记忆ID必须是整数" };

            var links = await ctx.MemoryLinks.GetByMemoryIdAsync(memoryId);
            if (links.Count == 0)
                return new ToolResult { Status = "success", Data = "该记忆没有关联" };

            var linkedIds = links.Select(l => l.SourceId == memoryId ? l.TargetId : l.SourceId).ToList();
            var linkedMemories = await ctx.Memories.GetByIdsAsync(linkedIds);
            var memMap = linkedMemories.ToDictionary(m => m.Id);

            var sb = new StringBuilder();
            foreach (var link in links)
            {
                var otherId = link.SourceId == memoryId ? link.TargetId : link.SourceId;
                var content = memMap.TryGetValue(otherId, out var m) ? m.Content : "(未找到)";
                sb.AppendLine($"[ID:{otherId}] 强度:{link.Strength:F2} 类型:{link.LinkType} | {content}");
            }
            return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
        }
    }

    /// <summary>
    /// 读取消息历史工具。拉某话题的原始消息。
    /// </summary>
    internal class ReviewReadMessagesTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewReadMessagesTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "读取消息历史";
        public string Description => "读取指定频道ID的原始消息记录";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("频道ID", "要读取的频道ID", 0),
            new("数量限制", "可选，最多返回多少条消息（默认50）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (!int.TryParse(resolvedInputs.ElementAtOrDefault(0), out var channelId))
                return new ToolResult { Status = "failed", Error = "频道ID必须是整数" };

            int limit = 50;
            int.TryParse(resolvedInputs.ElementAtOrDefault(1) ?? "", out var parsedLimit);
            if (parsedLimit > 0) limit = Math.Min(parsedLimit, 200);

            var messages = await ctx.Session.GetContextByChannelAsync(channelId, limit);
            if (messages.Count == 0)
                return new ToolResult { Status = "success", Data = "该频道没有消息" };

            var sb = new StringBuilder();
            foreach (var msg in messages)
                sb.AppendLine($"[{msg.Time:HH:mm}] User{msg.UserId}: {msg.Content}");
            return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
        }
    }

    /// <summary>
    /// 写入临时记忆工具。信号工具，由 ReviewEngine Agent 循环处理实际写入。
    /// </summary>
    internal class ReviewWriteTempMemoryTool : ITool
    {
        public string Name => "写入临时记忆";
        public string Description => "将复盘发现/结论写入临时记忆库（下次做梦时整合入主库）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("内容", "要存储的发现或结论", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "内容不能为空" });
            return Task.FromResult(new ToolResult { Status = "success", Data = resolvedInputs[0] });
        }
    }

    /// <summary>
    /// 复盘用思考笔记工具。与 WorkingCore 的思考笔记行为一致。
    /// </summary>
    internal class ReviewThinkingNotesTool : ITool
    {
        public string Name => "思考笔记";
        public string Description => "记录/修改/删除跨轮思考笔记（action: write/delete, key: 标识, value: 内容）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "write 或 delete", 0),
            new("键", "笔记标识", 1),
            new("值", "笔记内容（delete 时忽略）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.ElementAtOrDefault(0)?.Trim().ToLower();
            var key = resolvedInputs.ElementAtOrDefault(1);
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(key))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "操作和键不能为空" });
            if (action != "write" && action != "delete")
                return Task.FromResult(new ToolResult { Status = "failed", Error = "操作必须是 write 或 delete" });
            return Task.FromResult(new ToolResult { Status = "success", Data = action });
        }
    }

    /// <summary>
    /// 复盘用标记复盘工具。给未来的自己留 hint。
    /// </summary>
    internal class ReviewMarkHintTool : ITool
    {
        public string Name => "标记复盘";
        public string Description => "标记一条内容供下次复盘重点关注";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("标记内容", "值得下次复盘关注的内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "标记内容不能为空" });
            return Task.FromResult(new ToolResult { Status = "success", Data = resolvedInputs[0] });
        }
    }

    /// <summary>
    /// 请求增援工具。显式申请备用预算。
    /// </summary>
    internal class ReviewRequestReinforcementTool : ITool
    {
        public string Name => "请求增援";
        public string Description => "申请使用备用token预算（仅可使用一次，不可恢复）";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
            => Task.FromResult(new ToolResult { Status = "success", Data = "reinforcement-requested" });
    }

    /// <summary>
    /// 保存进度工具。信号工具，由 ReviewEngine 处理实际存档。
    /// </summary>
    internal class ReviewSaveProgressTool : ITool
    {
        public string Name => "保存进度";
        public string Description => "保存当前复盘进度（包含发现和后续计划），下次大睡可继续";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("进度内容", "当前调查进度的 JSON 描述", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "进度内容不能为空" });
            return Task.FromResult(new ToolResult { Status = "success", Data = resolvedInputs[0] });
        }
    }

    /// <summary>
    /// 复盘完成工具。终止 Agent 循环。
    /// </summary>
    internal class ReviewCompletionTool : ITool
    {
        public string Name => "完成";
        public string Description => "结束本次复盘（可选附带总结）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("总结", "可选的复盘总结", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
            => Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs.ElementAtOrDefault(0) ?? "复盘完成"
            });
    }

    internal class ReviewUpdateFastMemoryTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewUpdateFastMemoryTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "更新快速记忆";
        public string Description => "更新人物的快速记忆摘要（一句话概括此人的关键信息，简明扼要）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("人物ID", "目标人物ID", 0),
            new("内容", "快速记忆内容", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (!int.TryParse(resolvedInputs.ElementAtOrDefault(0), out var personId))
                return new ToolResult { Status = "failed", Error = "人物ID必须是整数" };
            var content = resolvedInputs.ElementAtOrDefault(1) ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return new ToolResult { Status = "failed", Error = "内容不能为空" };

            var person = await ctx.Session.GetPersonByIdAsync(personId);
            if (person == null)
                return new ToolResult { Status = "failed", Error = $"Person [{personId}] 不存在" };

            person.FastMemory = content;
            await ctx.Session.UpdatePersonAsync(person);
            return new ToolResult { Status = "success", Data = $"已更新 Person [{personId}] 快速记忆" };
        }
    }

    internal class ReviewUpdateTrustProgressTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewUpdateTrustProgressTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "调整好感度";
        public string Description => "调整人物的好感度（正值=好感增加，负值=好感降低，每次上限±0.3）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("人物ID", "目标人物ID", 0),
            new("变化量", "好感度变化量（如 0.2 或 -0.1）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (!int.TryParse(resolvedInputs.ElementAtOrDefault(0), out var personId))
                return new ToolResult { Status = "failed", Error = "人物ID必须是整数" };
            if (!float.TryParse(resolvedInputs.ElementAtOrDefault(1), out var delta))
                return new ToolResult { Status = "failed", Error = "变化量必须是数字" };

            var cap = ctx.TrustConfig.DreamEvaluationCap;
            delta = Math.Clamp(delta, -cap, cap);

            var person = await ctx.Session.GetPersonByIdAsync(personId);
            if (person == null)
                return new ToolResult { Status = "failed", Error = $"Person [{personId}] 不存在" };

            person.TrustProgress += delta;
            await ctx.Session.UpdatePersonAsync(person);
            return new ToolResult
            {
                Status = "success",
                Data = $"Person [{personId}] 好感度 {(delta >= 0 ? "+" : "")}{delta:F2} → {person.TrustProgress:F2}"
            };
        }
    }
}
