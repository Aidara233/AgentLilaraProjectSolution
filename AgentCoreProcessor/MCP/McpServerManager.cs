using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.MCP
{
    internal class McpServerManager : IAsyncDisposable
    {
        private readonly string _configPath;
        private readonly List<McpServerConnection> _connections = new();

        public McpServerManager(string configPath)
        {
            _configPath = configPath;
        }

        public IReadOnlyList<McpServerConnection> Connections => _connections;

        public async Task InitAsync()
        {
            var config = McpConfig.Load(_configPath);
            var enabled = config.Servers.Where(s => s.Enabled).ToList();

            if (enabled.Count == 0)
            {
                FrameworkLogger.Log("MCP", "无已启用的 MCP Server 配置");
                return;
            }

            int totalTools = 0;
            foreach (var entry in enabled)
            {
                try
                {
                    var conn = new McpServerConnection(entry);
                    await conn.ConnectAsync();
                    _connections.Add(conn);

                    foreach (var tool in conn.Tools)
                    {
                        if (ToolRegistry.Register(tool))
                            totalTools++;
                        else
                            FrameworkLogger.Log("MCP", $"工具名冲突，跳过: {tool.Name}");
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("MCP", ex, $"Server [{entry.Id}] 连接失败");
                }
            }

            FrameworkLogger.Log("MCP", $"已连接 {_connections.Count}/{enabled.Count} 个 Server，注册 {totalTools} 个工具");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var conn in _connections)
            {
                foreach (var tool in conn.Tools)
                    ToolRegistry.Unregister(tool.Name);

                await conn.DisposeAsync();
            }
            _connections.Clear();
        }
    }
}
