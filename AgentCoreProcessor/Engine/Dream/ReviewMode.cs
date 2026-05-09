using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    internal enum ReviewMode
    {
        ChannelDaily,
        PersonProfile,
        CrossDomain,
        ContradictionDetect
    }

    /// <summary>
    /// 复盘模式选择器。根据数据状态计算各模式权重，加权随机选择，构建预注入上下文。
    /// </summary>
    internal static class ReviewModeSelector
    {
        private static readonly Random rng = new();

        private static string DreamProgressPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamProgress.json");

        /// <summary>
        /// 选择复盘模式并构建预注入上下文。
        /// 返回 (选中模式, 预注入上下文文本, 进度存档)。
        /// </summary>
        public static async Task<(ReviewMode mode, string context, DreamProgress progress)>
            SelectAndPrepareAsync(ISystemContext ctx)
        {
            var progress = DreamProgress.Load(DreamProgressPath);
            var hints = await ctx.ReviewHints.GetUnprocessedAsync();

            // 检查是否有未完成的进度
            if (progress.ActiveInvestigations.Count > 0)
            {
                var investigation = progress.ActiveInvestigations[0];
                if (Enum.TryParse<ReviewMode>(investigation.Mode, out var resumeMode))
                {
                    var resumeContext = BuildResumeContext(investigation, hints);
                    return (resumeMode, resumeContext, progress);
                }
            }

            // 计算权重
            var weights = await ComputeWeightsAsync(ctx, hints);

            // 加权随机选择
            var mode = WeightedRandomSelect(weights);

            // 构建预注入上下文
            var context = await BuildContextAsync(ctx, mode, hints);

            return (mode, context, progress);
        }

        private static async Task<Dictionary<ReviewMode, float>> ComputeWeightsAsync(
            ISystemContext ctx, List<ReviewHint> hints)
        {
            var weights = new Dictionary<ReviewMode, float>();

            // ChannelDaily — 基础权重，有频道相关 hint 加权
            var channelHints = hints.Count(h => h.ChannelId != null);
            weights[ReviewMode.ChannelDaily] = 3.0f + channelHints * 2.0f;

            // PersonProfile — 基础权重，有人物相关 hint 加权
            var personHints = hints.Count(h => h.PersonId != null);
            weights[ReviewMode.PersonProfile] = 2.0f + personHints * 2.0f;

            // CrossDomain — 多频道多话题时权重高
            var channels = await ctx.Session.GetAllChannelsAsync();
            weights[ReviewMode.CrossDomain] = channels.Count > 1 ? 2.0f : 0.5f;

            // ContradictionDetect — 基础权重，记忆越多越值得检测
            var memoryCount = (await ctx.Memories.GetRecentAsync(100)).Count;
            weights[ReviewMode.ContradictionDetect] = 1.0f + memoryCount / 50f;

            return weights;
        }

        private static ReviewMode WeightedRandomSelect(Dictionary<ReviewMode, float> weights)
        {
            var total = weights.Values.Sum();
            if (total <= 0) return ReviewMode.ChannelDaily;

            var roll = rng.NextDouble() * total;
            double cumulative = 0;
            foreach (var (mode, weight) in weights)
            {
                cumulative += weight;
                if (roll <= cumulative) return mode;
            }
            return weights.Keys.Last();
        }

        private static async Task<string> BuildContextAsync(
            ISystemContext ctx, ReviewMode mode, List<ReviewHint> hints)
        {
            var sb = new StringBuilder();

            // 注入未处理的 ReviewHint
            if (hints.Count > 0)
            {
                sb.AppendLine("## 复盘提示（工作时标记的需要关注的内容）");
                foreach (var hint in hints)
                    sb.AppendLine($"- {hint.Content}");
                sb.AppendLine();
            }

            switch (mode)
            {
                case ReviewMode.ChannelDaily:
                    await BuildChannelDailyContext(ctx, sb, hints);
                    break;
                case ReviewMode.PersonProfile:
                    await BuildPersonProfileContext(ctx, sb, hints);
                    break;
                case ReviewMode.CrossDomain:
                    await BuildCrossDomainContext(ctx, sb);
                    break;
                case ReviewMode.ContradictionDetect:
                    await BuildContradictionContext(ctx, sb);
                    break;
            }

            return sb.ToString().TrimEnd();
        }

        private static async Task BuildChannelDailyContext(
            ISystemContext ctx, StringBuilder sb, List<ReviewHint> hints)
        {
            sb.AppendLine("## 复盘模式：频道日报");
            sb.AppendLine("分析频道近期的消息和活动，提炼要点，发现模式。");
            sb.AppendLine();

            var channels = await ctx.Session.GetAllChannelsAsync();
            // 优先选有 hint 的频道，否则取第一个
            var targetChannelId = hints.FirstOrDefault(h => h.ChannelId != null)?.ChannelId
                ?? channels.FirstOrDefault()?.Id;
            if (targetChannelId == null) { sb.AppendLine("（无可用频道）"); return; }

            var channel = channels.FirstOrDefault(c => c.Id == targetChannelId.Value);
            sb.AppendLine($"### 目标频道: {channel?.Name ?? $"ID:{targetChannelId}"}");
            if (channel != null)
                sb.AppendLine($"当前亲和度: {channel.Affinity:F2}（影响群聊回复倾向，1.0为基准）");

            // 展示最近消息摘要
            var recentMessages = await ctx.Session.GetContextByChannelAsync(targetChannelId.Value, limit: 30);
            if (recentMessages.Count > 0)
            {
                sb.AppendLine($"### 最近消息 ({recentMessages.Count}条)");
                foreach (var msg in recentMessages.TakeLast(20))
                {
                    var name = msg.IsFromBot ? "Lilara"
                             : !string.IsNullOrEmpty(msg.SenderName) ? msg.SenderName
                             : "用户";
                    var preview = msg.Content.Length > 60 ? msg.Content[..60] + "..." : msg.Content;
                    sb.AppendLine($"- [{msg.Time:HH:mm}] {name}: {preview}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("### 亲和度调整指引");
            sb.AppendLine("分析完频道活动后，可用「更新亲和度」工具调整该频道的参与倾向：");
            sb.AppendLine("- 频道活跃且互动有价值 → 适当上调（+0.1 ~ +0.2）");
            sb.AppendLine("- 频道冷清或互动质量低 → 适当下调（-0.1 ~ -0.2）");
            sb.AppendLine("- 无明显变化 → 不调整");
        }

        private static async Task BuildPersonProfileContext(
            ISystemContext ctx, StringBuilder sb, List<ReviewHint> hints)
        {
            sb.AppendLine("## 复盘模式：人物回顾");
            sb.AppendLine("聚焦某个人物的近期互动，更新对其的理解。");
            sb.AppendLine();

            // 优先选有 hint 的人物
            var targetPersonId = hints.FirstOrDefault(h => h.PersonId != null)?.PersonId;
            if (targetPersonId == null)
            {
                // 取最近有记忆的人物
                var recentMemories = await ctx.Memories.GetRecentAsync(20);
                targetPersonId = recentMemories.FirstOrDefault(m => m.PersonId != null)?.PersonId;
            }
            if (targetPersonId == null) { sb.AppendLine("（无可用人物）"); return; }

            sb.AppendLine($"### 目标人物ID: {targetPersonId}");

            // 展示当前人物状态
            var person = await ctx.Session.GetPersonByIdAsync(targetPersonId.Value);
            if (person != null)
            {
                sb.AppendLine($"信任等级: {person.TrustLevel}, 好感度: {person.TrustProgress:F2}, 警报等级: {person.AlertLevel}");
                sb.AppendLine($"当前称呼: {(string.IsNullOrEmpty(person.Name) ? "（未设置）" : person.Name)}");
                sb.AppendLine($"别称: {(string.IsNullOrEmpty(person.Aliases) ? "（无）" : person.Aliases)}");
                sb.AppendLine($"快速记忆: {(string.IsNullOrEmpty(person.FastMemory) ? "（空）" : person.FastMemory)}");
                sb.AppendLine();

                // 展示关联账号的平台名
                var users = await ctx.Session.GetUsersByPersonIdAsync(targetPersonId.Value);
                if (users.Count > 0)
                {
                    sb.AppendLine("### 关联账号");
                    foreach (var u in users)
                        sb.AppendLine($"- {u.Platform}: {u.DisplayName} (ID: {u.PlatformId})");
                    sb.AppendLine();
                }
            }

            var personMemories = await ctx.Memories.GetByPersonAsync(targetPersonId.Value);
            if (personMemories.Count > 0)
            {
                sb.AppendLine($"### 相关记忆 ({personMemories.Count}条，显示最近10条)");
                foreach (var m in personMemories.TakeLast(10))
                    sb.AppendLine($"- [ID:{m.Id}] {m.Content}");
            }

            sb.AppendLine();
            sb.AppendLine("### 人物回顾指引");
            sb.AppendLine("回顾完成后：");
            sb.AppendLine("- 如果「当前称呼」为空或不合适（如平台ID、不雅昵称），用「更新人物称呼」工具选一个自然、适合日常对话的称呼，并记录已知别称");
            sb.AppendLine("- 用「更新快速记忆」工具更新此人的一句话概括（简明扼要，关键信息）");
            sb.AppendLine("- 用「调整好感度」工具根据互动质量调整好感度（正值=好感增加，负值=降低）");
            sb.AppendLine("- 将深入发现写入临时记忆");
        }

        private static async Task BuildCrossDomainContext(ISystemContext ctx, StringBuilder sb)
        {
            sb.AppendLine("## 复盘模式：跨域关联");
            sb.AppendLine("跨频道发现被忽略的联系和共同趋势。");
            sb.AppendLine();

            var channels = await ctx.Session.GetAllChannelsAsync();
            sb.AppendLine($"### 活跃频道 ({channels.Count}个)");
            foreach (var channel in channels)
            {
                sb.AppendLine($"- {channel.Name} (亲和度={channel.Affinity:F2})");
                var recent = await ctx.Session.GetContextByChannelAsync(channel.Id, limit: 5);
                foreach (var msg in recent)
                {
                    var name = msg.IsFromBot ? "Lilara"
                             : !string.IsNullOrEmpty(msg.SenderName) ? msg.SenderName
                             : "用户";
                    var preview = msg.Content.Length > 50 ? msg.Content[..50] + "..." : msg.Content;
                    sb.AppendLine($"  - [{msg.Time:HH:mm}] {name}: {preview}");
                }
            }
        }

        private static async Task BuildContradictionContext(ISystemContext ctx, StringBuilder sb)
        {
            sb.AppendLine("## 复盘模式：矛盾检测");
            sb.AppendLine("检查记忆库中是否存在互相矛盾的信息。");
            sb.AppendLine();

            var recentMemories = await ctx.Memories.GetRecentAsync(30);
            sb.AppendLine($"### 记忆库概况: 近期 {recentMemories.Count} 条记忆");

            var highImportance = recentMemories.Where(m => m.Importance >= 0.7f).ToList();
            if (highImportance.Count > 0)
            {
                sb.AppendLine($"### 高重要性记忆 ({highImportance.Count}条)");
                foreach (var m in highImportance.Take(15))
                    sb.AppendLine($"- [ID:{m.Id}] (重要度:{m.Importance:F2}) {m.Content}");
            }
        }

        private static string BuildResumeContext(
            ReviewInvestigation investigation, List<ReviewHint> hints)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 继续未完成的复盘");
            sb.AppendLine($"模式: {investigation.Mode}");
            sb.AppendLine($"上次保存时间: {investigation.SavedAt:yyyy-MM-dd HH:mm}");

            if (investigation.Findings.Count > 0)
            {
                sb.AppendLine("### 已有发现");
                foreach (var f in investigation.Findings)
                    sb.AppendLine($"- {f}");
            }
            if (investigation.NextSteps.Count > 0)
            {
                sb.AppendLine("### 待完成步骤");
                foreach (var s in investigation.NextSteps)
                    sb.AppendLine($"- {s}");
            }

            if (hints.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 新增复盘提示");
                foreach (var hint in hints)
                    sb.AppendLine($"- {hint.Content}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
