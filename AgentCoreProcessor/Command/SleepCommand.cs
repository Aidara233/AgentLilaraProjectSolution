using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 手动触发睡眠 + 睡觉许可管理。
    /// 用法:
    ///   /sleep [daydream|nap|deepsleep] - 强制睡觉
    ///   /sleep approve <requestId> - 批准睡觉请求
    ///   /sleep deny <requestId> - 拒绝睡觉请求
    /// 无参数时进入交互选择。
    /// </summary>
    internal class SleepCommand : IInteractiveCommand
    {
        public string Name => "sleep";
        public string Description => "手动触发睡眠或管理睡觉请求";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        private static readonly List<string> LevelOptions = new() { "daydream", "nap", "deepsleep" };

        public List<CommandStep> Steps => new()
        {
            new()
            {
                Key = "level",
                Prompt = "选择睡眠等级:",
                Options = LevelOptions
            }
        };

        /// <summary>有参数时一次性执行。</summary>
        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

            return sub switch
            {
                "daydream" or "nap" or "deepsleep" => DoSleep(sub, context),
                "approve" => HandleApprove(parts, context),
                "deny" => HandleDeny(parts, context),
                "" => Task.FromResult(CommandResult.Fail("用法: /sleep [daydream|nap|deepsleep|approve|deny]")),
                _ => Task.FromResult(CommandResult.Fail($"未知子命令: {sub}"))
            };
        }

        /// <summary>交互完成后执行。</summary>
        public Task<CommandResult> ExecuteInteractiveAsync(
            Dictionary<string, string> data, CommandContext context)
        {
            return DoSleep(data["level"], context);
        }

        private static Task<CommandResult> DoSleep(string level, CommandContext context)
        {
            context.SystemContext.EventBus.PublishSignal("force-sleep", level);
            return Task.FromResult(CommandResult.Ok($"已发送强制睡眠信号: {level}"));
        }

        private static Task<CommandResult> HandleApprove(string[] parts, CommandContext context)
        {
            if (parts.Length < 2)
                return Task.FromResult(CommandResult.Fail("用法: /sleep approve <requestId>"));

            var requestId = parts[1];
            context.SystemContext.EventBus.PublishSignal("sleep-approve", requestId);
            return Task.FromResult(CommandResult.Ok($"睡觉请求 {requestId} 已批准"));
        }

        private static Task<CommandResult> HandleDeny(string[] parts, CommandContext context)
        {
            if (parts.Length < 2)
                return Task.FromResult(CommandResult.Fail("用法: /sleep deny <requestId>"));

            var requestId = parts[1];
            context.SystemContext.EventBus.PublishSignal("sleep-deny", requestId);
            return Task.FromResult(CommandResult.Ok($"睡觉请求 {requestId} 已拒绝"));
        }
    }
}
