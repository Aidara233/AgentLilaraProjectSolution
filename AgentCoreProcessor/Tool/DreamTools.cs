using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 睡眠许可工具：授予大睡许可。
    /// 信号工具——实际处理由 ChannelEngine 通过 EventBus 发布 SignalEvent。
    /// </summary>
    internal class DreamPermissionTool : ITool
    {
        public string Name => "睡眠许可";
        public string Description => "授予大睡许可，允许系统在满足其他条件时进入深度睡眠";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public string? ToolGroup => "系统管理";
        public bool DefaultExpanded => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = "dream-permission"
            });
        }
    }

    /// <summary>
    /// 强制睡觉工具：立即触发大睡，跳过条件检查。
    /// </summary>
    internal class ForceSleepTool : ITool
    {
        public string Name => "强制睡觉";
        public string Description => "立即触发深度睡眠，跳过所有前置条件检查";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public string? ToolGroup => "系统管理";
        public bool DefaultExpanded => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = "force-sleep"
            });
        }
    }

    /// <summary>
    /// 修改睡眠配置工具：更新运行时做梦配置。
    /// </summary>
    internal class DreamConfigTool : ITool
    {
        public string Name => "修改睡眠配置";
        public string Description => "修改做梦调度配置（JSON格式），如走神冷却期、小睡阈值、大睡时间段等";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("配置JSON", "JSON格式的配置内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;
        public string? ToolGroup => "系统管理";
        public bool DefaultExpanded => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "配置内容不能为空"
                });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }

    /// <summary>
    /// 调整睡意工具：设置运行时评分偏移。正值=更困，负值=更清醒。
    /// </summary>
    internal class SleepScoreTool : ITool
    {
        public string Name => "调整睡意";
        public string Description => "调整睡意偏移值（正值=更困，负值=更清醒），影响黄色评分总分。负值会取消已有的睡眠计划";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("偏移值", "浮点数，如 3.0 或 -5.0", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public string? ToolGroup => "系统管理";
        public bool DefaultExpanded => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "偏移值不能为空" });

            if (!float.TryParse(resolvedInputs[0], out _))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "偏移值必须是数字" });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }

    /// <summary>
    /// 触发红色警报工具：跳过黄色评分，直接进入许可等待阶段。
    /// </summary>
    internal class RedAlertTool : ITool
    {
        public string Name => "触发红色警报";
        public string Description => "触发深度睡眠红色警报，跳过黄色评分直接进入许可等待阶段";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;
        public string? ToolGroup => "系统管理";
        public bool DefaultExpanded => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = "red-alert"
            });
        }
    }
}
