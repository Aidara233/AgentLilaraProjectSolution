using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// mind_status 工具：查看心智状态。
/// 展示当前情绪五轴（兴奋/喜悦/敏觉/满足/积极），可选列出最近 N 条情绪快照。
/// 只读情绪状态，不做任何写入。
/// </summary>
[ToolMeta(Group = "mind")]
public class MindStatusTool : ITool
{
    private readonly IToolContext _ctx;

    public MindStatusTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "mind_status";
    public string Description => "查看心智状态（情绪五轴 + 记忆概览）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("snapshots", "最近情绪快照条数（可选，>0 时列出）", 0, false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 获取情绪服务；心智引擎未启动时 Require 会抛异常，捕获后返回 failed
        IEmotionAccess emotion;
        try
        {
            emotion = _ctx.Require<IEmotionAccess>();
        }
        catch (Exception)
        {
            return new ToolResult { Status = "failed", Error = "情绪服务不可用（心智引擎未启动？）" };
        }

        int snapshots = 0;
        if (inputs.Count > 0 && int.TryParse(inputs[0], out var n))
            snapshots = Math.Clamp(n, 0, 100);

        try
        {
            var state = await emotion.GetCurrentAsync();

            var lines = new List<string>
            {
                $"当前情绪：兴奋 {F(state.Excitement)} / 喜悦 {F(state.Joy)} / 敏觉 {F(state.Alertness)} / 满足 {F(state.Satisfaction)} / 积极 {F(state.Optimism)}（更新于 {state.LastUpdatedAt:yyyy-MM-dd HH:mm}）"
            };

            if (snapshots > 0)
            {
                var list = await emotion.GetRecentSnapshotAsync(snapshots);
                if (list.Count == 0)
                {
                    lines.Add("暂无情绪快照。");
                }
                else
                {
                    lines.Add($"最近 {list.Count} 条情绪快照（时间倒序）：");
                    foreach (var s in list)
                    {
                        var src = string.IsNullOrWhiteSpace(s.SourceRef) ? "" : $"（{s.SourceRef}）";
                        lines.Add($"[{s.CreatedAt:yyyy-MM-dd HH:mm}] 兴奋 {F(s.Excitement)} / 喜悦 {F(s.Joy)} / 敏觉 {F(s.Alertness)} / 满足 {F(s.Satisfaction)} / 积极 {F(s.Optimism)}{src}");
                    }
                }
            }

            return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"读取情绪状态失败: {ex.Message}" };
        }
    }

    /// <summary>五轴数值格式化为两位小数（固定使用 . 小数点，避免区域设置差异）。</summary>
    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
