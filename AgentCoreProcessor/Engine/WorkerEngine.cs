using System;
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

        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();
        private readonly PreprocessingCore preprocessingCore;

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
                // 1. 构建 SessionContext
                var context = await ctx.Session.OnMessageAsync(message);

                // 2. 权限检查
                switch (context.User.PermissionLevel)
                {
                    case PermissionLevel.Blocked:
                        FrameworkLogger.LogPermission("WorkerEngine", context.User.PlatformId, "Blocked", false);
                        return;
                    case PermissionLevel.Restricted:
                        FrameworkLogger.LogPermission("WorkerEngine", context.User.PlatformId, "Restricted", false);
                        return;
                }
                FrameworkLogger.Log("WorkerEngine",
                    $"消息处理: user={context.User.PlatformId} person={context.Person.Id} channel={context.Channel.Id} topic={context.Topic.Id}");

                // 3. 二分类（embedding）
                var isTask = await preprocessingCore.IsTaskAsync(message.Content);
                FrameworkLogger.Log("WorkerEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");

                // 4. 检索记忆
                string? memoryContext = await BuildMemoryContextAsync(context);

                // 5. 路由处理
                if (isTask)
                {
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
                    await workingCore.ProcessAsync(message.Content, memoryContext);
                }
                else
                {
                    // 聊天 → ExpressCore 直接回复
                    expressCore.ResetProcessor();
                    var input = memoryContext != null
                        ? $"{message.Content}\n\n[记忆参考]\n{memoryContext}"
                        : message.Content;
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

        private async Task<string?> BuildMemoryContextAsync(SessionContext context)
        {
            try
            {
                var results = await ctx.MemorySvc.RecallAsync(
                    context.Person.Id, context.Channel.Id, context.Topic.Id,
                    message.Content, topK: 10, includeLinks: true);

                if (results.Count == 0) return null;

                FrameworkLogger.LogMemoryRecall("WorkerEngine",
                    results.Count, results.Count(r => r.IsTemp));

                var sb = new StringBuilder();
                foreach (var m in results)
                    sb.AppendLine($"- {m.Content}");
                return sb.ToString().TrimEnd();
            }
            catch { return null; }
        }
    }
}