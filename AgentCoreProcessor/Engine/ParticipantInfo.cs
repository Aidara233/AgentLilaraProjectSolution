using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 话题参与者信息。TopicEngine 维护，通过 ActivationBatch 快照传递给 WorkerEngine。
    /// </summary>
    internal sealed class ParticipantInfo
    {
        public required string DisplayName { get; init; }
        public required string Nickname { get; init; }
        public required string PlatformId { get; init; }

        public static ParticipantInfo From(User user, IncomingMessage msg)
        {
            var display = !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                        : !string.IsNullOrEmpty(msg.DisplayName) ? msg.DisplayName
                        : msg.PlatformUserId;
            return new ParticipantInfo
            {
                DisplayName = display,
                Nickname = msg.Nickname ?? "",
                PlatformId = msg.PlatformUserId
            };
        }
    }
}
