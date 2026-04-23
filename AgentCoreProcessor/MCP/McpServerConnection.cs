using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using ModelContextProtocol.Client;

namespace AgentCoreProcessor.MCP
{
    internal class McpServerConnection : IAsyncDisposable
    {
        private readonly McpServerEntry _entry;
        private McpClient? _client;
        private readonly List<McpBridgeTool> _tools = new();

        public string ServerId => _entry.Id;
        public string ServerName => _entry.Name;
        public bool IsConnected => _client != null;
        public IReadOnlyList<McpBridgeTool> Tools => _tools;

        public McpServerConnection(McpServerEntry entry)
        {
            _entry = entry;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            IClientTransport transport;

            if (_entry.Transport.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_entry.Url))
                    throw new InvalidOperationException($"MCP Server [{_entry.Id}] transport=http 但未配置 url");

                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_entry.Url),
                    Name = _entry.Id
                });
            }
            else
            {
                if (string.IsNullOrEmpty(_entry.Command))
                    throw new InvalidOperationException($"MCP Server [{_entry.Id}] transport=stdio 但未配置 command");

                var opts = new StdioClientTransportOptions
                {
                    Command = _entry.Command,
                    Name = _entry.Id
                };

                if (_entry.Args.Count > 0 && opts.Arguments != null)
                    foreach (var arg in _entry.Args) opts.Arguments.Add(arg);

                if (_entry.Env.Count > 0 && opts.EnvironmentVariables != null)
                    foreach (var kv in _entry.Env) opts.EnvironmentVariables[kv.Key] = kv.Value;

                transport = new StdioClientTransport(opts);
            }

            var clientOptions = new McpClientOptions
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "AgentLilara",
                    Version = "1.0.0"
                }
            };

            _client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: ct);

            var toolList = await _client.ListToolsAsync(cancellationToken: ct);
            _tools.Clear();
            foreach (var mcpTool in toolList)
                _tools.Add(new McpBridgeTool(mcpTool, _entry));

            FrameworkLogger.Log("MCP", $"[{_entry.Id}] 已连接，发现 {_tools.Count} 个工具");
        }

        public async ValueTask DisposeAsync()
        {
            if (_client != null)
            {
                try { await _client.DisposeAsync(); }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("MCP", $"[{_entry.Id}] 断开连接异常: {ex.Message}");
                }
                _client = null;
            }
            _tools.Clear();
            FrameworkLogger.Log("MCP", $"[{_entry.Id}] 已断开");
        }
    }
}
