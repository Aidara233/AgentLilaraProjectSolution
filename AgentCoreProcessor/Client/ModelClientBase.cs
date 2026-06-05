using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Client
{
    public abstract class ModelClientBase : IModelClient
    {
        protected ApiClientCfg apiClientCfg;
        protected List<ToolDefinition>? tools;
        private readonly List<Message> _conversationHistory = new();

        public ApiClientCfg Config
        {
            get => apiClientCfg;
            set => apiClientCfg = value ?? new ApiClientCfg();
        }

        protected ModelClientBase()
        {
            apiClientCfg = new ApiClientCfg();
        }

        protected ModelClientBase(ApiClientCfg cfg)
        {
            apiClientCfg = cfg ?? new ApiClientCfg();
        }

        // ── 对话历史管理 ──

        public IModelClient AddMessage(string role, string content, string? name = null)
        {
            _conversationHistory.Add(new Message
            {
                Role = role,
                Content = content,
                Name = name
            });
            return this;
        }

        public IModelClient AddUserMessage(string content, string? name = null)
            => AddMessage("user", content, name);

        public IModelClient AddAssistantMessage(string content, string? name = null)
            => AddMessage("assistant", content, name);

        public IModelClient AddSystemMessage(string content, string? name = null)
            => AddMessage("system", content, name);

        public IModelClient AddMultimodalMessage(string role, string text, List<string> imagePaths)
        {
            var parts = new List<ContentPart>();
            if (!string.IsNullOrEmpty(text))
                parts.Add(ContentPart.FromText(text));
            foreach (var path in imagePaths.Where(p => !string.IsNullOrEmpty(p)))
                parts.Add(ContentPart.FromImagePath(path));

            _conversationHistory.Add(new Message
            {
                Role = role,
                Content = text ?? "",
                ContentParts = parts.Count > 0 ? parts : null
            });
            return this;
        }

        public IModelClient ClearConversationHistory()
        {
            _conversationHistory.Clear();
            return this;
        }

        public List<Message> GetConversationHistory()
        {
            return new List<Message>(_conversationHistory);
        }

        public IModelClient SetConversationHistory(List<Message> history)
        {
            _conversationHistory.Clear();
            _conversationHistory.AddRange(history);
            return this;
        }

        public IModelClient RemoveLastMessage()
        {
            if (_conversationHistory.Count > 0)
                _conversationHistory.RemoveAt(_conversationHistory.Count - 1);
            return this;
        }

        public int GetHistoryCount()
            => _conversationHistory.Count;

        // ── 子类实现 ──

        public abstract Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default);

        // ── 原生工具调用（默认实现，子类覆盖） ──

        public virtual IModelClient SetTools(List<ToolDefinition> tools)
        {
            this.tools = tools;
            return this;
        }

        public virtual List<ToolDefinition>? GetTools() => tools;

        public virtual Task StreamChatWithToolsAsync(Action<StreamEvent> onEvent, CancellationToken ct = default)
            => throw new NotSupportedException($"Provider {apiClientCfg.Provider} 不支持原生工具调用");

        public virtual void AddToolResult(string toolUseId, string result, bool isError = false, List<string>? imagePaths = null) { }

        public virtual void Dispose() { }
    }
}