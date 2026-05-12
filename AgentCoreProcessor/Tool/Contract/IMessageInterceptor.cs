using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 系统级睡眠状态。插件可据此决定行为。
    /// </summary>
    public enum SleepState
    {
        None,
        Daydream,
        Nap,
        DeepSleep,
    }

    /// <summary>
    /// 消息拦截器。插件实现此接口可在引擎处理消息前介入决策。
    /// 按 Priority 升序执行，首个非 Continue 结果短路后续拦截器。
    /// </summary>
    public interface IMessageInterceptor
    {
        int Priority { get; }
        Task<InterceptResult> OnBeforeProcessAsync(MessageInterceptContext context);
    }

    public enum InterceptAction
    {
        /// <summary>不干预，继续正常流程。可附加 PromptInjection。</summary>
        Continue,
        /// <summary>跳过本轮处理（沉默）。消息已入库但不生成回复。</summary>
        Skip,
        /// <summary>拦截器已自行处理完毕（如发送了梦话），引擎不再处理。</summary>
        Handled
    }

    public class InterceptResult
    {
        public InterceptAction Action { get; init; }
        public string? PromptInjection { get; init; }

        public static InterceptResult Continue(string? injection = null) =>
            new() { Action = InterceptAction.Continue, PromptInjection = injection };
        public static InterceptResult Skip() =>
            new() { Action = InterceptAction.Skip };
        public static InterceptResult Handled() =>
            new() { Action = InterceptAction.Handled };
    }

    public class MessageInterceptContext
    {
        public required SleepState SleepState { get; init; }
        public required IReadOnlyList<MessageInfo> Messages { get; init; }
        public required int ChannelId { get; init; }
        public required bool IsPrivate { get; init; }
        public required bool HasMention { get; init; }
        public required IToolContext ToolContext { get; init; }
    }

    public class MessageInfo
    {
        public required string Content { get; init; }
        public required string SenderName { get; init; }
        public required int PersonId { get; init; }
        public required bool IsMentioned { get; init; }
        public required bool IsPrivate { get; init; }
        public required int PermissionLevel { get; init; }
    }
}
