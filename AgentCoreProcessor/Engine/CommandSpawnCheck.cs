using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Command;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 命令拦截 SpawnCheck。检测命令前缀消息，执行命令，标记事件已消费。
    /// 永远不创建引擎实例（ShouldSpawnAsync 始终返回 false）。
    /// </summary>
    internal class CommandSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Command";

        private readonly CommandConfig config;

        public CommandSpawnCheck()
        {
            var configPath = Path.Combine(PathConfig.StoragePath, "Command", "CommandConfig.json");
            config = CommandConfig.Load(configPath);
        }

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

        public async Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is not MessageEvent msgEvent) return false;

            var content = msgEvent.Message.Content?.Trim();
            if (string.IsNullOrEmpty(content) || !content.StartsWith(config.Prefix))
                return false;

            // 解析: "/help foo bar" → name="help", args="foo bar"
            var body = content[config.Prefix.Length..].TrimStart();
            var spaceIdx = body.IndexOf(' ');
            var name = spaceIdx < 0 ? body : body[..spaceIdx];
            var args = spaceIdx < 0 ? "" : body[(spaceIdx + 1)..].Trim();

            if (string.IsNullOrEmpty(name)) return false;

            var command = CommandRegistry.Get(name);
            if (command == null)
            {
                await Reply(ctx, msgEvent.Message,
                    $"未知命令: {config.Prefix}{name}，输入 {config.Prefix}help 查看可用命令。");
                e.Consumed = true;
                return false;
            }

            // 轻量用户解析（不走话题/消息管线）
            var (user, person) = await ctx.Session.ResolveUserAsync(msgEvent.Message);

            // 权限检查
            if (user.PermissionLevel < command.RequiredPermission)
            {
                FrameworkLogger.Log("Command",
                    $"权限不足: /{name} 需要 {command.RequiredPermission}，用户 {user.PlatformId} 为 {user.PermissionLevel}");
                await Reply(ctx, msgEvent.Message,
                    $"权限不足: /{name} 需要 {command.RequiredPermission} 权限。");
                e.Consumed = true;
                return false;
            }

            // 构建上下文并执行
            var context = new CommandContext
            {
                User = user,
                Person = person,
                Message = msgEvent.Message,
                SystemContext = ctx
            };

            try
            {
                var result = await command.ExecuteAsync(args, context);
                if (!string.IsNullOrEmpty(result.Response))
                    await Reply(ctx, msgEvent.Message, result.Response);
                FrameworkLogger.Log("Command",
                    $"/{name} user={user.PlatformId} success={result.Success}");
            }
            catch (Exception ex)
            {
                await Reply(ctx, msgEvent.Message, $"命令异常: {ex.Message}");
                FrameworkLogger.Log("Command", $"/{name} 异常: {ex.Message}");
            }

            e.Consumed = true;
            return false;
        }

        public ISubEngine Create(ISystemContext ctx)
            => throw new NotSupportedException("CommandSpawnCheck 不创建引擎实例");

        private static Task Reply(ISystemContext ctx, IncomingMessage src, string text)
            => ctx.Adapters.SendMessageAsync(src.Platform, new OutgoingMessage
            {
                ChannelId = src.ChannelId,
                Content = text
            });
    }
}
