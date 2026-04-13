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

        private readonly PreprocessingCore preprocessingCore = new();
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();

        public WorkerEngine(ISystemContext ctx, IncomingMessage message)
        {
            this.ctx = ctx;
            this.message = message;
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

                // 3. 分类
                var category = await preprocessingCore.ClassifyAsync(message.Content);
                FrameworkLogger.LogClassification("WorkerEngine", category);

                // 4. 检索记忆
                string? memoryContext = await BuildMemoryContextAsync(context,
                    includeLinks: category >= 3);

                // 5. 路由处理
                switch (category)
                {
                    case 1:
                    case 2:
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
                        break;

                    case 3:
                    case 4:
                        workingCore.OnSpeak = async (rawText) =>
                        {
                            var polished = await expressCore.PolishAsync(message.Content, rawText);
                            await ctx.Adapters.SendMessageAsync(message.Platform, new OutgoingMessage
                            {
                                ChannelId = message.ChannelId,
                                Content = polished
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
                        break;

                    default:
                        expressCore.ResetProcessor();
                        var defaultExpressed = await expressCore.GenerateOnceAsync(message.Content);
                        await ctx.Adapters.SendMessageAsync(message.Platform, new OutgoingMessage
                        {
                            ChannelId = message.ChannelId,
                            Content = defaultExpressed
                        });
                        break;
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

        public void OnEvent(EngineEvent e)
        {
            // 可转发信号给 WorkingCore（如果在 Agent 循环中）
        }

        public void RequestStop() => IsAlive = false;

        private async Task<string?> BuildMemoryContextAsync(SessionContext context, bool includeLinks)
        {
            try
            {
                var results = await ctx.MemorySvc.RecallAsync(
                    context.Person.Id, context.Channel.Id, context.Topic.Id,
                    message.Content, topK: 10, includeLinks: includeLinks);

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
