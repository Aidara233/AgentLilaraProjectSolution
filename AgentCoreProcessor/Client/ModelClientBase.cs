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
        protected readonly HttpClient httpClient;
        protected ApiClientCfg apiClientCfg;
        protected List<ToolDefinition>? tools;

        public ApiClientCfg Config
        {
            get => apiClientCfg;
            set => apiClientCfg = value ?? new ApiClientCfg();
        }

        protected ModelClientBase()
        {
            httpClient = new HttpClient();
            apiClientCfg = new ApiClientCfg();
        }

        protected ModelClientBase(ApiClientCfg cfg)
        {
            httpClient = new HttpClient();
            apiClientCfg = cfg ?? new ApiClientCfg();
        }

        // ── 对话历史管理 ──

        public IModelClient AddMessage(string role, string content, string? name = null)
        {
            apiClientCfg.ConversationHistory.Add(new Message
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

            apiClientCfg.ConversationHistory.Add(new Message
            {
                Role = role,
                Content = text ?? "",
                ContentParts = parts.Count > 0 ? parts : null
            });
            return this;
        }

        public IModelClient ClearConversationHistory()
        {
            apiClientCfg.ConversationHistory.Clear();
            return this;
        }

        public List<Message> GetConversationHistory()
        {
            var merged = new List<Message>(apiClientCfg.PresetMessages.Count + apiClientCfg.ConversationHistory.Count);
            merged.AddRange(apiClientCfg.PresetMessages);
            merged.AddRange(apiClientCfg.ConversationHistory);
            return merged;
        }

        public IModelClient SetConversationHistory(List<Message> history)
        {
            apiClientCfg.ConversationHistory.Clear();
            apiClientCfg.ConversationHistory.AddRange(history);
            return this;
        }

        public IModelClient RemoveLastMessage()
        {
            if (apiClientCfg.ConversationHistory.Count > 0)
                apiClientCfg.ConversationHistory.RemoveAt(apiClientCfg.ConversationHistory.Count - 1);
            return this;
        }

        public int GetHistoryCount()
            => apiClientCfg.PresetMessages.Count + apiClientCfg.ConversationHistory.Count;

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

        public virtual void AddToolResult(string toolUseId, string result, bool isError = false) { }

        public virtual void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}