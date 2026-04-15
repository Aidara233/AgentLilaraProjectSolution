using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 手动触发睡眠。交互式示例命令。
    /// 用法: /sleep [daydream|nap|deepsleep]
    /// 无参数时进入交互选择。
    /// </summary>
    internal class SleepCommand : IInteractiveCommand
    {
        public string Name => "sleep";
        public string Description => "手动触发睡眠 (用法: /sleep [daydream|nap|deepsleep])";
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
            var level = args.Trim().ToLowerInvariant();
            if (!LevelOptions.Contains(level))
                return Task.FromResult(CommandResult.Fail(
                    $"未知等级: {level}，可选: {string.Join(" / ", LevelOptions)}"));

            return DoSleep(level, context);
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
    }
}
