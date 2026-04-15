using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;

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
        IModelClient ClearConversationHistory();
        List<Message> GetConversationHistory();
        IModelClient SetConversationHistory(List<Message> history);
        IModelClient RemoveLastMessage();
        int GetHistoryCount();

        // 流式生成
        Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default);
    }
}
