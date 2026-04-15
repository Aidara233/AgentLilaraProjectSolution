using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Client
{
    public abstract class ModelClientBase : IModelClient
    {
        protected readonly HttpClient httpClient;
        protected ApiClientCfg apiClientCfg;

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

        public virtual void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}