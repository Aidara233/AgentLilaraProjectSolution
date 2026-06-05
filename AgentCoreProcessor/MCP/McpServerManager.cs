using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
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
            await ConnectAllAsync();
        }

        public async Task ReloadAsync()
        {
            await DisconnectAllAsync();
            await ConnectAllAsync();
        }

        private async Task ConnectAllAsync()
        {
            var config = McpConfig.Load(_configPath);
            var enabled = config.Servers.Where(s => s.Enabled).ToList();

            if (enabled.Count == 0)
            {
                return;
            }

            int totalTools = 0;
            var connectTasks = enabled.Select(async entry =>
            {
                try
                {
                    var conn = new McpServerConnection(entry);
                    await conn.ConnectAsync();

                    lock (_connections)
                    {
                        _connections.Add(conn);
                    }

                    int added = 0;
                    foreach (var tool in conn.Tools)
                    {
                        if (ToolRegistry.Register(tool, isNonComponent: true))
                            added++;
                    }
                    Interlocked.Add(ref totalTools, added);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Adapter, "MCP服务器连接失败", new { server = entry.Name, error = ex.Message });
                }
            });

            await Task.WhenAll(connectTasks);
        }

        private async Task DisconnectAllAsync()
        {
            foreach (var conn in _connections)
            {
                foreach (var tool in conn.Tools)
                    ToolRegistry.Unregister(tool.Name);
                await conn.DisposeAsync();
            }
            _connections.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAllAsync();
        }
    }
}
