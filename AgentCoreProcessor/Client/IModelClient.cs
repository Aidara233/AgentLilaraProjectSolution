using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Client
{
    public interface IModelClient : IDisposable
    {
        ApiClientCfg Config { get; set; }

        // 对话历史管理
        IModelClient AddMessage(string role, string content, string? name = null);
        IModelClient AddUserMessage(string content, string? name = null);
        IModelClient AddAssistantMessage(string content, string? name = null);
        IModelClient AddSystemMessage(string content, string? name = null);
        IModelClient AddMultimodalMessage(string role, string text, List<string> imagePaths);
        IModelClient ClearConversationHistory();
        List<Message> GetConversationHistory();
        IModelClient SetConversationHistory(List<Message> history);
        IModelClient RemoveLastMessage();
        int GetHistoryCount();

        // 流式生成
        Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default);

        // 原生工具调用
        IModelClient SetTools(List<ToolDefinition> tools);
        List<ToolDefinition>? GetTools();
        Task StreamChatWithToolsAsync(Action<StreamEvent> onEvent, CancellationToken ct = default);
        void AddToolResult(string toolUseId, string result, bool isError = false, List<string>? imagePaths = null);
    }
}
