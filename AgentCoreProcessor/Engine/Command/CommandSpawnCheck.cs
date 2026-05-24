using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Command;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 命令拦截 SpawnCheck。检测命令前缀消息，执行命令，标记事件已消费。
    /// 支持交互式命令会话：活跃会话拦截优先于命令前缀解析。
    /// 永远不创建引擎实例（ShouldSpawnAsync 始终返回 false）。
    /// </summary>
    internal class CommandSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Command";

        private readonly CommandConfig config;
        private readonly Dictionary<string, CommandSession> _sessions = new();
        private const int SessionTimeoutSeconds = 120;

        public CommandSpawnCheck()
        {
            var configPath = Path.Combine(PathConfig.StoragePath, "Command", "CommandConfig.json");
            config = CommandConfig.Load(configPath);
        }

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            // 清理超时会话
            var expired = _sessions
                .Where(kv => (DateTime.Now - kv.Value.LastActivity).TotalSeconds > SessionTimeoutSeconds)
                .Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _sessions.Remove(key);
            }
            return Task.CompletedTask;
        }

        public async Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is not MessageEvent msgEvent) return false;

            var content = msgEvent.Message.Content?.Trim();
            if (string.IsNullOrEmpty(content)) return false;

            var sessionKey = $"{msgEvent.Message.Platform}:{msgEvent.Message.PlatformUserId}";

            // ---- 1. 活跃会话拦截 ----
            if (_sessions.TryGetValue(sessionKey, out var session))
            {
                // /cancel 退出会话
                if (content.Equals($"{config.Prefix}cancel", StringComparison.OrdinalIgnoreCase))
                {
                    _sessions.Remove(sessionKey);
                    await Reply(ctx, msgEvent.Message, "已取消。");
                    e.Consumed = true;
                    return false;
                }

                await HandleSessionInput(ctx, msgEvent.Message, session, content);
                e.Consumed = true;
                return false;
            }

            // ---- 2. 命令前缀解析 ----
            if (!content.StartsWith(config.Prefix)) return false;

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

            // 轻量用户解析
            var (user, person) = await ctx.Session.ResolveUserAsync(msgEvent.Message);

            // 权限检查
            if (user.PermissionLevel < command.RequiredPermission)
            {
                await Reply(ctx, msgEvent.Message,
                    $"权限不足: /{name} 需要 {command.RequiredPermission} 权限。");
                e.Consumed = true;
                return false;
            }

            var context = new CommandContext
            {
                User = user,
                Person = person,
                Message = msgEvent.Message,
                SystemContext = ctx
            };

            // ---- 2a. 交互式命令，无参数 → 创建会话 ----
            if (command is IInteractiveCommand interactive && string.IsNullOrEmpty(args))
            {
                var newSession = new CommandSession
                {
                    SessionKey = sessionKey,
                    Command = interactive,
                    Context = context,
                    CurrentStep = 0
                };
                _sessions[sessionKey] = newSession;
                await Reply(ctx, msgEvent.Message, FormatStepPrompt(interactive.Steps[0]));
                e.Consumed = true;
                return false;
            }

            // ---- 2b/2c. 一次性执行 ----
            try
            {
                var result = await command.ExecuteAsync(args, context);
                if (!string.IsNullOrEmpty(result.Response))
                    await Reply(ctx, msgEvent.Message, result.Response);
            }
            catch (Exception ex)
            {
                await Reply(ctx, msgEvent.Message, $"命令异常: {ex.Message}");
            }

            e.Consumed = true;
            return false;
        }

        private async Task HandleSessionInput(ISystemContext ctx, IncomingMessage msg,
            CommandSession session, string input)
        {
            session.LastActivity = DateTime.Now;
            var step = session.Command.Steps[session.CurrentStep];

            // 选项校验
            if (step.Options != null)
            {
                var match = step.Options.FirstOrDefault(o =>
                    o.Equals(input, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    await Reply(ctx, msg, $"无效选项。{FormatStepPrompt(step)}");
                    return;
                }
                input = match; // 规范化大小写
            }

            // 自定义校验
            if (step.Validate != null)
            {
                var error = step.Validate(input);
                if (error != null)
                {
                    await Reply(ctx, msg, $"{error}\n{FormatStepPrompt(step)}");
                    return;
                }
            }

            // 存值，推进
            session.Data[step.Key] = input;
            session.CurrentStep++;

            // 还有下一步
            if (session.CurrentStep < session.Command.Steps.Count)
            {
                var nextStep = session.Command.Steps[session.CurrentStep];
                await Reply(ctx, msg, FormatStepPrompt(nextStep));
                return;
            }

            // 所有步骤完成 → 执行
            _sessions.Remove(session.SessionKey);
            try
            {
                var result = await session.Command.ExecuteInteractiveAsync(
                    session.Data, session.Context);
                if (!string.IsNullOrEmpty(result.Response))
                    await Reply(ctx, msg, result.Response);
            }
            catch (Exception ex)
            {
                await Reply(ctx, msg, $"命令异常: {ex.Message}");
            }
        }

        private static string FormatStepPrompt(CommandStep step)
        {
            var sb = new StringBuilder();
            sb.Append(step.Prompt);
            if (step.Options != null && step.Options.Count > 0)
            {
                sb.AppendLine();
                sb.Append("可选: ");
                sb.Append(string.Join(" / ", step.Options));
            }
            return sb.ToString();
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