using System.Collections.Generic;

namespace AgentCoreProcessor.WebUI.Navigation
{
    internal static class NavConfig
    {
        public static List<NavItem> Items { get; } = new()
        {
            new() { Title = "总览", Icon = "bi-speedometer2", Href = "/" },

            new() { Title = "引擎", Icon = "bi-cpu", Children = new()
            {
                new() { Title = "系统循环", Icon = "bi-arrow-repeat", Children = new()
                {
                    new() { Title = "概览", Href = "/engine/system" },
                    new() { Title = "任务队列", Href = "/engine/system/tasks" },
                    new() { Title = "子agent", Href = "/engine/system/agents" },
                    new() { Title = "睡觉请求", Href = "/engine/system/sleep" },
                }},
                new() { Title = "频道循环", Icon = "bi-diagram-3", Children = new()
                {
                    new() { Title = "频道列表", Href = "/engine/channels" },
                }},
                new() { Title = "做梦", Icon = "bi-moon-stars", Children = new()
                {
                    new() { Title = "状态", Href = "/dream" },
                    new() { Title = "配置", Href = "/dream/config" },
                    new() { Title = "历史", Href = "/dream/history" },
                }},
                new() { Title = "视觉", Icon = "bi-eye", Href = "/engine/vision" },
                new() { Title = "引擎管理", Icon = "bi-gear", Href = "/engine/manage" },
            }},

            new() { Title = "记忆", Icon = "bi-brain", Children = new()
            {
                new() { Title = "主库浏览", Href = "/memories" },
                new() { Title = "关联图谱", Href = "/memories/graph" },
                new() { Title = "临时库", Href = "/memories/temp" },
                new() { Title = "人物", Href = "/people" },
            }},

            new() { Title = "消息", Icon = "bi-chat-dots", Children = new()
            {
                new() { Title = "消息历史", Href = "/messages" },
                new() { Title = "控制台", Href = "/console" },
            }},

            new() { Title = "图片", Icon = "bi-image", Href = "/images" },

            new() { Title = "适配器", Icon = "bi-plug", Children = new()
            {
                new() { Title = "概览", Href = "/adapters" },
                new() { Title = "OneBot", Href = "/adapters/onebot" },
                new() { Title = "File", Href = "/adapters/file" },
            }},

            new() { Title = "系统", Icon = "bi-wrench", Children = new()
            {
                new() { Title = "日志", Href = "/logs" },
                new() { Title = "模型日志", Href = "/logs/model" },
                new() { Title = "Token 统计", Href = "/logs/tokens" },
                new() { Title = "工具管理", Href = "/config/tools" },
                new() { Title = "配置", Href = "/config" },
                new() { Title = "MCP", Href = "/mcp" },
            }},
        };
    }
}
