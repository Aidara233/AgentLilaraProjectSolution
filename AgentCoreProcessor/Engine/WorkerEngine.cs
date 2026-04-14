using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine
{
    internal class WorkerEngine : ISubEngine
    {
        public string EngineType => "Worker";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly IncomingMessage message;
        private readonly SessionContext? prebuiltContext;
        private readonly string? mergedContent;
        private readonly List<ScoredMemory>? preloadedMemory;

        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();
        private readonly PreprocessingCore preprocessingCore;

        /// <summary>由 TopicEngine 孵化时使用。SessionContext 已构建，权限已检查。</summary>
        public WorkerEngine(ISystemContext ctx, IncomingMessage message,
                            SessionContext sessionContext, string mergedContent,
                            List<ScoredMemory>? preloadedMemory = null)
        {
            this.ctx = ctx;
            this.message = message;
            this.prebuiltContext = sessionContext;
            this.mergedContent = mergedContent;
            this.preloadedMemory = preloadedMemory;
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);
        }

        public WorkerEngine(ISystemContext ctx, IncomingMessage message)
        {
            this.ctx = ctx;
            this.message = message;
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);
        }

        public async Task RunAsync()
        {
            try
            {
                // 1. 构建 SessionContext（TopicEngine 孵化时已预构建）
                var context = prebuiltContext ?? await ctx.Session.OnMessageAsync(message);
                var content = mergedContent ?? message.Content;

                FrameworkLogger.Log("WorkerEngine",
                    $"消息处理: user={context.User.PlatformId} person={context.Person.Id} channel={context.Channel.Id} topic={context.Topic.Id}");

                // 2. 二分类（embedding）
                var isTask = await preprocessingCore.IsTaskAsync(content);
                FrameworkLogger.Log("WorkerEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");

                // 3. 获取记忆（优先使用预加载）
                var memoryResults = preloadedMemory
                    ?? await BuildMemoryResultsAsync(context, content);

                // 4. 路由处理
                if (isTask)
                {
                    string? memoryContext = FormatMemory(memoryResults, topK: 10);

                    // 任务 → WorkingCore Agent 循环
                    workingCore.OnSpeak = async (rawText) =>
                    {
                        await ctx.Adapters.SendMessageAsync(message.Platform, new OutgoingMessage
                        {
                            ChannelId = message.ChannelId,
                            Content = rawText
                        });
                    };
                    workingCore.OnMemory = async (content) =>
                    {
                        await ctx.MemorySvc.StoreAsync(content,
                            context.Person.Id, context.Channel.Id, context.Topic.Id);
                    };
                    workingCore.OnSignal = async (signalName, payload) =>
                    {
                        ctx.EventBus.PublishSignal(signalName, payload);
                        await Task.CompletedTask;
                    };
                    workingCore.OnReviewHint = async (content) =>
                    {
                        await ctx.ReviewHints.CreateAsync(content,
                            context.Person.Id, context.Channel.Id, context.Topic.Id);
                    };
                    await workingCore.ProcessAsync(content, memoryContext);
                }
                else
                {
                    string? memoryContext = FormatMemory(memoryResults, topK: 5);

                    // 聊天 → ExpressCore 直接回复
                    expressCore.ResetProcessor();
                    var input = memoryContext != null
                        ? $"{content}\n\n[记忆参考]\n{memoryContext}"
                        : content;
                    var expressed = await expressCore.GenerateOnceAsync(input);
                    await ctx.Adapters.SendMessageAsync(message.Platform, new OutgoingMessage
                    {
                        ChannelId = message.ChannelId,
                        Content = expressed
                    });
                }
            }
            catch (Exception ex)
            {
                await ctx.Adapters.SendMessageAsync(message.Platform, new OutgoingMessage
                {
                    ChannelId = message.ChannelId,
                    Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                });
            }
            finally
            {
                IsAlive = false;
            }
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop() => IsAlive = false;

        /// <summary>查询记忆，返回原始结果列表（无预加载缓存时的 fallback）。</summary>
        private async Task<List<ScoredMemory>?> BuildMemoryResultsAsync(SessionContext context, string content)
        {
            try
            {
                var results = await ctx.MemorySvc.RecallAsync(
                    context.Person.Id, context.Channel.Id, context.Topic.Id,
                    content, topK: 10, includeLinks: true);

                if (results.Count > 0)
                    FrameworkLogger.LogMemoryRecall("WorkerEngine",
                        results.Count, results.Count(r => r.IsTemp));

                return results;
            }
            catch { return null; }
        }

        /// <summary>将记忆结果格式化为注入文本。</summary>
        private static string? FormatMemory(List<ScoredMemory>? results, int topK)
        {
            if (results == null || results.Count == 0) return null;

            var sb = new StringBuilder();
            foreach (var m in results.Take(topK))
                sb.AppendLine($"- {m.Content}");
            return sb.ToString().TrimEnd();
        }
    }
}