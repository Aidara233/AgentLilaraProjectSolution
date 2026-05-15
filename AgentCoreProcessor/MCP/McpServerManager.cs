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
                    }
                }
                catch (Exception)
                {
                }
            }

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
